namespace RAMChecker.Models
{
    // Характеристики одного модуля памяти: паспорт из SMBIOS, тайминги из SPD.
    public class ModuleMemoryModel
    {
        public const string NoData = "Нет данных";

        public int Number { get; set; }
        public int Size_GB { get; set; }
        public string RAM_maker { get; set; } = NoData;
        public string RAM_partNumber { get; set; } = NoData;
        public string RAM_serialNumber { get; set; } = NoData;
        public string RAM_type { get; set; } = NoData;
        public string RAM_formFactor { get; set; } = NoData;
        public string RAM_slot { get; set; } = NoData;
        public string RAM_voltage { get; set; } = NoData;

        // Настроенная в BIOS скорость из SMBIOS и номинальная из SPD, в MT/s.
        public string RAM_speed { get; set; } = NoData;
        public string RAM_nominalSpeed { get; set; } = NoData;

        // Тайминги CL-tRCD-tRP-tRAS и tRC в тактах по паспорту SPD.
        public string RAM_timings { get; set; } = NoData;
        public string RAM_tRC { get; set; } = NoData;

        public string DisplayName => $"Модуль {Number} · {RAM_maker} {RAM_partNumber}";
    }
}
