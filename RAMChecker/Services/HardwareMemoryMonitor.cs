using System.Globalization;
using System.Security.Principal;
using ZenStates.Core;

namespace RAMChecker.Services;

public record HardwareReading(string Group, string Name, string Value, string Source);
public record HardwareSnapshot(string Status, List<HardwareReading> Readings);

// Все обращения к библиотеке выполняются последовательно вне потока интерфейса.
public sealed class HardwareMemoryMonitor : IDisposable
{
    private Cpu? cpu;
    private bool attempted;
    private string unavailable = "Аппаратные данные недоступны";
    private static readonly string[] TimingGroups =
    [
        "CL RCDRD RCDWR RP RAS",
        "RC RRDS RRDL FAW WTRS WTRL WR CWL RTP RFC RFC2 RFC4 REFI",
        "RDRDSCL WRWRSCL RDWR WRRD RDRDSC RDRDSD RDRDDD WRWRSC WRWRSD WRWRDD",
        "CKE STAG MOD MODPDA MRD MRDPDA XP PHYWRD PHYWRL PHYRDL"
    ];

    public static bool HasAdminRights()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public HardwareSnapshot Read()
    {
        try
        {
            if (!attempted)
            {
                attempted = true;
                if (!HasAdminRights())
                    return new(unavailable = "Для таймингов и датчиков нужны права администратора.", []);
                // Библиотека меняет общую культуру; сохраняем русский формат приложения.
                var culture = CultureInfo.DefaultThreadCurrentCulture;
                try
                {
                    cpu = new Cpu();
                }
                finally { CultureInfo.DefaultThreadCurrentCulture = culture; }
            }
            if (cpu is null)
                return new(unavailable, []);
            var rows = new List<HardwareReading>();
            var memory = cpu.GetMemoryConfig();
            if (memory is not null)
            {
                foreach (uint channel in memory.Timings.Select(t => t.Key).Distinct().ToArray())
                {
                    // Библиотека сохраняет старые поля при ошибке чтения: помечаем их до нового опроса.
                    var previous = memory.Timings.First(t => t.Key == channel).Value;
                    foreach (string name in TimingGroups.SelectMany(g => g.Split(' ')))
                        if (previous[name] is uint)
                            previous[name] = uint.MaxValue;
                    var timings = memory.ReadTimings(channel);
                    if (timings is null || timings.CL is 0 or uint.MaxValue)
                        continue;
                    string source = $"UMC 0x{channel:X} · регистры контроллера";
                    for (int group = 0; group < TimingGroups.Length; group++)
                        foreach (string name in TimingGroups[group].Split(' '))
                            if (timings[name] is uint value && value != uint.MaxValue)
                                rows.Add(new(new[] { "Первичные", "Вторичные", "Третичные", "Дополнительные" }[group], "t" + name, value + " такт.", source));
                    rows.Add(new("Режим", "Command Rate / Gear Down", $"{timings.Cmd2T} / {timings.GDM}", source));
                }
                if (memory.RefreshTelemetry(1000) && memory.Ddr4ThermalSensors is not null)
                    foreach (var sensor in memory.Ddr4ThermalSensors)
                        if (sensor.Value is { IsValid: true, TempSensorEnabled: true } data)
                            rows.Add(new("Температура", $"DIMM SPD 0x{sensor.Key:X2}", $"{data.TemperatureC:F1} °C", "TSOD · датчик модуля"));
            }
            bool freshSmu = cpu.powerTable is not null && cpu.powerTable.Refresh() == SMU.Status.OK;
            if (freshSmu)
            {
                foreach (var clock in new[] { ("MCLK", cpu.powerTable!.MCLK), ("UCLK", cpu.powerTable.UCLK), ("FCLK", cpu.powerTable.FCLK) })
                    if (float.IsFinite(clock.Item2) && clock.Item2 > 0)
                        rows.Add(new("Частота", clock.Item1, $"{clock.Item2:F1} МГц", "SMU · телеметрия"));
            }
            if (cpu.systemInfo is not null)
                foreach (var group in cpu.systemInfo.SensorGroups)
                    foreach (var sensor in group.Sensors)
                        sensor.Value = null;
            cpu.systemInfo?.UpdateSensors();
            if (cpu.systemInfo is not null)
                foreach (var group in cpu.systemInfo.SensorGroups)
                    foreach (var sensor in group.Sensors)
                        if ((freshSmu || group.HardwareType.ToString() != "Svi3") && sensor.SensorType.ToString() == "Voltage"
                            && sensor.Value is float value && float.IsFinite(value) && value > 0
                            && new[] { "DIMM", "DRAM", "VDDIO", "VDDP", "VDDQ", "SOC" }.Any(n => sensor.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))
                            rows.Add(new("Напряжение", sensor.Name, $"{value:F3} В", group.Name + " · датчик"));
            string status = rows.Count > 0 ? $"Обновлено {DateTime.Now:HH:mm:ss}" : "Нет доступных аппаратных показаний";
            if (!rows.Any(r => r.Group == "Температура"))
                rows.Add(new("Температура", "Модули RAM", "Нет данных", "TSOD · показания не получены"));
            return new(status, rows.OrderBy(r => r.Group is "Частота" or "Напряжение" or "Температура" ? 0 : 1).ToList());
        }
        catch (Exception ex)
        {
            unavailable = "Аппаратный опрос недоступен: " + ex.Message;
            return new(unavailable, []);
        }
    }

    public void Dispose() => cpu?.Dispose();
}
