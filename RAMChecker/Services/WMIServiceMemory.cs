using System.Globalization;
using System.Management;
using RAMChecker.Models;

namespace RAMChecker.Services
{
    // Сервис, который получает сведения о физических устройствах памяти из WMI Windows.
    public class WmiServiceMemory : IServisMemory
    {
        // Получает доступные характеристики каждого устройства оперативной памяти.
        public List<ModuleMemoryModel> GetModulesMemory()
        {
            var modulesMemory = new List<ModuleMemoryModel>();

            // Запрашиваем технические свойства памяти, которые доступны в Win32_PhysicalMemory.
            var query = "SELECT Capacity, Manufacturer, PartNumber, SMBIOSMemoryType, FormFactor, ConfiguredClockSpeed, ConfiguredVoltage, DataWidth, TotalWidth, DeviceLocator, BankLabel FROM Win32_PhysicalMemory";

            using (var searcher = new ManagementObjectSearcher(query))
            using (var foundModules = searcher.Get())
            {
                foreach (ManagementObject module in foundModules)
                {
                    // WMI хранит объём в байтах, поэтому переводим его в целые гигабайты.
                    var capacityBytes = Convert.ToUInt64(module["Capacity"]);
                    var size_GB = (int)(capacityBytes / 1024 / 1024 / 1024);

                    // Пустые или отсутствующие строки заменяем понятной подписью.
                    var maker = GetTextValue(module["Manufacturer"]);
                    var partNumber = GetTextValue(module["PartNumber"]);
                    var deviceLocator = GetTextValue(module["DeviceLocator"]);
                    var bankLabel = GetTextValue(module["BankLabel"]);

                    // Числовые коды SMBIOS преобразуем в привычные названия.
                    var memoryType = GetMemoryType(Convert.ToUInt32(module["SMBIOSMemoryType"]));
                    var formFactor = GetFormFactor(Convert.ToUInt32(module["FormFactor"]));

                    // На этом ноутбуке WMI возвращает эффективную скорость DDR как 3200.
                    // Делим её пополам, чтобы отдельно показать тактовую частоту 1600 МГц.
                    var configuredClockSpeed = Convert.ToUInt32(module["ConfiguredClockSpeed"]);
                    var frequencyMHz = configuredClockSpeed > 0
                        ? (configuredClockSpeed / 2).ToString(CultureInfo.CurrentCulture)
                        : "Недоступно";
                    var transferSpeed = configuredClockSpeed > 0
                        ? configuredClockSpeed.ToString(CultureInfo.CurrentCulture)
                        : "Недоступно";

                    // Напряжение WMI сообщает в милливольтах, переводим его в вольты.
                    var configuredVoltage = Convert.ToUInt32(module["ConfiguredVoltage"]);
                    var voltage = configuredVoltage > 0
                        ? (configuredVoltage / 1000m).ToString("0.##", CultureInfo.CurrentCulture)
                        : "Недоступно";

                    // Ширина шины указана в битах; нулевое значение считаем отсутствующим.
                    var dataWidth = GetNumberValue(module["DataWidth"]);
                    var totalWidth = GetNumberValue(module["TotalWidth"]);

                    modulesMemory.Add(new ModuleMemoryModel
                    {
                        size_GB = size_GB,
                        maker = maker,
                        RAM_partNumber = partNumber,
                        RAM_type = memoryType,
                        RAM_formFactor = formFactor,
                        RAM_frequency_MHz = frequencyMHz,
                        RAM_speed_MT_s = transferSpeed,
                        RAM_voltage_V = voltage,
                        RAM_dataWidth_bits = dataWidth,
                        RAM_totalWidth_bits = totalWidth,
                        RAM_deviceLocator = deviceLocator,
                        RAM_bankLabel = bankLabel,
                        // Стандартный класс WMI не возвращает эти четыре значения таймингов.
                        RAM_CL = "Недоступно",
                        RAM_tRCD = "Недоступно",
                        RAM_tRP = "Недоступно",
                        RAM_tRAS = "Недоступно"
                    });
                }
            }

            return modulesMemory;
        }

        // Возвращает текстовое значение WMI или подпись для пустого значения.
        private static string GetTextValue(object? value)
        {
            var text = value?.ToString()?.Trim();
            return string.IsNullOrWhiteSpace(text) ? "Недоступно" : text;
        }

        // Возвращает число в виде строки или подпись, если WMI вернул ноль.
        private static string GetNumberValue(object? value)
        {
            var number = Convert.ToUInt32(value);
            return number > 0
                ? number.ToString(CultureInfo.CurrentCulture)
                : "Недоступно";
        }

        // Переводит распространённые коды SMBIOS в название поколения памяти.
        private static string GetMemoryType(uint value)
        {
            return value switch
            {
                20 => "DDR",
                21 => "DDR2",
                24 => "DDR3",
                26 => "DDR4",
                30 => "LPDDR4",
                34 => "DDR5",
                35 => "LPDDR5",
                _ => "Недоступно"
            };
        }

        // Переводит числовой код форм-фактора памяти в понятное обозначение.
        private static string GetFormFactor(uint value)
        {
            return value switch
            {
                7 => "SIMM",
                8 => "DIMM",
                11 => "RIMM",
                12 => "SO-DIMM",
                13 => "SO-RIMM",
                _ => "Недоступно"
            };
        }
    }
}
