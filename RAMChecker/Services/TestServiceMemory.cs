using RAMChecker.Models;

namespace RAMChecker.Services
{
    // Временный сервис с тестовыми данными для разработки таблицы.
    public class TestServiceMemory : IServisMemory
    {
        // Возвращает тестовые данные, чтобы проверить отображение таблицы без WMI.
        public List<ModuleMemoryModel> GetModulesMemory()
        {
            return new List<ModuleMemoryModel>
            {
                new ModuleMemoryModel
                {
                    size_GB = 8,
                    maker = "Kingston",
                    RAM_partNumber = "Недоступно",
                    RAM_type = "DDR4",
                    RAM_formFactor = "SO-DIMM",
                    RAM_frequency_MHz = "1600",
                    RAM_speed_MT_s = "3200",
                    RAM_voltage_V = "1,2",
                    RAM_dataWidth_bits = "64",
                    RAM_totalWidth_bits = "64",
                    RAM_deviceLocator = "DIMM 0",
                    RAM_bankLabel = "BANK 0",
                    // Временная заглушка показывает, что тайминги пока недоступны.
                    RAM_CL = "Недоступно",
                    RAM_tRCD = "Недоступно",
                    RAM_tRP = "Недоступно",
                    RAM_tRAS = "Недоступно"
                },
                new ModuleMemoryModel
                {
                    size_GB = 8,
                    maker = "Kingston",
                    RAM_partNumber = "Недоступно",
                    RAM_type = "DDR4",
                    RAM_formFactor = "SO-DIMM",
                    RAM_frequency_MHz = "1600",
                    RAM_speed_MT_s = "3200",
                    RAM_voltage_V = "1,2",
                    RAM_dataWidth_bits = "64",
                    RAM_totalWidth_bits = "64",
                    RAM_deviceLocator = "DIMM 1",
                    RAM_bankLabel = "BANK 0",
                    RAM_CL = "Недоступно",
                    RAM_tRCD = "Недоступно",
                    RAM_tRP = "Недоступно",
                    RAM_tRAS = "Недоступно"
                }
            };
        }
    }
}
