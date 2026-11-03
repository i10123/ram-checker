namespace RAMChecker.Models
{
    // Характеристики одного модуля памяти: паспорт из WMI, тайминги и температура из SPD.
    public class ModuleMemoryModel
    {
        public const string NetDannyh = "Нет данных";

        public int Number { get; set; }
        public int size_GB { get; set; }
        public string RAM_maker { get; set; } = NetDannyh;
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

        // Адрес SPD на шине SMBus; ноль, если паспорт не прочитан или на модуле нет датчика температуры.
        public byte SpdAddress { get; set; }
        // Откуда взят паспорт: адрес микросхемы и пометка, если соответствие планке не подтверждено.
        public string RAM_spd { get; set; } = NetDannyh;

        public string DisplayName => $"Модуль {Number} · {RAM_maker} {RAM_partNumber}";
    }
}
