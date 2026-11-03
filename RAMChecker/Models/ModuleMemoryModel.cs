namespace RAMChecker.Models
{
    // Характеристики одного модуля памяти: паспорт из WMI, тайминги и температура из SPD.
    public class ModuleMemoryModel
    {
        public const string NetDannyh = "Нет данных";

        public int size_GB { get; set; }
        public string maker { get; set; } = NetDannyh;
        public string RAM_partNumber { get; set; } = NetDannyh;
        public string RAM_serialNumber { get; set; } = NetDannyh;
        public string RAM_type { get; set; } = NetDannyh;
        public string RAM_formFactor { get; set; } = NetDannyh;
        public string RAM_slot { get; set; } = NetDannyh;
        public string RAM_voltage { get; set; } = NetDannyh;

        // Текущая скорость из настроек Windows и номинальная из SPD, в MT/s.
        public string RAM_speed { get; set; } = NetDannyh;
        public string RAM_nominalSpeed { get; set; } = NetDannyh;

        // Тайминги CL-tRCD-tRP-tRAS и tRC в тактах по паспорту SPD.
        public string RAM_timings { get; set; } = NetDannyh;
        public string RAM_tRC { get; set; } = NetDannyh;
        public string RAM_temperature { get; set; } = NetDannyh;

        // Адрес SPD на шине SMBus; ноль, если паспорт не прочитан.
        public byte SpdAddress { get; set; }

        public string DisplayName => $"{RAM_slot} · {maker} {size_GB} ГБ";
    }
}
