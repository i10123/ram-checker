using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Media;
using RAMChecker.Helpers;
using RAMChecker.Models;
using RAMChecker.Services;

namespace RAMChecker.ViewModels;

public record BarItem(string Name, string Value, double Width);

// Страница «Обзор»: модули памяти, загрузка в реальном времени и замер скорости.
public class OverviewViewModel : ObservableObject
{
    private readonly MainViewModel main;
    private readonly Queue<double> samples = new();

    public ObservableCollection<ModuleMemoryModel> ModulesMemory { get; } = [];
    public MemorySnapshot? Memory { get; private set; }
    // Точки графика для страницы теста: новая замороженная коллекция на каждое обновление,
    // потому что изменения внутри одной коллекции WPF не перерисовывает.
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
    public string UsedText => Memory is null ? "—" : $"{MemorySnapshot.Format(Memory.Used)} · {Memory.Load:0}%";
    public string LoadText => Memory is null ? "—" : $"{Memory.Load:0}%";
    // Выделенная программам память (commit) вместе с файлом подкачки.
    public string CommitText => Memory is { Committed: ulong c, CommitLimit: ulong l } ? $"{MemorySnapshot.Format(c)} из {MemorySnapshot.Format(l)}" : "—";
    public GridLength CommitUsed => new(Memory?.Committed ?? 0, GridUnitType.Star);
    public GridLength CommitFree => new(Memory is { Committed: ulong c, CommitLimit: ulong l } && l > c ? l - c : 1, GridUnitType.Star);
    public List<KeyValuePair<string, string>> MemoryRows { get; private set; } = [];
    public string AvailableText => Memory is null ? "—" : MemorySnapshot.Format(Memory.Available);
    // Пока SMBIOS и SPD читаются в фоне, вместо пустого списка показывается состояние загрузки.
    public bool Loading { get; private set; } = true;
    public string ModulesText => Loading ? "…" : $"{ModulesMemory.Count} шт. · {ModulesMemory.FirstOrDefault()?.RAM_type ?? "—"}";

    public OverviewViewModel(MainViewModel main)
    {
        this.main = main;
        BenchmarkCommand = new(async () => await BenchmarkAsync(), () => !BenchmarkRunning && !main.Test.IsRunning);
        AdministratorCommand = new(RestartAsAdministrator, () => !BenchmarkRunning && !main.Test.IsRunning);
    }

    public ModuleMemoryModel? SelectedModule
    {
        get;
        set
        {
            field = value;
            Changed();
        }
    }

    // Строки паспорта выбранного модуля для таблицы «характеристика — значение».
    public List<KeyValuePair<string, string>> ModuleRows => SelectedModule is not { } m ? [] :
    [
        new("Производитель", m.RAM_maker), new("Модель", m.RAM_partNumber), new("Серийный номер", m.RAM_serialNumber),
        new("Тип", $"{m.RAM_type} {m.RAM_formFactor}"), new("Объём", $"{m.size_GB} ГБ"), new("Слот", m.RAM_slot),
        new("Текущая частота", m.RAM_speed), new("Номинальная частота", m.RAM_nominalSpeed),
        new("Тайминги CL-tRCD-tRP-tRAS", m.RAM_timings), new("tRC", m.RAM_tRC),
        new("Напряжение", m.RAM_voltage)
    ];

    public async Task LoadModulesAsync()
    {
        try
        {
            var modules = await Task.Run(SmbiosMemory.Read);
            if (!NeedsAdministrator)
                await Task.Run(() => ReadSpd(modules));
            foreach (var m in modules)
                ModulesMemory.Add(m);
            SelectedModule = ModulesMemory.FirstOrDefault();
        }
        catch (Exception ex) { main.Notice = "Не удалось получить сведения о модулях: " + ex.Message; }
        Loading = false;
        Changed(); // Изменились сразу список модулей, состояние загрузки и строка о чтении SPD.
    }

    // Пары «модуль из SMBIOS — паспорт SPD». Надёжный признак планки — серийный номер и модель из второй страницы SPD:
    // пара принимается, если они совпали ровно у одного модуля и одного паспорта. Распаянная на плате память
    // микросхемы SPD не имеет, такой модуль остаётся без пары. Если по номеру не нашлось ни одной пары (номер не
    // прочитан или BIOS записал его в SMBIOS иначе), паспорта сопоставляются по порядку (адреса SPD 0x50, 0x51… и список
    // SMBIOS обычно идут от первого слота), но только если число модулей и объём каждого совпадают. Если объёмы всех
    // планок разные, совпадение объёма подтверждает пару; если есть одинаковые, их можно перепутать.
    public static List<(ModuleMemoryModel Module, SpdInfo Info)> Match(List<ModuleMemoryModel> modules, List<SpdInfo> infos, out string how)
    {
        bool Same(ModuleMemoryModel m, SpdInfo i) => i.Serial == m.RAM_serialNumber && i.Part == m.RAM_partNumber;
        List<(ModuleMemoryModel, SpdInfo)> pairs = [.. from m in modules from i in infos
            where Same(m, i) && infos.Count(j => Same(m, j)) == 1 && modules.Count(n => Same(n, i)) == 1 select (m, i)];
        how = "совпали серийный номер и модель";
        if (pairs.Count == 0 && infos.Count == modules.Count && modules.Zip(infos).All(p => p.First.size_GB == p.Second.SizeGB))
        {
            pairs = [.. modules.Zip(infos)];
            how = modules.Select(m => m.size_GB).Distinct().Count() < modules.Count ? "отнесён по порядку слотов, предположительно" : "по порядку слотов";
        }
        return pairs;
    }

