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
        int tableSize = (int)GetSystemFirmwareTable(Signature, 0, null, 0);
        if (tableSize == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        var rawTable = new byte[tableSize];
        if (GetSystemFirmwareTable(Signature, 0, rawTable, (uint)tableSize) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return Parse(rawTable);
    }

    // raw — структура Windows RawSMBIOSData: 8 байт заголовка (в байтах 4–7 длина таблицы) и сама таблица.
    public static List<ModuleMemoryModel> Parse(byte[] rawTable)
    {
        var modules = new List<ModuleMemoryModel>();
        int tableEnd = (int)Math.Min(8L + BitConverter.ToUInt32(rawTable, 4), rawTable.Length);
        for (int recordStart = 8; recordStart + 4 <= tableEnd && rawTable[recordStart] != EndOfTable;)
        {
            int formattedLength = rawTable[recordStart + 1], stringsStart = recordStart + formattedLength;
            if (formattedLength < 4 || stringsStart > tableEnd)
                break; // испорченная запись: дальше таблицу разобрать нельзя
            // Набор строк заканчивается двумя нулями подряд; без строк это просто два нулевых байта.
            int stringsEnd = stringsStart;
            while (stringsEnd + 1 < tableEnd && (rawTable[stringsEnd] != 0 || rawTable[stringsEnd + 1] != 0))
                stringsEnd++;
            if (rawTable[recordStart] == MemoryDevice && formattedLength >= 0x1B
                && ParseMemoryDevice(rawTable[recordStart..stringsStart], Encoding.ASCII.GetString(rawTable, stringsStart, Math.Min(stringsEnd + 1, tableEnd) - stringsStart).Split('\0'), modules.Count + 1) is { } parsedModule)
                modules.Add(parsedModule);
            recordStart = stringsEnd + 2;
        }
        return modules;
    }

    // Смещения полей записи типа 17 — по спецификации SMBIOS. Короткая запись (старая версия BIOS) не содержит
    // последних полей, они остаются нулевыми. Пустой слот имеет размер 0 и в список не попадает.
    private static ModuleMemoryModel? ParseMemoryDevice(byte[] record, string[] recordStrings, int moduleNumber)
    {
        ushort ReadWord(int offset) => offset + 2 <= record.Length ? BitConverter.ToUInt16(record, offset) : (ushort)0;
        string ReadString(int offset)
        {
            int stringIndex = offset < record.Length ? record[offset] : 0;
            var value = stringIndex > 0 && stringIndex <= recordStrings.Length ? recordStrings[stringIndex - 1].Trim() : "";
            return value.Length == 0 ? ModuleMemoryModel.NoData : value;
        }

        // Размер — 15 бит в МБ (или в КБ, если старший бит равен 1); значение 0x7FFF означает «смотри расширенное поле».
        ushort sizeField = ReadWord(0x0C);
        long megabytes = sizeField switch
        {
            0 or 0xFFFF => 0,
            0x7FFF => ReadWord(0x1C) | (long)(ReadWord(0x1E) & 0x7FFF) << 16,
            _ => (sizeField & 0x8000) != 0 ? (sizeField & 0x7FFF) / 1024 : sizeField
        };
        if (megabytes == 0)
            return null;
        // Скорость в MT/s, напряжение в милливольтах.
        int speedMTs = ReadWord(0x20), voltageMillivolts = ReadWord(0x26);
        return new ModuleMemoryModel
        {
            Number = moduleNumber,
            Size_GB = (int)(megabytes >> 10),
            RAM_maker = ReadString(0x17),
            RAM_partNumber = ReadString(0x1A),
            RAM_serialNumber = ReadString(0x18),
            RAM_type = record[0x12] switch { 24 => "DDR3", 26 => "DDR4", 30 => "LPDDR4", 34 => "DDR5", 35 => "LPDDR5", _ => ModuleMemoryModel.NoData },
            RAM_formFactor = record[0x0E] switch { 9 => "DIMM", 13 => "SO-DIMM", _ => ModuleMemoryModel.NoData },
            // На многих платах слоты разных каналов называются одинаково, поэтому добавляем банк.
            RAM_slot = $"{ReadString(0x11)} · {ReadString(0x10)}",
            ConfiguredSpeedMTs = speedMTs > 0 ? speedMTs : null,
            RAM_speed = speedMTs > 0 ? $"{speedMTs} MT/s ({speedMTs / 2} МГц)" : ModuleMemoryModel.NoData,
            RAM_voltage = voltageMillivolts > 0 ? $"{voltageMillivolts / 1000m:0.00} В" : ModuleMemoryModel.NoData
        };
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetSystemFirmwareTable(uint signature, uint tableId, byte[]? buffer, uint bufferSize);
}
