using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using RAMChecker.Models;

namespace RAMChecker.Services;

// Читает сведения о планках памяти из таблицы SMBIOS: BIOS кладёт её в память при загрузке компьютера.
// Таблица — последовательность записей: заголовок (тип, длина, номер), поля фиксированной длины и набор строк,
// который заканчивается двумя нулевыми байтами. Планке памяти соответствует запись типа 17 (Memory Device).
public static partial class SmbiosMemory
{
    private const uint Signature = 0x52534D42; // 'RSMB' — «сырая» таблица SMBIOS
    private const int MemoryDevice = 17, EndOfTable = 127;

    public static List<ModuleMemoryModel> Read()
    {
        // Первый вызов без буфера возвращает нужный размер, второй — заполняет буфер.
        int size = (int)GetSystemFirmwareTable(Signature, 0, null, 0);
        if (size == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        var raw = new byte[size];
        if (GetSystemFirmwareTable(Signature, 0, raw, (uint)size) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return Parse(raw);
    }

    // raw — структура Windows RawSMBIOSData: 8 байт заголовка (в байтах 4–7 длина таблицы) и сама таблица.
    public static List<ModuleMemoryModel> Parse(byte[] raw)
    {
        var modules = new List<ModuleMemoryModel>();
        int end = (int)Math.Min(8L + BitConverter.ToUInt32(raw, 4), raw.Length);
        for (int pos = 8; pos + 4 <= end && raw[pos] != EndOfTable;)
        {
            int formatted = raw[pos + 1], strings = pos + formatted;
            if (formatted < 4 || strings > end)
                break; // испорченная запись: дальше таблицу разобрать нельзя
            // Набор строк заканчивается двумя нулями подряд; без строк это просто два нулевых байта.
            int next = strings;
            while (next + 1 < end && (raw[next] != 0 || raw[next + 1] != 0))
                next++;
            if (raw[pos] == MemoryDevice && formatted >= 0x1B
                && Module(raw[pos..strings], Encoding.ASCII.GetString(raw, strings, Math.Min(next + 1, end) - strings).Split('\0'), modules.Count + 1) is { } module)
                modules.Add(module);
            pos = next + 2;
        }
        return modules;
    }

    // Смещения полей записи типа 17 — по спецификации SMBIOS. Короткая запись (старая версия BIOS) не содержит
    // последних полей, они остаются нулевыми. Пустой слот имеет размер 0 и в список не попадает.
    private static ModuleMemoryModel? Module(byte[] s, string[] strings, int number)
    {
        ushort Word(int offset) => offset + 2 <= s.Length ? BitConverter.ToUInt16(s, offset) : (ushort)0;
        string Text(int offset)
        {
            int index = offset < s.Length ? s[offset] : 0;
            var text = index > 0 && index <= strings.Length ? strings[index - 1].Trim() : "";
            return text.Length == 0 ? ModuleMemoryModel.NoData : text;
        }

        // Размер — 15 бит в МБ (или в КБ, если старший бит равен 1); значение 0x7FFF означает «смотри расширенное поле».
        ushort size = Word(0x0C);
        long megabytes = size switch
        {
            0 or 0xFFFF => 0,
            0x7FFF => Word(0x1C) | (long)(Word(0x1E) & 0x7FFF) << 16,
            _ => (size & 0x8000) != 0 ? (size & 0x7FFF) / 1024 : size
        };
        if (megabytes == 0)
            return null;
        // Скорость в MT/s, напряжение в милливольтах.
        int speed = Word(0x20), voltage = Word(0x26);
        return new ModuleMemoryModel
        {
            Number = number,
            size_GB = (int)(megabytes >> 10),
            RAM_maker = Text(0x17),
            RAM_partNumber = Text(0x1A),
            RAM_serialNumber = Text(0x18),
            RAM_type = s[0x12] switch { 24 => "DDR3", 26 => "DDR4", 30 => "LPDDR4", 34 => "DDR5", 35 => "LPDDR5", _ => ModuleMemoryModel.NoData },
            RAM_formFactor = s[0x0E] switch { 9 => "DIMM", 13 => "SO-DIMM", _ => ModuleMemoryModel.NoData },
            // На многих платах слоты разных каналов называются одинаково, поэтому добавляем банк.
            RAM_slot = $"{Text(0x11)} · {Text(0x10)}",
            RAM_speed = speed > 0 ? $"{speed} MT/s ({speed / 2} МГц)" : ModuleMemoryModel.NoData,
            RAM_voltage = voltage > 0 ? $"{voltage / 1000m:0.00} В" : ModuleMemoryModel.NoData
        };
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetSystemFirmwareTable(uint signature, uint id, byte[]? buffer, uint size);
}
