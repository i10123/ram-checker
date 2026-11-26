using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Security.Principal;
using System.Windows;
using System.Windows.Media;
using RAMChecker.Helpers;
using RAMChecker.Models;
using RAMChecker.Services;

namespace RAMChecker.ViewModels;

public record BarItem(string Name, string Value, double Width);

public class OverviewViewModel : ObservableObject
{
    private readonly MainViewModel main;
    private readonly Queue<double> loadSamples = new();
    private SpdReader? sensorBus;
    private List<SpdInfo> sensors = [];
    private RyzenMemoryClock? clock;
    private BoardVoltage? power;
    private Task polling = Task.CompletedTask;
    private readonly Queue<double> clocks = new();
    public PointCollection ClockPoints { get; private set; } = [];
    private long clockTimestamp;
    private double? clockMHz;
    public double? RecentClockMHz => Environment.TickCount64 - clockTimestamp <= 2 * main.IntervalMs ? clockMHz : null;
    public string ClockRange { get; private set; } = "";
    public string ClockText { get; private set; } = "—";
    public string VoltageText { get; private set; } = "—";
    public bool NoTemperatures => Temperatures.Count == 0;
    public List<KeyValuePair<string, string>> Temperatures { get; private set; } = [];
    public PointCollection CommitPoints { get; private set; } = [];
    private readonly Queue<double> commits = new();
    private string wheaErrors = "…";

    public ObservableCollection<ModuleMemoryModel> ModulesMemory { get; } = [];
    public MemorySnapshot? Memory { get; private set; }
    public PointCollection UsagePoints { get; private set; } = [];
    public PointCollection UsageArea { get; private set; } = [];
    public List<BarItem> BenchmarkBars { get; private set; } = [];
    public string BenchmarkText { get; private set; } = "Замер ещё не выполнялся";
    public bool BenchmarkRunning { get; private set; }
    public string SpdStatus { get; private set; } = "";
    public bool NeedsAdministrator { get; } = !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public RelayCommand BenchmarkCommand { get; }
    public RelayCommand AdministratorCommand { get; }

    public string TotalText => Memory is null ? "—" : MemorySnapshot.Format(Memory.Total);
    public string UsedText => Memory is null ? "—" : $"{MemorySnapshot.Format(Memory.Used)} · {Memory.LoadPercent:0}%";
    public string LoadText => Memory is null ? "—" : $"{Memory.LoadPercent:0}%";
    public string CommitText => Memory is { Committed: ulong committedBytes, CommitLimit: ulong commitLimitBytes } ? $"{MemorySnapshot.Format(committedBytes)} из {MemorySnapshot.Format(commitLimitBytes)}" : "—";
    public GridLength CommitUsed => new(Memory?.Committed ?? 0, GridUnitType.Star);
    public GridLength CommitFree => new(Memory is { Committed: ulong committedBytes, CommitLimit: ulong commitLimitBytes } && commitLimitBytes > committedBytes ? commitLimitBytes - committedBytes : 1, GridUnitType.Star);
    public List<KeyValuePair<string, string>> MemoryRows { get; private set; } = [];
    public string AvailableText => Memory is null ? "—" : MemorySnapshot.Format(Memory.Available);
    public bool Loading { get; private set; } = true;
    public string ModulesText => Loading ? "…" : $"{ModulesMemory.Count} шт. · {ModulesMemory.FirstOrDefault()?.RAM_type ?? "—"}";

    public OverviewViewModel(MainViewModel main)
    {
        this.main = main;
        BenchmarkCommand = new(async () => await BenchmarkAsync(), () => !BenchmarkRunning && !main.Test.IsRunning);
        AdministratorCommand = new(RestartAsAdministrator, () => !BenchmarkRunning && !main.Test.IsRunning);
    }

    public void RefreshLanguage() => NotifyChanged();

    public ModuleMemoryModel? SelectedModule
    {
        get;
        set
        {
            field = value;
            NotifyChanged();
        }
    }

    // Строки паспорта выбранного модуля для таблицы «характеристика — значение».
    public List<KeyValuePair<string, string>> ModuleRows => SelectedModule is not { } module ? [] :
    [
        new("Производитель", module.RAM_maker), new("Модель", module.RAM_partNumber), new("Серийный номер", module.RAM_serialNumber),
        new("Тип", $"{module.RAM_type} {module.RAM_formFactor}"), new("Объём", $"{module.Size_GB} ГБ"), new("Слот", module.RAM_slot),
        new("Настроенная скорость (SMBIOS)", module.RAM_speed), new("Номинальная частота", module.RAM_nominalSpeed),
        new("Тайминги CL-tRCD-tRP-tRAS", module.RAM_timings), new("tRC", module.RAM_tRC),
        new("Напряжение (SMBIOS)", module.RAM_voltage)
    ];

