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
            // Пока запрашиваем только объём и производителя каждой планки.
            var query = "SELECT Capacity, Manufacturer FROM Win32_PhysicalMemory";

            using (var searcher = new ManagementObjectSearcher(query))
            using (var foundModules = searcher.Get())
            {
                foreach (ManagementObject module in foundModules)
                {
                    // WMI возвращает объём в байтах, поэтому переводим его в гигабайты.
                    var capacityBytes = Convert.ToUInt64(module["Capacity"]);
                    var size_GB = (int)(capacityBytes / 1024 / 1024 / 1024);
                    var maker = module["Manufacturer"]?.ToString() ?? "Неизвестно";

                    // Частота пока не запрашивается; модель покажет «Недоступно».
                    modulesMemory.Add(new ModuleMemoryModel
                    {
                        size_GB = size_GB,
                        maker = maker
                    });
                }
            }

            return modulesMemory;
        }
    }
}
