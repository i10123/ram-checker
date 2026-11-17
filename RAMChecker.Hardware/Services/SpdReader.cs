using System.Buffers.Binary;
using System.Text;

namespace RAMChecker.Services;

public record SpdInfo(byte Address, int NominalSpeed, int CL, int RCD, int RP, int RAS, int RC, int SizeGB)
{
    // Серийный номер и модель планки — по ним паспорт сопоставляется с модулем из SMBIOS.
    public string Serial { get; init; } = "";
    public string Part { get; init; } = "";
}

// Читает паспорт планок DDR4 и DDR5 из микросхемы SPD по шине SMBus (стандарт JEDEC): тайминги, модель и серийный номер.
public sealed class SpdReader : IDisposable
{
    private const long Write = 0, Read = 1, Byte = 1, ByteData = 2;
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

    // Адреса планок DDR5: у них нет страниц, которые переключаются командами по адресам 0x36 и 0x37.
    private readonly HashSet<byte> ddr5 = [];

    public List<SpdInfo> ReadAll() => Locked(() =>
    {
        var result = new List<SpdInfo>();
        ddr5.Clear();
        // Микросхемы SPD восьми слотов отвечают по адресам 0x50–0x57. Хаб SPD5118 (DDR5) в первых двух регистрах
        // хранит тип устройства 0x51 0x18; у DDR4 в этих байтах другие значения.
        for (byte address = 0x50; address <= 0x57; address++)
        {
            if (ReadBytes(address, 0, 2) is [0x51, 0x18])
            {
                if (ReadDdr5(address) is { } hub && ddr5.Add(address))
                    result.Add(hub);
            }
            else if (ReadBytes(address, 0, 128) is { } bytes && Decode(address, bytes) is { } info)
                result.Add(info);
        }
        // Память SPD DDR4 разбита на две страницы по 256 байт; серийный номер (байты 325–328) и модель (329–348)
        // лежат во второй. Команда по адресу 0x37 переключает на вторую страницу сразу все микросхемы на шине,
        // по адресу 0x36 — обратно на первую. Команды посылаются только после того, как первая страница подтвердила
        // DDR4: у микросхем SPD DDR3 эти адреса включают защиту от записи. Некоторые микросхемы не подтверждают
        // переключение, поэтому результат команды не проверяется — чужие данные просто не совпадут с SMBIOS.
        if (result.Count > ddr5.Count)
        {
            SetPage(1);
            for (int k = 0; k < result.Count; k++)
                if (!ddr5.Contains(result[k].Address) && ReadBytes(result[k].Address, 325 - 256, 24) is { } id)
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

    // У хаба SPD5118 адреса до 0x80 — служебные регистры MR0–MR127, а по адресам 0x80–0xFF открыта одна из восьми
    // страниц паспорта по 128 байт; номер страницы записывается в регистр MR11. Паспорт DDR5 занимает 1024 байта:
    // контрольная сумма покрывает байты 0–509, а серийный номер и модель лежат в байтах 517–550, то есть нужны пять страниц.
    private SpdInfo? ReadDdr5(byte address)
    {
        var bytes = new byte[640];
        try
        {
            for (int page = 0; page < 5; page++)
            {
                if (!WriteRegister(address, 11, page) || ReadBytes(address, 0x80, 128) is not { } part)
                    return null;
                part.CopyTo(bytes, page * 128);
            }
        }
        finally { WriteRegister(address, 11, 0); } // Другие программы ждут, что выбрана первая страница.
        return DecodeDdr5(address, bytes) is { } info
            ? info with { Serial = Convert.ToHexString(bytes, 517, 4), Part = Encoding.ASCII.GetString(bytes, 521, 30).Trim(' ', '\0') }
            : null;
    }

    private bool WriteRegister(byte address, int register, int value) =>
        smbus.Execute("ioctl_smbus_xfer", [address, Write, register, ByteData, value], new long[1]);

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
        int aa = Clocks(Ps(b[24], 123), tCK), rcd = Clocks(Ps(b[25], 122), tCK), rp = Clocks(Ps(b[26], 121), tCK);
        int ras = Clocks(((b[27] & 0x0F) << 8 | b[28]) * 125, tCK), rc = Clocks(Ps((b[27] >> 4) << 8 | b[29], 120), tCK);
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
        // у сборок 3DS ещё × число кристаллов в корпусе. Нужен, чтобы сверить паспорт с модулем из SMBIOS.
        int density = b[4] & 0x0F, densityMb = density < 8 ? 256 << density : density == 8 ? 12288 : 24576;
        int dies = (b[6] & 3) == 2 ? (b[6] >> 4 & 7) + 1 : 1;
        int sizeGB = densityMb / 8 * (8 << (b[13] & 7)) / (4 << (b[12] & 7)) * ((b[12] >> 3 & 7) + 1) * dies / 1024;
        // Скорости DDR4 идут с шагом 133⅓ MT/s, а tCK в паспорте округлён до пикосекунды: 0,938 нс дало бы 2132,
        // а не 2133. Поэтому скорость округляется до ближайшего шага, а дробная часть отбрасывается (2666,67 → 2666).
        int speed = (int)(Math.Round(2_000_000d / tCK * 3 / 400) * 400 / 3);
        return new(address, speed, cl, rcd, rp, ras, rc, sizeGB);
    }

    // Паспорт DDR5 (стандарт JESD400-5): 1024 байта, но для таймингов достаточно первых 512. Время хранится
    // в пикосекундах двумя байтами (младший первым), поэтому поправки в отдельных байтах, как у DDR4, не нужны.
    public static SpdInfo? DecodeDdr5(byte address, byte[] b)
    {
        // Байт 2 — тип памяти (0x12 — DDR5); байты 0–509 защищены той же контрольной суммой, что и у DDR4.
        if (b.Length < 512 || b[2] != 0x12 || Crc16(b, 510) != (b[510] | b[511] << 8))
            return null;
        int Ps(int offset) => b[offset] | b[offset + 1] << 8;
        int tCK = Ps(20);
        // Стандартные скорости DDR5 — от 3200 до 8800 MT/s (tCK от 0,625 до 0,227 нс), берём с запасом.
        // Плотность 1–8 — от 4 до 64 Гбит, корпус 0–5, ширина 0–3 — x4–x32, ширина шины 0–3 — 8–64 бита.
        int density = b[4] & 0x1F, package = b[4] >> 5, width = b[6] >> 5;
        if (tCK is < 200 or > 700 || density is 0 or > 8 || package > 5 || width > 3 || (b[235] & 7) > 3)
            return null;
        int aa = Clocks(Ps(30), tCK), rcd = Clocks(Ps(32), tCK), rp = Clocks(Ps(34), tCK), ras = Clocks(Ps(36), tCK), rc = Clocks(Ps(38), tCK);
        // Байты 24–28 — список поддерживаемых CL: бит 0 соответствует CL20, каждый следующий бит добавляет 2.
        // Берётся наименьшая поддерживаемая задержка не меньше рассчитанной; если такой нет, паспорт неверен.
        long supported = b[24] | (long)b[25] << 8 | (long)b[26] << 16 | (long)b[27] << 24 | (long)b[28] << 32;
        int bit = Math.Max(0, (aa - 20 + 1) / 2);
        while (bit < 40 && (supported >> bit & 1) == 0)
            bit++;
        if (bit >= 40 || new[] { aa, rcd, rp, ras, rc }.Min() <= 0)
            return null;
        // Объём: плотность кристалла × число кристаллов на шине (ширина шины всех подканалов / ширина кристалла) × ранги;
        // у сборок 3DS ещё × число кристаллов в стеке. У DDP каждый кристалл — отдельный ранг, он уже учтён в рангах.
        int[] gigabits = [0, 4, 8, 12, 16, 24, 32, 48, 64];
        int subChannels = (b[235] >> 5) + 1, busBits = 8 << (b[235] & 7), ranks = (b[234] >> 3 & 7) + 1;
        int dies = package >= 2 ? 1 << (package - 1) : 1;
        int sizeGB = gigabits[density] * subChannels * busBits / (4 << width) * ranks * dies / 8;
        // Скорости DDR5 идут с шагом 200 MT/s, а tCK в паспорте округлён до пикосекунды: 0,416 нс дало бы 4807, а не 4800.
        int speed = (int)(Math.Round(2_000_000d / tCK / 200) * 200);
        return new(address, speed, 20 + 2 * bit, rcd, rp, ras, rc, sizeGB);
    }

    // Перевод времени в пикосекундах в такты по правилу округления JEDEC.
    private static int Clocks(int ps, int tCK) => (ps * 1000 / tCK + 974) / 1000;

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