    public async Task LoadModulesAsync()
    {
        try
        {
            var modules = await Task.Run(SmbiosMemory.Read);
            await Task.Run(() =>
            {
                try { power = new BoardVoltage(); }
                catch (Exception) { /* Optional WMI provider unavailable. */ }
                if (NeedsAdministrator) return;
                try { clock = new RyzenMemoryClock(); } catch (Exception) { }
                ReadSpd(modules);
            });
            foreach (var module in modules)
                ModulesMemory.Add(module);
            SelectedModule = ModulesMemory.FirstOrDefault();
        }
        catch (Exception ex) { main.Notice = "Не удалось получить сведения о модулях: " + ex.Message; }
        Loading = false;
        NotifyChanged();
        await ReadWheaAsync();
    }

    private void ReadSpd(List<ModuleMemoryModel> modules)
    {
        try
        {
            sensorBus = new SpdReader();
            var spdInfos = sensorBus.ReadAll();
            sensors = spdInfos;
            var pairs = SpdMatcher.Match(modules, spdInfos, out string matchMethod);
            SpdStatus = spdInfos.Count == 0 ? "Паспорт SPD DDR4 или DDR5 не найден на шине SMBus."
                : pairs.Count == 0 ? $"Паспорт SPD прочитан у {spdInfos.Count} из {modules.Count} модулей, но его нельзя однозначно отнести к слотам."
                : pairs.Count < modules.Count ? $"Паспорт SPD найден у {pairs.Count} из {modules.Count} модулей."
                : matchMethod.Contains("предположительно") ? "Планки одинакового объёма: тайминги отнесены к слотам предположительно."
                : "";
            foreach (var (module, info) in pairs)
            {
                module.RAM_nominalSpeed = $"{info.NominalSpeed} MT/s ({info.NominalSpeed / 2} МГц)";
                module.RAM_timings = $"{info.CasLatency}-{info.RasToCasDelay}-{info.RowPrecharge}-{info.RowActiveTime}";
                module.RAM_tRC = info.RowCycleTime.ToString();
            }
        }
        catch (DllNotFoundException) { SpdStatus = "Для таймингов установите драйвер PawnIO."; }
        catch (Exception ex) { SpdStatus = "Шина SMBus недоступна: " + ex.Message; }
    }

    // Исправленные ошибки, которые сам процессор нашёл во всей памяти, а не только в буфере проверки.
    // Windows записывает их в журнал System от источника WHEA-Logger с кодом 47 (ошибка памяти).
    public async Task ReadWheaAsync() => wheaErrors = await Task.Run(ReadWhea);

