using System.Management;
using RAMChecker.Models;

namespace RAMChecker.Services
{
    // Получает сведения о физических модулях памяти из WMI Windows.
    public class WmiServiceMemory
    {
        public List<ModuleMemoryModel> GetModulesMemory()
        {
            var modulesMemory = new List<ModuleMemoryModel>();
            var query = "SELECT Capacity, Manufacturer, PartNumber, SerialNumber, SMBIOSMemoryType, FormFactor, ConfiguredClockSpeed, ConfiguredVoltage, DeviceLocator, BankLabel FROM Win32_PhysicalMemory";

            using var searcher = new ManagementObjectSearcher(query);
            using var foundModules = searcher.Get();
            foreach (ManagementObject module in foundModules)
            {
                // WMI сообщает скорость в MT/s и напряжение в милливольтах.
                var speed = Convert.ToUInt32(module["ConfiguredClockSpeed"]);
                var voltage = Convert.ToUInt32(module["ConfiguredVoltage"]);
                modulesMemory.Add(new ModuleMemoryModel
                {
                    Number = modulesMemory.Count + 1,
                    size_GB = (int)(Convert.ToUInt64(module["Capacity"]) >> 30),
                    RAM_maker = GetTextValue(module["Manufacturer"]),
                    RAM_partNumber = GetTextValue(module["PartNumber"]),
                    RAM_serialNumber = GetTextValue(module["SerialNumber"]),
                    RAM_type = GetMemoryType(Convert.ToUInt32(module["SMBIOSMemoryType"])),
                    RAM_formFactor = GetFormFactor(Convert.ToUInt32(module["FormFactor"])),
                    // На многих платах слоты разных каналов называются одинаково, поэтому добавляем банк.
                    RAM_slot = $"{GetTextValue(module["BankLabel"])} · {GetTextValue(module["DeviceLocator"])}",
                    RAM_speed = speed > 0 ? $"{speed} MT/s ({speed / 2} МГц)" : ModuleMemoryModel.NetDannyh,
                    RAM_voltage = voltage > 0 ? $"{voltage / 1000m:0.00} В" : ModuleMemoryModel.NetDannyh
                });
            }
            return modulesMemory;
        }

        private static string GetTextValue(object? value)
        {
            var text = value?.ToString()?.Trim();
            return string.IsNullOrWhiteSpace(text) ? ModuleMemoryModel.NetDannyh : text;
        }

        // Коды SMBIOS переводим в название поколения памяти.
        private static string GetMemoryType(uint value) => value switch
        {
            24 => "DDR3",
            26 => "DDR4",
            30 => "LPDDR4",
            34 => "DDR5",
            35 => "LPDDR5",
            _ => ModuleMemoryModel.NetDannyh
        };

        private static string GetFormFactor(uint value) => value switch
        {
            8 => "DIMM",
            12 => "SO-DIMM",
            _ => ModuleMemoryModel.NetDannyh
        };
    }
}
