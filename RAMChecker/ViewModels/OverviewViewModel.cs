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
    private const double ChartLeft = 58, ChartRight = 600, ChartTop = 34, ChartBottom = 136;
    private readonly MainViewModel main;
    private readonly Queue<double> loadSamples = new();
    public PointCollection CommitPoints { get; private set; } = [];
    public PointCollection CommitArea { get; private set; } = [];
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
    public string CommitPercentText => Memory is { Committed: ulong committedBytes, CommitLimit: > 0 } ? $"{100d * committedBytes / Memory.CommitLimit.Value:0}%" : "—";
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
        new("Настроенная скорость (SMBIOS)", module.RAM_speed), new("Номинальная частота JEDEC (SPD)", module.RAM_nominalSpeed),
        new("Тайминги JEDEC CL-tRCD-tRP-tRAS", module.RAM_timings), new("tRC JEDEC", module.RAM_tRC),
        new("XMP профиль 1", module.RAM_xmp),
        new("Напряжение (SMBIOS)", module.RAM_voltage)
    ];

    public async Task LoadModulesAsync()
    {
        try
        {
            var modules = await Task.Run(SmbiosMemory.Read);
            if (!NeedsAdministrator)
                await Task.Run(() => ReadSpd(modules));
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
            using var sensorBus = new SpdReader();
            var spdInfos = sensorBus.ReadAll();
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
                if (info.XmpProfile1 is { } xmp)
                {
                    string voltage = xmp.Voltage is double value ? $" · {value:0.00} В" : "";
                    string status = module.ConfiguredSpeedMTs == xmp.SpeedMTs ? " · вероятно активен" : "";
                    module.RAM_xmp = $"{xmp.SpeedMTs} MT/s ({xmp.SpeedMTs / 2} МГц) · {xmp.Timings} · tRC {xmp.RowCycleTime}{voltage}{status}";
                }
                else module.RAM_xmp = "Нет профиля XMP";
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

    public void Tick()
    {
        try
        {
            Memory = MemoryMonitor.Read();
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
            UpdateChart();
        }
        catch (Exception ex) {
            main.Notice = "Мониторинг недоступен: " + ex.Message;
        }
        NotifyChanged(nameof(TotalText), nameof(UsedText), nameof(AvailableText), nameof(LoadText), nameof(CommitText), nameof(CommitPercentText), nameof(CommitUsed), nameof(CommitFree));
    }

    // График последних 60 измерений
    public void UpdateChart()
    {
        if (loadSamples.Count == 0)
            return;
        double ChartX(int count, int index) => ChartLeft + (60 - count + index) * (ChartRight - ChartLeft) / 59d;
        double ChartY(double percent) => ChartBottom - percent * (ChartBottom - ChartTop) / 100d;
        var chartPoints = loadSamples.Select((loadPercent, sampleIndex) => new Point(ChartX(loadSamples.Count, sampleIndex), ChartY(loadPercent))).ToList();
        UsagePoints = [.. chartPoints];
        UsageArea = [.. chartPoints.Append(new(ChartRight, ChartBottom)).Append(new(chartPoints[0].X, ChartBottom))];
        UsagePoints.Freeze();
        UsageArea.Freeze();
        var commitPoints = commits.Select((value, sampleIndex) => new Point(ChartX(commits.Count, sampleIndex), ChartY(value))).ToList();
        CommitPoints = [.. commitPoints];
        CommitArea = [.. commitPoints.Append(new(ChartRight, ChartBottom)).Append(new(commitPoints.Count > 0 ? commitPoints[0].X : ChartRight, ChartBottom))];
        CommitPoints.Freeze();
        CommitArea.Freeze();
        NotifyChanged(nameof(CommitPoints), nameof(CommitArea), nameof(UsagePoints), nameof(UsageArea));
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
