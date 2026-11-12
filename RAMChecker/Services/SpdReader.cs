using System.Buffers.Binary;
using System.Text;

namespace RAMChecker.Services;

public record SpdInfo(byte Address, int NominalSpeed, int CL, int RCD, int RP, int RAS, int RC, int SizeGB, bool HasSensor)
{
    // Серийный номер и модель планки — по ним паспорт сопоставляется с модулем из WMI.
    public string Serial { get; init; } = "";
    public string Part { get; init; } = "";
}

// Читает паспорт планок DDR4 из микросхемы SPD по шине SMBus (стандарт JEDEC): первые 128 байт и модель с серийным номером.
public sealed class SpdReader : IDisposable
{
    private const long Write = 0, Read = 1, Byte = 1, ByteData = 2, WordData = 3;
    private readonly PawnIO smbus;
    // Общее имя блокировки шины, которое используют и другие программы мониторинга.
    private readonly Mutex busLock;

    public SpdReader()
    {
        // Модуль PIIX4 подходит для AMD, I801 — для Intel. При загрузке модуль ищет свой контроллер SMBus и без него
        // не загружается (pawnio_load возвращает ошибку) — тогда пробуем второй. Отсутствие самого драйвера не перехватываем.
        bool amd = true;
        try
        {
            smbus = new PawnIO("SmbusPIIX4.bin");
        }
        catch (Exception ex) when (ex is not DllNotFoundException)
        {
            smbus = new PawnIO("SmbusI801.bin");
            amd = false;
        }
        try
        {
            busLock = new(false, @"Global\Access_SMBUS.HTP.Method");
        }
        catch
        {
            smbus.Dispose(); // Блокировку создать не удалось — драйвер не должен остаться открытым.
            throw;
        }
        // На AMD микросхемы SPD подключены к порту 0. Другая программа могла переключить порт, поэтому он выбирается
        // явно и, как требует описание модуля, под блокировкой шины. Ошибка значит, что контроллер занят или неисправен.
        if (amd && !Locked(() => smbus.Execute("ioctl_piix4_port_sel", [0], new long[1])))
        {
            Dispose();
            throw new InvalidOperationException("не удалось выбрать порт SMBus с микросхемами SPD.");
        }
    }

    public List<SpdInfo> ReadAll() => Locked(() =>
    {
        var result = new List<SpdInfo>();
        // Микросхемы SPD восьми слотов отвечают по адресам 0x50–0x57.
        for (byte address = 0x50; address <= 0x57; address++)
            if (ReadBytes(address, 0, 128) is { } bytes && Decode(address, bytes) is { } info)
                result.Add(info);
        // Память SPD DDR4 разбита на две страницы по 256 байт; серийный номер (байты 325–328) и модель (329–348)
        // лежат во второй. Команда по адресу 0x37 переключает на вторую страницу сразу все микросхемы на шине,
        // по адресу 0x36 — обратно на первую. Команды посылаются только после того, как первая страница подтвердила
        // DDR4: у микросхем SPD DDR3 эти адреса включают защиту от записи. Некоторые микросхемы не подтверждают
        // переключение, поэтому результат команды не проверяется — чужие данные просто не совпадут с WMI.
        if (result.Count > 0)
        {
            SetPage(1);
            for (int k = 0; k < result.Count; k++)
                if (ReadBytes(result[k].Address, 325 - 256, 24) is { } id)
                    result[k] = result[k] with { Serial = Convert.ToHexString(id, 0, 4), Part = Encoding.ASCII.GetString(id, 4, 20).Trim(' ', '\0') };
            SetPage(0); // Другие программы и BIOS ждут, что выбрана первая страница.
        }
        return result;
    }) ?? [];

    private byte[]? ReadBytes(byte address, int start, int count)
    {
        var bytes = new byte[count];
        for (int i = 0; i < count; i++)
        {
            int value = Transfer(address, start + i, ByteData);
            if (value < 0)
                return null;
            bytes[i] = (byte)value;
        }
        return bytes;
    }

    // Команда без данных: сам адрес 0x36 или 0x37 выбирает страницу, передаваемый байт микросхема не читает.
    private void SetPage(int page) => smbus.Execute("ioctl_smbus_xfer", [0x36 + page, Write, 0, Byte, 0], new long[1]);

    // Датчик температуры модуля (TSOD) находится по адресу 0x18 + номер слота, регистр 5.
    public double? Temperature(byte spdAddress) => Locked<double?>(() =>
    {
        int word = Transfer(spdAddress - 0x50 + 0x18, 5, WordData);
        if (word < 0)
            return null;
        // Датчик передаёт старший байт первым; значение — 13 бит со знаком в шагах 0,0625 °C.
        int raw = ((word & 0xFF) << 8 | word >> 8) & 0x1FFF;
        return (raw >= 0x1000 ? raw - 0x2000 : raw) / 16.0;
    });

