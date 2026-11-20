using System.Buffers.Binary;
using System.Text;

namespace RAMChecker.Services;

public record SpdInfo(byte Address, int NominalSpeed, int CasLatency, int RasToCasDelay, int RowPrecharge, int RowActiveTime, int RowCycleTime, int SizeGB)
{
    // Серийный номер и модель планки — по ним паспорт сопоставляется с модулем из SMBIOS.
    public string SerialNumber { get; init; } = "";
    public string PartNumber { get; init; } = "";
}

// Читает паспорт планок DDR4 и DDR5 из микросхемы SPD по шине SMBus (стандарт JEDEC): тайминги, модель и серийный номер.
public sealed class SpdReader : IDisposable
{
    private const long SmbusWrite = 0, SmbusRead = 1, ByteTransfer = 1, ByteDataTransfer = 2;
    private readonly PawnIO smbusDriver;
    // Общее имя блокировки шины, которое используют и другие программы мониторинга.
    private readonly Mutex busLock;

    public SpdReader()
    {
        // Модуль PIIX4 подходит для AMD, I801 — для Intel. При загрузке модуль ищет свой контроллер SMBus и без него
        // не загружается (pawnio_load возвращает ошибку) — тогда пробуем второй. Отсутствие самого драйвера не перехватываем.
        bool isAmdController = true;
        try
        {
            smbusDriver = new PawnIO("SmbusPIIX4.bin");
        }
        catch (Exception exception) when (exception is not DllNotFoundException)
        {
            smbusDriver = new PawnIO("SmbusI801.bin");
            isAmdController = false;
        }
        try
        {
            busLock = new(false, @"Global\Access_SMBUS.HTP.Method");
        }
        catch
        {
            smbusDriver.Dispose(); // Блокировку создать не удалось — драйвер не должен остаться открытым.
            throw;
        }
        // На AMD микросхемы SPD подключены к порту 0. Другая программа могла переключить порт, поэтому он выбирается
        // явно и, как требует описание модуля, под блокировкой шины. Ошибка значит, что контроллер занят или неисправен.
        try
        {
            if (isAmdController && !RunWithBusLock(() => smbusDriver.Execute("ioctl_piix4_port_sel", [0], new long[1])))
                throw new InvalidOperationException("не удалось выбрать порт SMBus с микросхемами SPD.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public List<SpdInfo> ReadAll() => RunWithBusLock(() =>
    {
        var result = new List<SpdInfo>();
        var ddr4ResultIndexes = new List<int>(); // номера паспортов DDR4 в result: только у них страницы переключаются по адресам 0x36 и 0x37
        // Микросхемы SPD восьми слотов отвечают по адресам 0x50–0x57. Хаб SPD5118 (DDR5) в первых двух регистрах
        // хранит тип устройства 0x51 0x18; у DDR4 в этих байтах другие значения.
        for (byte address = 0x50; address <= 0x57; address++)
        {
            if (ReadBytes(address, 0, 128) is not { } spdBytes)
                continue;
            if (spdBytes is [0x51, 0x18, ..])
            {
                if (ReadDdr5(address) is { } ddr5Info)
                    result.Add(ddr5Info);
            }
            else if (DecodeDdr4(address, spdBytes) is { } info)
            {
                ddr4ResultIndexes.Add(result.Count);
                result.Add(info);
            }
        }
        // Память SPD DDR4 разбита на две страницы по 256 байт; серийный номер (байты 325–328) и модель (329–348)
        // лежат во второй. Команда по адресу 0x37 переключает на вторую страницу сразу все микросхемы на шине,
        // по адресу 0x36 — обратно на первую. Команды посылаются только после того, как первая страница подтвердила
        // DDR4: у микросхем SPD DDR3 эти адреса включают защиту от записи. Некоторые микросхемы не подтверждают
        // переключение, поэтому результат команды не проверяется — чужие данные просто не совпадут с SMBIOS.
        if (ddr4ResultIndexes.Count > 0)
        {
            SetPage(1);
            foreach (int resultIndex in ddr4ResultIndexes)
                if (ReadBytes(result[resultIndex].Address, 325 - 256, 24) is { } identityBytes)
                    result[resultIndex] = result[resultIndex] with { SerialNumber = Convert.ToHexString(identityBytes, 0, 4), PartNumber = Encoding.ASCII.GetString(identityBytes, 4, 20).Trim(' ', '\0') };
            SetPage(0); // Другие программы и BIOS ждут, что выбрана первая страница.
        }
        return result;
    });

    private byte[]? ReadBytes(byte address, int firstRegister, int byteCount)
    {
        var readBuffer = new byte[byteCount];
        for (int offset = 0; offset < byteCount; offset++)
        {
            var output = new long[1];
            if (!smbusDriver.Execute("ioctl_smbus_xfer", [address, SmbusRead, firstRegister + offset, ByteDataTransfer], output))
                return null;
            readBuffer[offset] = (byte)output[0];
        }
        return readBuffer;
    }

    // Команда без данных: сам адрес 0x36 или 0x37 выбирает страницу, передаваемый байт микросхема не читает.
    private void SetPage(int page) => smbusDriver.Execute("ioctl_smbus_xfer", [0x36 + page, SmbusWrite, 0, ByteTransfer, 0], new long[1]);

    // У хаба SPD5118 адреса до 0x80 — служебные регистры MR0–MR127, а по адресам 0x80–0xFF открыта одна из восьми
    // страниц паспорта по 128 байт; номер страницы записывается в регистр MR11. Паспорт DDR5 занимает 1024 байта:
    // контрольная сумма покрывает байты 0–509, а серийный номер и модель лежат в байтах 517–550, то есть нужны пять страниц.
    private SpdInfo? ReadDdr5(byte address)
    {
        var spdBytes = new byte[640];
        try
        {
            for (int page = 0; page < 5; page++)
            {
                if (!WriteRegister(address, 11, page) || ReadBytes(address, 0x80, 128) is not { } pageBytes)
                    return null;
                pageBytes.CopyTo(spdBytes, page * 128);
            }
        }
        finally { WriteRegister(address, 11, 0); } // Другие программы ждут, что выбрана первая страница.
        return DecodeDdr5(address, spdBytes) is { } info
            ? info with { SerialNumber = Convert.ToHexString(spdBytes, 517, 4), PartNumber = Encoding.ASCII.GetString(spdBytes, 521, 30).Trim(' ', '\0') }
            : null;
    }

    private bool WriteRegister(byte address, int register, int value) =>
        smbusDriver.Execute("ioctl_smbus_xfer", [address, SmbusWrite, register, ByteDataTransfer, value], new long[1]);

    public static SpdInfo? DecodeDdr4(byte address, byte[] spd)
    {
        if (spd.Length < 128 || spd[2] != 0x0C || Crc16(spd, 126) != (spd[126] | spd[127] << 8))
            return null;
        // Контрольная сумма защищает только от сбоев чтения, поэтому сами значения тоже проверяются.
        // Время хранится в шагах 0,125 нс плюс поправка в пикосекундах (байт со знаком).
        int ToPicoseconds(int coarseSteps, int fineCorrectionIndex) => coarseSteps * 125 + (sbyte)spd[fineCorrectionIndex];
        int clockPeriod = ToPicoseconds(spd[18], 125);
        // DDR4 по стандарту работает от 1600 до 3200 MT/s, то есть tCK от 0,625 до 1,25 нс (берём с запасом до 1,5 нс).
        // Другое значение — испорченный паспорт; заодно исключено деление на ноль. Коды ширины и ёмкости выше 3 и 9 зарезервированы.
        if (clockPeriod is < 625 or > 1500 || (spd[12] & 7) > 3 || (spd[13] & 7) > 3 || (spd[4] & 0x0F) > 9)
            return null;
        int minimumCasClocks = PicosecondsToClocks(ToPicoseconds(spd[24], 123), clockPeriod), rasToCasDelay = PicosecondsToClocks(ToPicoseconds(spd[25], 122), clockPeriod), rowPrecharge = PicosecondsToClocks(ToPicoseconds(spd[26], 121), clockPeriod);
        int rowActiveTime = PicosecondsToClocks(((spd[27] & 0x0F) << 8 | spd[28]) * 125, clockPeriod), rowCycleTime = PicosecondsToClocks(ToPicoseconds((spd[27] >> 4) << 8 | spd[29], 120), clockPeriod);
        // Байты 20–23 — список поддерживаемых CL: бит 0 соответствует CL7, а при флаге в старшем бите байта 23 — CL23.
        // Берётся наименьшая поддерживаемая задержка не меньше рассчитанной; если такой нет, паспорт неверен.
        int firstListedCas = (spd[23] & 0x80) != 0 ? 23 : 7;
        uint supportedCasMask = BinaryPrimitives.ReadUInt32LittleEndian(spd.AsSpan(20)) & 0x3FFFFFFF;
        int casLatency = Math.Max(firstListedCas, minimumCasClocks);
        while (casLatency < firstListedCas + 30 && (supportedCasMask >> (casLatency - firstListedCas) & 1) == 0)
            casLatency++;
        // ">=": расчётная задержка могла с самого начала оказаться выше списка, и тогда цикл не выполнялся ни разу.
        if (casLatency >= firstListedCas + 30 || new[] { minimumCasClocks, rasToCasDelay, rowPrecharge, rowActiveTime, rowCycleTime }.Min() <= 0)
            return null;
        // Объём планки: ёмкость кристалла × число кристаллов на шине (ширина шины / ширина кристалла) × ранги,
        // у сборок 3DS ещё × число кристаллов в корпусе. Нужен, чтобы сверить паспорт с модулем из SMBIOS.
        int densityCode = spd[4] & 0x0F, densityMegabits = densityCode < 8 ? 256 << densityCode : densityCode == 8 ? 12288 : 24576;
        int diesPerPackage = (spd[6] & 3) == 2 ? (spd[6] >> 4 & 7) + 1 : 1;
        int sizeGB = densityMegabits / 8 * (8 << (spd[13] & 7)) / (4 << (spd[12] & 7)) * ((spd[12] >> 3 & 7) + 1) * diesPerPackage / 1024;
        // Скорости DDR4 идут с шагом 133⅓ MT/s, а tCK в паспорте округлён до пикосекунды: 0,938 нс дало бы 2132,
        // а не 2133. Поэтому скорость округляется до ближайшего шага, а дробная часть отбрасывается (2666,67 → 2666).
        int nominalSpeed = (int)(Math.Round(2_000_000d / clockPeriod * 3 / 400) * 400 / 3);
        return new(address, nominalSpeed, casLatency, rasToCasDelay, rowPrecharge, rowActiveTime, rowCycleTime, sizeGB);
    }

    // Паспорт DDR5 (стандарт JESD400-5): 1024 байта, но для таймингов достаточно первых 512. Время хранится
    // в пикосекундах двумя байтами (младший первым), поэтому поправки в отдельных байтах, как у DDR4, не нужны.
    public static SpdInfo? DecodeDdr5(byte address, byte[] spd)
    {
        // Байт 2 — тип памяти (0x12 — DDR5); байты 0–509 защищены той же контрольной суммой, что и у DDR4.
        if (spd.Length < 512 || spd[2] != 0x12 || Crc16(spd, 510) != (spd[510] | spd[511] << 8))
            return null;
        int ReadPicoseconds(int spdOffset) => spd[spdOffset] | spd[spdOffset + 1] << 8;
        int clockPeriod = ReadPicoseconds(20);
        // Стандартные скорости DDR5 — от 3200 до 8800 MT/s (tCK от 0,625 до 0,227 нс), берём с запасом.
        // Плотность 1–8 — от 4 до 64 Гбит, корпус 0–5, ширина 0–3 — x4–x32, ширина шины 0–3 — 8–64 бита.
        int densityCode = spd[4] & 0x1F, packageCode = spd[4] >> 5, deviceWidthCode = spd[6] >> 5;
        if (clockPeriod is < 200 or > 700 || densityCode is 0 or > 8 || packageCode > 5 || deviceWidthCode > 3 || (spd[235] & 7) > 3)
            return null;
        // Округление DDR5 по JESD400-5: время уменьшается на 0,3 %, потому что tCK в паспорте урезан до пикосекунды
        // (333 вместо 333⅓ у DDR5-6000), и без поправки 10 нс дали бы 31 такт вместо 30.
        int Ddr5Clocks(int spdOffset) => (ReadPicoseconds(spdOffset) * 997 / clockPeriod + 1000) / 1000;
        int minimumCasClocks = Ddr5Clocks(30), rasToCasDelay = Ddr5Clocks(32), rowPrecharge = Ddr5Clocks(34), rowActiveTime = Ddr5Clocks(36), rowCycleTime = Ddr5Clocks(38);
        // Байты 24–28 — список поддерживаемых CL: бит 0 соответствует CL20, каждый следующий бит добавляет 2.
        // Берётся наименьшая поддерживаемая задержка не меньше рассчитанной; если такой нет, паспорт неверен.
        long supportedCasMask = BinaryPrimitives.ReadInt64LittleEndian(spd.AsSpan(24)) & 0xFF_FFFF_FFFF;
        int casBitIndex = Math.Max(0, (minimumCasClocks - 20 + 1) / 2);
        while (casBitIndex < 40 && (supportedCasMask >> casBitIndex & 1) == 0)
            casBitIndex++;
        if (casBitIndex >= 40 || new[] { minimumCasClocks, rasToCasDelay, rowPrecharge, rowActiveTime, rowCycleTime }.Min() <= 0)
            return null;
        // Объём: плотность кристалла × число кристаллов на шине (ширина шины всех подканалов / ширина кристалла) × ранги;
        // у сборок 3DS ещё × число кристаллов в стеке. У DDP каждый кристалл — отдельный ранг, он уже учтён в рангах.
        int[] densityGigabits = [0, 4, 8, 12, 16, 24, 32, 48, 64];
        int subChannels = (spd[235] >> 5) + 1, busBits = 8 << (spd[235] & 7), ranks = (spd[234] >> 3 & 7) + 1;
        int diesPerPackage = packageCode >= 2 ? 1 << (packageCode - 1) : 1;
        int sizeGB = densityGigabits[densityCode] * subChannels * busBits / (4 << deviceWidthCode) * ranks * diesPerPackage / 8;
        // Скорости DDR5 идут с шагом 200 MT/s, а tCK в паспорте округлён до пикосекунды: 0,416 нс дало бы 4807, а не 4800.
        int nominalSpeed = (int)(Math.Round(2_000_000d / clockPeriod / 200) * 200);
        return new(address, nominalSpeed, 20 + 2 * casBitIndex, rasToCasDelay, rowPrecharge, rowActiveTime, rowCycleTime, sizeGB);
    }

    // Перевод времени в пикосекундах в такты по правилу округления JEDEC для DDR4.
    private static int PicosecondsToClocks(int picoseconds, int clockPeriod) => (picoseconds * 1000 / clockPeriod + 974) / 1000;

    // Контрольная сумма JEDEC защищает от ошибок чтения по шине.
    public static int Crc16(byte[] data, int count)
    {
        int crc = 0;
        for (int byteIndex = 0; byteIndex < count; byteIndex++)
        {
            crc ^= data[byteIndex] << 8;
            for (int bitIndex = 0; bitIndex < 8; bitIndex++)
                crc = (crc & 0x8000) != 0 ? crc << 1 ^ 0x1021 : crc << 1;
        }
        return crc & 0xFFFF;
    }

    private T RunWithBusLock<T>(Func<T> action)
    {
        try
        {
            // Шину держит другая программа (мониторинг, подсветка): это не то же самое, что отсутствие паспорта.
            if (!busLock.WaitOne(500))
                throw new TimeoutException("шина занята другой программой.");
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
        smbusDriver.Dispose();
        busLock.Dispose();
    }
}
