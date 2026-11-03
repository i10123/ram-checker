using System.Buffers.Binary;

namespace RAMChecker.Services;

public record SpdInfo(byte Address, int NominalSpeed, int CL, int RCD, int RP, int RAS, int RC, bool HasSensor);

// Читает паспорт планок DDR4 из микросхемы SPD по шине SMBus (стандарт JEDEC, первые 128 байт).
public sealed class SpdReader : IDisposable
{
    private const long Read = 1, ByteData = 2, WordData = 3;
    private readonly PawnIO smbus;
    // Общее имя блокировки шины, которое используют и другие программы мониторинга.
    private readonly Mutex busLock = new(false, @"Global\Access_SMBUS.HTP.Method");

    public SpdReader()
    {
        // Модуль PIIX4 подходит для AMD, I801 — для Intel; загрузится тот, который нашёл свой контроллер.
        try
        {
            smbus = new PawnIO("SmbusPIIX4.bin");
            smbus.Execute("ioctl_piix4_port_sel", [0], new long[1]);
        }
        catch
        {
            smbus = new PawnIO("SmbusI801.bin");
        }
    }

    public List<SpdInfo> ReadAll() => Locked(() =>
    {
        var result = new List<SpdInfo>();
        // Микросхемы SPD восьми слотов отвечают по адресам 0x50–0x57.
        for (byte address = 0x50; address <= 0x57; address++)
        {
            var bytes = new byte[128];
            int i = 0;
            for (int value; i < bytes.Length && (value = Transfer(address, i, ByteData)) >= 0; i++)
                bytes[i] = (byte)value;
            if (i == bytes.Length && Decode(address, bytes) is { } info)
                result.Add(info);
        }
        return result;
    }) ?? [];

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
        if (b[2] != 0x0C || Crc16(b, 126) != (b[126] | b[127] << 8))
            return null;
        // Время хранится в шагах 0,125 нс плюс поправка в пикосекундах (байт со знаком).
        int Ps(int coarse, int fineIndex) => coarse * 125 + (sbyte)b[fineIndex];
        int tCK = Ps(b[18], 125);
        // Перевод наносекунд в такты по правилу округления JEDEC.
        int Clocks(int ps) => (ps * 1000 / tCK + 974) / 1000;
        int cl = Clocks(Ps(b[24], 123));
        // Байты 20–23 — список поддерживаемых CL, бит 0 соответствует CL7.
        uint supported = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(20)) & 0x3FFFFFFF;
        while (cl < 37 && (supported >> (cl - 7) & 1) == 0)
            cl++;
        return new(address, 2_000_000 / tCK, cl, Clocks(Ps(b[25], 122)), Clocks(Ps(b[26], 121)),
            Clocks(((b[27] & 0x0F) << 8 | b[28]) * 125), Clocks(Ps((b[27] >> 4) << 8 | b[29], 120)), (b[14] & 0x80) != 0);
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