    // Паспорт читается один раз: после чтения драйвер освобождается. Без сопоставления модули остаются без таймингов.
    private void ReadSpd(List<ModuleMemoryModel> modules)
    {
        try
        {
            using var reader = new SpdReader();
            var infos = reader.ReadAll();
            var pairs = Match(modules, infos, out string how);
            SpdStatus = infos.Count == 0 ? "Паспорт SPD DDR4 или DDR5 не найден на шине SMBus."
                : pairs.Count == 0 ? $"Паспорт SPD прочитан у {infos.Count} из {modules.Count} модулей, но его нельзя однозначно отнести к слотам."
                : pairs.Count < modules.Count ? $"Паспорт SPD найден у {pairs.Count} из {modules.Count} модулей."
                : how.Contains("предположительно") ? "Планки одинакового объёма: тайминги отнесены к слотам предположительно."
                : "";
            foreach (var (m, info) in pairs)
            {
                m.RAM_nominalSpeed = $"{info.NominalSpeed} MT/s ({info.NominalSpeed / 2} МГц)";
                m.RAM_timings = $"{info.CL}-{info.RCD}-{info.RP}-{info.RAS}";
                m.RAM_tRC = info.RC.ToString();
            }
        }
        catch (DllNotFoundException) { SpdStatus = "Для таймингов установите драйвер PawnIO."; }
        catch (Exception ex) { SpdStatus = "Шина SMBus недоступна: " + ex.Message; }
    }

    public void Tick()
    {
        try
        {
            Memory = MemoryMonitor.Read();
            samples.Enqueue(Memory.Load);
            if (samples.Count > 60)
                samples.Dequeue();
            UpdateMemoryRows(Memory);
            // График виден только на странице теста — на других страницах его не пересчитываем.
            if (main.SelectedPage == 1)
                UpdateChart();
        }
        catch (Exception ex) { main.Notice = "Мониторинг недоступен: " + ex.Message; }
        Changed(nameof(TotalText), nameof(UsedText), nameof(AvailableText), nameof(LoadText), nameof(CommitText), nameof(CommitUsed), nameof(CommitFree));
    }

    // График последних 60 измерений в области 600 × 160; заливка замыкается по нижнему краю.
    public void UpdateChart()
    {
        if (samples.Count == 0)
            return;
        var line = samples.Select((load, i) => new Point((60 - samples.Count + i) * 600 / 59d, 160 - load * 1.6)).ToList();
        UsagePoints = new PointCollection(line);
        UsageArea = new PointCollection(line.Append(new(600, 160)).Append(new(line[0].X, 160)));
        UsagePoints.Freeze();
        UsageArea.Freeze();
        Changed(nameof(UsagePoints), nameof(UsageArea));
    }

    // Строки пересоздаются, только когда меняется хотя бы одно значение, — экран не перестраивается каждую секунду.
    private void UpdateMemoryRows(MemorySnapshot m)
    {
        List<KeyValuePair<string, string>> rows =
        [
            new("Системный кэш", m.Cache is ulong cache ? MemorySnapshot.Format(cache) : ModuleMemoryModel.NoData),
            new("Файл подкачки", m.CommitLimit is ulong l && l > m.Total ? MemorySnapshot.Format(l - m.Total) : "Нет"),
            new("Доступно для проверки", MemorySnapshot.Format((ulong)MemorySnapshot.SafeTestBytes(m)))
        ];
        if (rows.SequenceEqual(MemoryRows))
            return;
        MemoryRows = rows;
        Changed(nameof(MemoryRows));
    }

    private async Task BenchmarkAsync()
    {
        BenchmarkRunning = true;
        BenchmarkText = "Идёт замер…";
        Changed();
        try
        {
            var r = await MemoryBenchmark.RunAsync();
            double max = Math.Max(r.ReadMBs, Math.Max(r.WriteMBs, r.CopyMBs));
            BenchmarkBars = [.. new[] { ("Чтение", r.ReadMBs), ("Запись", r.WriteMBs), ("Копирование", r.CopyMBs) }
                .Select(b => new BarItem(b.Item1, $"{b.Item2:N0} МБ/с", b.Item2 / max * 200))];
            BenchmarkText = $"Задержка: {r.LatencyNs:0.0} нс";
        }
        catch (Exception ex) { BenchmarkText = "Замер не выполнен: " + ex.Message; }
        MemoryMonitor.ReturnFreeMemory(); // Буферы замера больше не нужны.
        BenchmarkRunning = false;
        Changed();
    }

    private void RestartAsAdministrator()
    {
        try
        {
            // Ключи запуска (например, --imitation) передаются новому процессу.
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, Environment.GetCommandLineArgs().Skip(1)) { UseShellExecute = true, Verb = "runas" });
            Application.Current.MainWindow.Close();
        }
        catch (Exception) { } // Пользователь отменил запрос UAC — продолжаем без прав администратора.
    }
}
