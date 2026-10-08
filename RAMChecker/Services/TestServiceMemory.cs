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
                    // Тестовый пример показывает частоту и скорость передачи DDR.
                    RAM_frequency_MHz = "1600 МГц (3200 MT/s)"
                },
                new ModuleMemoryModel
                {
                    size_GB = 8,
                    maker = "Kingston",
                    RAM_frequency_MHz = "1600 МГц (3200 MT/s)"
                }
            };
        }
    }
}