    public static SpdInfo? Decode(byte address, byte[] b)
    {
        if (b.Length < 128 || b[2] != 0x0C || Crc16(b, 126) != (b[126] | b[127] << 8))
            return null;
        // Контрольная сумма защищает только от сбоев чтения, поэтому сами значения тоже проверяются.
        // Время хранится в шагах 0,125 нс плюс поправка в пикосекундах (байт со знаком).
        int Ps(int coarse, int fineIndex) => coarse * 125 + (sbyte)b[fineIndex];
        int tCK = Ps(b[18], 125);
        // DDR4 по стандарту работает от 1600 до 3200 MT/s, то есть tCK от 0,625 до 1,25 нс (берём с запасом до 1,5 нс).
        // Другое значение — испорченный паспорт; заодно исключено деление на ноль. Коды ширины и ёмкости выше 3 и 9 зарезервированы.
        if (tCK is < 625 or > 1500 || (b[12] & 7) > 3 || (b[13] & 7) > 3 || (b[4] & 0x0F) > 9)
            return null;
        // Перевод наносекунд в такты по правилу округления JEDEC.
        int Clocks(int ps) => (ps * 1000 / tCK + 974) / 1000;
        int aa = Clocks(Ps(b[24], 123)), rcd = Clocks(Ps(b[25], 122)), rp = Clocks(Ps(b[26], 121));
        int ras = Clocks(((b[27] & 0x0F) << 8 | b[28]) * 125), rc = Clocks(Ps((b[27] >> 4) << 8 | b[29], 120));
        // Байты 20–23 — список поддерживаемых CL: бит 0 соответствует CL7, а при флаге в старшем бите байта 23 — CL23.
        // Берётся наименьшая поддерживаемая задержка не меньше рассчитанной; если такой нет, паспорт неверен.
        int first = (b[23] & 0x80) != 0 ? 23 : 7;
        uint supported = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(20)) & 0x3FFFFFFF;
        int cl = Math.Max(first, aa);
        while (cl < first + 30 && (supported >> (cl - first) & 1) == 0)
            cl++;
        // ">=": расчётная задержка могла с самого начала оказаться выше списка, и тогда цикл не выполнялся ни разу.
        if (cl >= first + 30 || new[] { aa, rcd, rp, ras, rc }.Min() <= 0)
            return null;
        // Объём планки: ёмкость кристалла × число кристаллов на шине (ширина шины / ширина кристалла) × ранги,
        // у сборок 3DS ещё × число кристаллов в корпусе. Нужен, чтобы сверить паспорт с модулем из WMI.
        int density = b[4] & 0x0F, densityMb = density < 8 ? 256 << density : density == 8 ? 12288 : 24576;
        int dies = (b[6] & 3) == 2 ? (b[6] >> 4 & 7) + 1 : 1;
        int sizeGB = densityMb / 8 * (8 << (b[13] & 7)) / (4 << (b[12] & 7)) * ((b[12] >> 3 & 7) + 1) * dies / 1024;
        // Скорости DDR4 идут с шагом 133⅓ MT/s, а tCK в паспорте округлён до пикосекунды: 0,938 нс дало бы 2132,
        // а не 2133. Поэтому скорость округляется до ближайшего шага, а дробная часть отбрасывается (2666,67 → 2666).
        int speed = (int)(Math.Round(2_000_000d / tCK * 3 / 400) * 400 / 3);
        return new(address, speed, cl, rcd, rp, ras, rc, sizeGB, (b[14] & 0x80) != 0);
    }

    // Контрольная сумма JEDEC защищает от ошибок чтения по шине.
    public static int Crc16(byte[] data, int count)
    {
        int crc = 0;
        for (int i = 0; i < count; i++)
        {
            crc ^= data[i] << 8;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x8000) != 0 ? crc << 1 ^ 0x1021 : crc << 1;
        }
        return crc & 0xFFFF;
    }

    private int Transfer(int address, int register, long size)
    {
        var output = new long[1];
        return smbus.Execute("ioctl_smbus_xfer", [address, Read, register, size], output) ? (int)(output[0] & 0xFFFF) : -1;
    }

    private T? Locked<T>(Func<T> action)
    {
        try
        {
            if (!busLock.WaitOne(500))
                return default;
        }
        catch (AbandonedMutexException) { }
        try
        {
            return action();
        }
        finally { busLock.ReleaseMutex(); }
    }

    public void Dispose()
    {
        smbus.Dispose();
        busLock.Dispose();
    }
}
