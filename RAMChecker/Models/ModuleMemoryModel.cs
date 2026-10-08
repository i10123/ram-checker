namespace RAMChecker.Models
{
    // Хранит характеристики одного устройства оперативной памяти для таблицы.
    public class ModuleMemoryModel
    {
        // Объём устройства памяти в гигабайтах.
        public int size_GB { get; set; }

        // Название производителя устройства памяти.
        public string maker { get; set; } = "Недоступно";

        // Номер детали, который позволяет определить конкретную модель памяти.
        public string RAM_partNumber { get; set; } = "Недоступно";

        // Поколение памяти, например DDR4 или DDR5.
        public string RAM_type { get; set; } = "Недоступно";

        // Физический форм-фактор, например SO-DIMM.
        public string RAM_formFactor { get; set; } = "Недоступно";

        // Тактовая частота памяти в мегагерцах.
        public string RAM_frequency_MHz { get; set; } = "Недоступно";

        // Эффективная скорость передачи данных в миллионах передач в секунду.
        public string RAM_speed_MT_s { get; set; } = "Недоступно";

        // Настроенное напряжение памяти в вольтах.
        public string RAM_voltage_V { get; set; } = "Недоступно";

        // Ширина шины данных устройства в битах.
        public string RAM_dataWidth_bits { get; set; } = "Недоступно";

        // Полная ширина устройства с учётом дополнительных битов, если они есть.
        public string RAM_totalWidth_bits { get; set; } = "Недоступно";

        // Обозначение расположения устройства памяти в системе.
        public string RAM_deviceLocator { get; set; } = "Недоступно";

        // Название банка памяти, указанное прошивкой компьютера.
        public string RAM_bankLabel { get; set; } = "Недоступно";

        // CAS Latency: задержка между запросом столбца и получением данных.
        public string RAM_CL { get; set; } = "Недоступно";

        // Задержка между выбором строки и столбца в памяти.
        public string RAM_tRCD { get; set; } = "Недоступно";

        // Задержка закрытия строки памяти.
        public string RAM_tRP { get; set; } = "Недоступно";

        // Минимальное время активности строки памяти.
        public string RAM_tRAS { get; set; } = "Недоступно";
    }
}