    private static string ReadWhea()
    {
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName,
                "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and EventID=47]]") { ReverseDirection = true });
            int eventCount = 0;
            DateTime? lastEventTime = null;
            for (var eventRecord = reader.ReadEvent(); eventRecord is not null; eventRecord = reader.ReadEvent())
                using (eventRecord)
                {
                    lastEventTime ??= eventRecord.TimeCreated;
                    eventCount++;
                }
            return eventCount == 0 ? "Нет" : $"{eventCount}, последняя {lastEventTime:dd.MM.yyyy HH:mm}";
        }
        catch (Exception) { return ModuleMemoryModel.NoData; }
    }

    private async Task PollSensorsAsync()
    {
        var sample = await Task.Run(() =>
        {
            double? mhz = null, volts = null;
            double?[] temperatures = new double?[sensors.Count];
            try { mhz = clock?.ReadMHz(); } catch (Exception) { }
            try { volts = power?.ReadVolts(); } catch (Exception) { }
            try { if (sensorBus != null && sensors.Count > 0) temperatures = sensorBus.ReadTemperatures(sensors); }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { }
            return (mhz, volts, temperatures);
        });
        clockMHz = sample.mhz;
        clockTimestamp = Environment.TickCount64;
        ClockText = sample.mhz is double mhz ? $"{mhz:0.##} MHz" : "—";
        VoltageText = sample.volts is double volts ? $"{volts:0.000} V · {power?.Source}" : "—";
        Temperatures = sensors.Select((sensor, i) => new KeyValuePair<string, string>(
            $"SPD 0x{sensor.Address:X2}  {sensor.PartNumber}", sample.temperatures[i] is double t ? $"{t:0.##} °C" : "—")).ToList();
        if (sample.mhz is double value)
        {
            clocks.Enqueue(value);
            if (clocks.Count > 60) clocks.Dequeue();
        }
        else clocks.Clear();
        if (clocks.Count > 0)
        {
            double low = clocks.Min() - 1, range = clocks.Max() + 1 - low;
            ClockRange = $"{low:0.#}–{low + range:0.#} MHz";
            ClockPoints = [.. clocks.Select((x,i) => new Point(i * 600d / 59, 158 - (x-low) / range * 156))];
        }
        else { ClockPoints = []; ClockRange = ""; }
        NotifyChanged(nameof(ClockText), nameof(VoltageText), nameof(Temperatures), nameof(NoTemperatures), nameof(ClockPoints), nameof(ClockRange));
    }

    public async Task CloseSensorsAsync()
    {
        await polling;
        sensorBus?.Dispose();
        clock?.Dispose();
    }

    public void Tick()
    {
        try
        {
            Memory = MemoryMonitor.Read();
            if (!Loading && polling.IsCompleted)
                polling = PollSensorsAsync();
            if (Memory.Committed is ulong used && Memory.CommitLimit is > 0)
            {
                commits.Enqueue(100.0 * used / Memory.CommitLimit.Value);
                if (commits.Count > 60) commits.Dequeue();
            }
            else commits.Clear();
            loadSamples.Enqueue(Memory.LoadPercent);
            if (loadSamples.Count > 60)
                loadSamples.Dequeue();
            UpdateMemoryRows(Memory);
            if (main.SelectedPage == 1)
                UpdateChart();
        }
        catch (Exception ex) {
            main.Notice = "Мониторинг недоступен: " + ex.Message;
        }
        NotifyChanged(nameof(TotalText), nameof(UsedText), nameof(AvailableText), nameof(LoadText), nameof(CommitText), nameof(CommitUsed), nameof(CommitFree));
    }

    // График последних 60 измерений
    public void UpdateChart()
    {
        if (loadSamples.Count == 0)
            return;
        var chartPoints = loadSamples.Select((loadPercent, sampleIndex) => new Point((60 - loadSamples.Count + sampleIndex) * 600 / 59d, 160 - loadPercent * 1.6)).ToList();
        UsagePoints = [.. chartPoints];
        UsageArea = [.. chartPoints.Append(new(600, 160)).Append(new(chartPoints[0].X, 160))];
        UsagePoints.Freeze();
        UsageArea.Freeze();
        CommitPoints = [.. commits.Select((v,i) => new Point((60 - commits.Count + i)*600d/59, 160 - v*1.6))];
        NotifyChanged(nameof(CommitPoints));
        NotifyChanged(nameof(UsagePoints), nameof(UsageArea));
    }

    private void UpdateMemoryRows(MemorySnapshot snapshot)
    {
        List<KeyValuePair<string, string>> rows =
        [
            new("Системный кэш", snapshot.Cache is ulong cache ? MemorySnapshot.Format(cache) : ModuleMemoryModel.NoData),
            new("Файл подкачки", snapshot.CommitLimit is ulong commitLimitBytes && commitLimitBytes > snapshot.Total ? MemorySnapshot.Format(commitLimitBytes - snapshot.Total) : "Нет"),
            new("Доступно для проверки", MemorySnapshot.Format((ulong)MemorySnapshot.SafeTestBytes(snapshot))),
            new("Ошибки памяти в журнале Windows", wheaErrors)
        ];
        if (rows.SequenceEqual(MemoryRows))
            return;
        MemoryRows = rows;
        NotifyChanged(nameof(MemoryRows));
    }

    private async Task BenchmarkAsync()
    {
        BenchmarkRunning = true;
        BenchmarkText = "Идёт замер…";
        NotifyChanged();
        try
        {
            var benchmark = await MemoryBenchmark.RunAsync();
            double bestSpeed = Math.Max(benchmark.ReadMegabytesPerSecond, Math.Max(benchmark.WriteMegabytesPerSecond, benchmark.CopyMegabytesPerSecond));
            BenchmarkBars = [.. new[] { ("Чтение", benchmark.ReadMegabytesPerSecond), ("Запись", benchmark.WriteMegabytesPerSecond), ("Копирование", benchmark.CopyMegabytesPerSecond) }
                .Select(bar => new BarItem(bar.Item1, $"{bar.Item2:N0} МБ/с", bar.Item2 / bestSpeed * 200))];
            BenchmarkText = $"Задержка: {benchmark.LatencyNanoseconds:0.0} нс";
        }
        catch (Exception ex) { BenchmarkText = "Замер не выполнен: " + ex.Message; }
        MemoryMonitor.ReturnFreeMemory();
        BenchmarkRunning = false;
        NotifyChanged();
    }

    private void RestartAsAdministrator()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, Environment.GetCommandLineArgs().Skip(1)) { UseShellExecute = true, Verb = "runas" });
            Application.Current.MainWindow.Close();
        }
        catch (Exception) { }
    }
}
