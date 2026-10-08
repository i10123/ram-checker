using System.Management;
using RAMChecker.Models;

namespace RAMChecker.Services
{
    // Сервис, который получает сведения о планках памяти из WMI Windows.
    public class WmiServiceMemory : IServisMemory
    {
        // Получает сведения о модулях памяти через Windows Management Instrumentation.
        public List<ModuleMemoryModel> GetModulesMemory()
        {
            var modulesMemory = new List<ModuleMemoryModel>();
            // Запрашиваем объём, производителя и настроенную тактовую частоту планки.
            var query = "SELECT Capacity, Manufacturer, ConfiguredClockSpeed FROM Win32_PhysicalMemory";

            using (var searcher = new ManagementObjectSearcher(query))
            using (var foundModules = searcher.Get())
            {
                foreach (ManagementObject module in foundModules)
                {
                    // WMI возвращает объём в байтах, поэтому переводим его в гигабайты.
                    var capacityBytes = Convert.ToUInt64(module["Capacity"]);
                    var size_GB = (int)(capacityBytes / 1024 / 1024 / 1024);
                    var maker = module["Manufacturer"]?.ToString() ?? "Неизвестно";
                    var configuredClockSpeed = Convert.ToUInt32(module["ConfiguredClockSpeed"]);

                    // На этом ноутбуке WMI возвращает эффективную скорость DDR в ConfiguredClockSpeed.
                    // Поэтому делим её пополам для частоты тактов и показываем исходное значение как MT/s.
                    var frequencyText = configuredClockSpeed > 0
                        ? $"{configuredClockSpeed / 2} МГц ({configuredClockSpeed} MT/s)"
                        : "Недоступно";

                    modulesMemory.Add(new ModuleMemoryModel
                    {
                        size_GB = size_GB,
                        maker = maker,
                        RAM_frequency_MHz = frequencyText
                    });
                }
            }

            return modulesMemory;
        }
    }
}
