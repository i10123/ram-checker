using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using RAMChecker.Helpers;
using RAMChecker.Models;
using RAMChecker.Services;

namespace RAMChecker.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private readonly LocalStorage storage = new();
    private readonly DispatcherTimer timer = new();
    private readonly Queue<double> samples = new();
    private readonly HardwareMemoryMonitor hardware = new();
    private Task? hardwareTask;
    private CancellationTokenSource? cancellation;
    private Task? runningTask;
    private DiagnosticResult? active, selectedResult;
    private AppSettings settings = new();
    private MemorySnapshot? memory;
    private int selectedPage, modeIndex;
    private double memoryMiB = 512, percent;
    private long lastSavedSecond;
    private bool historyWritable = true, benchmarkRunning;
    private ModuleMemoryModel? selectedModule;
    private string notice = "", stage = "Готово к запуску";
    public ObservableCollection<ModuleMemoryModel> ModulesMemory { get; } = [];
    public ObservableCollection<DiagnosticResult> History { get; } = [];
    public ObservableCollection<string> Events { get; } = [];
    public ObservableCollection<HardwareReading> HardwareReadings { get; } = [];
    public string HardwareStatus { get; private set; } = "Подготовка аппаратного опроса…";
    public bool NeedsAdministrator { get; } = !HardwareMemoryMonitor.HasAdminRights();
    public bool HasHardwareReadings => HardwareReadings.Count > 0;
    public string[] Navigation { get; } = ["Обзор", "Тестирование", "Результаты", "Настройки"];
    public string[] RefreshOptions { get; } = ["1 секунда", "2 секунды", "5 секунд"];
    public string[] Modes { get; } = ["Быстрая · 10 минут", "Полная · 2 часа", "Циклическая · до остановки"];
    public Komanda OpenTestCommand { get; }
    public Komanda StartCommand { get; }
    public Komanda StopCommand { get; }
    public Komanda RefreshModulesCommand { get; }
    public Komanda BenchmarkCommand { get; }
    public string BenchmarkText { get; private set; } = "Измерение ещё не выполнялось";
    private bool ModulesLoading { get; set; }
    public bool IsRunning => cancellation is not null;
    public bool CanConfigure => !IsRunning && !benchmarkRunning;
    public bool HasHistory => History.Count > 0;
    private bool HasModules => ModulesMemory.Count > 0;
    public string Notice
    {
        get => notice;
        private set
        {
            notice = value;
            Changed();
        }
    }

    public bool HasNotice => !string.IsNullOrEmpty(Notice);
    public int SelectedPage
    {
        get => selectedPage;
        set
        {
            if (selectedPage == value)
                return;
            selectedPage = value;
            Changed();
        }
    }

    public int ModeIndex
    {
        get => modeIndex;
        set
        {
            var v = Math.Clamp(value, 0, 2);
            if (modeIndex == v)
                return;
            modeIndex = v;
            Changed();
        }
    }

    public double MemoryMiB
    {
        get => memoryMiB;
        set
        {
            if (!double.IsFinite(value))
                return;
            var v = Math.Max(64, Math.Floor(value / 16) * 16);
            if (memoryMiB == v)
                return;
            memoryMiB = v;
            Changed();
        }
    }

    public double MaxMemoryMiB => IsRunning ? MemoryMiB : Math.Max(64, memory is null ? 64 : MemorySnapshot.SafeTestBytes(memory) / 1048576);
    public string TestMemoryText => $"{MemoryMiB:0} МБ ({MemoryMiB / 1024:0.##} ГБ)";
    public string ModeText => new[] { "Быстрая", "Полная", "Циклическая" }[ModeIndex];
    public string TotalText => memory is null ? "—" : MemorySnapshot.Format(memory.Total);
    public string UsedText => memory is null ? "—" : MemorySnapshot.Format(memory.Used);
    public string AvailableText => memory is null ? "—" : MemorySnapshot.Format(memory.Available);
    public string CommittedText => memory?.Committed is ulong v ? MemorySnapshot.Format(v) : "Нет данных";
    public string CacheText => memory?.Cache is ulong v ? MemorySnapshot.Format(v) : "Нет данных";
    public string SpeedText => HasModules ? ModulesMemory[0].RAM_speed_MT_s + " MT/s" : "Нет данных";
    public string Stage => stage;
    public double Percent => percent;
    public string PercentText => ModeIndex == 2 ? "∞" : $"{percent:0}%";
    public string ElapsedText => active?.DurationText ?? "00:00:00";
    public string ErrorCountText => (active?.ErrorCount ?? 0).ToString();
    public string PassText { get; private set; } = "—";
    public PointCollection UsagePoints { get; private set; } = new();
    public ModuleMemoryModel? SelectedModule
    {
        get => selectedModule;
        set
        {
            selectedModule = value;
            Changed();
        }
    }

    public DiagnosticResult? SelectedResult
    {
        get => selectedResult;
        set
        {
            selectedResult = value;
            Changed();
        }
    }

    public bool AutoSave
    {
        get => settings.AutoSave;
        set
        {
            settings.AutoSave = value;
            SaveSettings();
            if (value)
                SaveHistory();
            Changed();
        }
    }

    public bool StopOnFirstError
    {
        get => settings.StopOnFirstError;
        set
        {
            settings.StopOnFirstError = value;
            SaveSettings();
            Changed();
        }
    }

    public int RefreshIndex
    {
        get => settings.RefreshSeconds == 5 ? 2 : settings.RefreshSeconds == 2 ? 1 : 0;
        set
        {
            settings.RefreshSeconds = new[] { 1, 2, 5 }[Math.Clamp(value, 0, 2)];
            timer.Interval = TimeSpan.FromSeconds(settings.RefreshSeconds);
            SaveSettings();
            Changed();
        }
    }

    public MainViewModel()
    {
        OpenTestCommand = new(() => SelectedPage = 1);
        StartCommand = new(async () => await StartAsync(), () => CanConfigure && memory is not null && MaxMemoryMiB >= MemoryMiB);
        BenchmarkCommand = new(async () => await (runningTask = BenchmarkAsync()), () => CanConfigure);
        StopCommand = new(() => cancellation?.Cancel(), () => IsRunning);
        RefreshModulesCommand = new(async () => await LoadModulesAsync(), () => !ModulesLoading);
        timer.Tick += (_, _) => Tick();
    }

    public async Task InitializeAsync()
    {
        try
        {
            settings = storage.Read<AppSettings>("settings.json") ?? new();
        }
        catch (Exception ex) { Notice = "Не удалось прочитать настройки: " + ex.Message; }
        try
        {
            foreach (var r in storage.ReadHistory().OrderByDescending(r => r.Started))
                History.Add(r);
            SelectedResult = History.FirstOrDefault();
        }
        catch (Exception ex) { historyWritable = false; Notice = "История недоступна; исходный файл сохранён. " + ex.Message; }
        timer.Interval = TimeSpan.FromSeconds(new[] { 1, 2, 5 }.Contains(settings.RefreshSeconds) ? settings.RefreshSeconds : 1);
        Tick();
        timer.Start();
        await LoadModulesAsync();
    }

    private async Task LoadModulesAsync()
    {
        if (ModulesLoading)
            return;
        ModulesLoading = true;
        Changed();
        try
        {
            var items = await Task.Run(() => new WmiServiceMemory().GetModulesMemory());
            ModulesMemory.Clear();
            foreach (var m in items)
                ModulesMemory.Add(m);
            SelectedModule = ModulesMemory.FirstOrDefault();
            if (items.Count == 0)
                Notice = "Windows не вернула сведения о модулях. Мониторинг и проверка буфера остаются доступны.";
        }
        catch (Exception ex) { Notice = "Не удалось получить паспорт модулей: " + ex.Message; }
        finally { ModulesLoading = false; Changed(); }
    }

    private void Tick()
    {
        if (hardwareTask is null || hardwareTask.IsCompleted)
            hardwareTask = ReadHardwareAsync();
        try
        {
            memory = MemoryMonitor.Read();
            samples.Enqueue(memory.Load);
            while (samples.Count > 60)
                samples.Dequeue();
            var points = new PointCollection();
            int i = 60 - samples.Count;
            foreach (var sample in samples)
                points.Add(new Point(i++ * 600d / 59, 130 - sample * 1.3));
            points.Freeze();
            UsagePoints = points;
        }
        catch (Exception ex) { memory = null; Notice = "Мониторинг недоступен: " + ex.Message; }
        if (active is not null && active.ElapsedSeconds - lastSavedSecond >= 10)
        {
            lastSavedSecond = (long)active.ElapsedSeconds;
            SaveHistory();
        }
        Changed();
    }

    private async Task ReadHardwareAsync()
    {
        var snapshot = await Task.Run(hardware.Read);
        // Сохраняем коллекцию, чтобы таблица не сбрасывала прокрутку раз в секунду.
        while (HardwareReadings.Count > snapshot.Readings.Count)
            HardwareReadings.RemoveAt(HardwareReadings.Count - 1);
        for (int i = 0; i < snapshot.Readings.Count; i++)
            if (i == HardwareReadings.Count)
                HardwareReadings.Add(snapshot.Readings[i]);
            else if (HardwareReadings[i] != snapshot.Readings[i])
                HardwareReadings[i] = snapshot.Readings[i];
        HardwareStatus = snapshot.Status;
        Changed();
    }

    public async Task StartAsync()
    {
        if (!CanConfigure)
            return;
        Tick();
        long bytes = checked((long)MemoryMiB * 1048576);
        if (memory is null || bytes > MemorySnapshot.SafeTestBytes(memory))
        {
            Notice = "Для выбранного объёма не хватает свободной памяти с резервом для Windows.";
            return;
        }
        cancellation = new();
        percent = 0;
        lastSavedSecond = 0;
        stage = "Выделение памяти";
        active = new()
        {
            Mode = ModeText,
            RequestedBytes = bytes,
            Algorithms = MemoryDiagnosticService.Profile(ModeIndex != 0),
            Configuration = string.Join("\n", ModulesMemory.Select(m => $"{m.maker} · {m.size_GB} ГБ · {m.RAM_speed_MT_s} MT/s (настройка Windows)"))
                + "\n" + HardwareStatus + "\n" + string.Join("\n", HardwareReadings.Select(r => $"{r.Name}: {r.Value} · {r.Source}"))
        };
        var session = active;
        var mode = ModeIndex;
        History.Insert(0, session);
        SelectedResult = session;
        Events.Clear();
        Events.Add($"{DateTime.Now:HH:mm:ss} · Запуск: {ModeText}, {TestMemoryText}");
        SaveHistory();
        Changed();

        // Сервис сообщает прогресс, а эти свойства обновляют экран через привязки WPF.
        var progress = new Progress<DiagnosticProgress>(p =>
        {
            if (!ReferenceEquals(active, session))
                return;
            if (stage != p.Stage || PassText != p.Pass.ToString())
            {
                Events.Add($"{DateTime.Now:HH:mm:ss} · Проход {p.Pass}: {p.Stage}");
                if (Events.Count > 300)
                    Events.RemoveAt(0);
            }
            stage = p.Stage;
            percent = p.Percent;
            PassText = p.Pass.ToString();
            session.ElapsedSeconds = p.ElapsedSeconds;
            session.AllocatedBytes = p.AllocatedBytes;
            session.VerifiedBytes = p.VerifiedBytes;
            session.ErrorCount = p.ErrorCount;
            session.Passes = Math.Max(0, p.Pass - 1);
            session.Events = Events.ToList();
            Changed();
        });
        runningTask = RunAsync();
        await runningTask;

        async Task RunAsync()
        {
            try
            {
                var result = await MemoryDiagnosticService.RunAsync(bytes,
                    mode == 2 ? null : TimeSpan.FromMinutes(mode == 0 ? 10 : 120), settings.StopOnFirstError, progress, cancellation.Token, extended: mode != 0);
                result.Id = session.Id;
                result.Started = session.Started;
                result.Mode = session.Mode;
                result.Configuration = session.Configuration;
                History[History.IndexOf(session)] = result;
                SelectedResult = result;
            }
            catch (Exception ex)
            {
                session.Status = "Failed";
                session.Detail = ex.Message;
                Notice = ex.Message;
            }
            finally
            {
                active = null;
                cancellation.Dispose();
                cancellation = null;
                percent = SelectedResult?.Status == "Completed" ? 100 : percent;
                SaveHistory();
                SelectedPage = 2;
                Changed();
            }
        }
    }

    private async Task BenchmarkAsync()
    {
        benchmarkRunning = true;
        BenchmarkText = "Выполняется измерение…";
        Changed();
        try
        {
            BenchmarkText = $"{DateTime.Now:HH:mm:ss} · " + (await MemoryBenchmark.RunAsync()).Summary;
        }
        catch (Exception ex) { BenchmarkText = "Измерение не выполнено: " + ex.Message; }
        finally { benchmarkRunning = false; Changed(); }
    }

    public async Task ShutdownAsync()
    {
        timer.Stop();
        cancellation?.Cancel();
        if (runningTask is not null)
            await runningTask;
        if (hardwareTask is not null)
            await hardwareTask;
        await Task.Run(hardware.Dispose);
    }

    private void SaveHistory()
    {
        if (!historyWritable || !settings.AutoSave)
            return;
        try
        {
            storage.Write("history.json", History.ToList());
        }
        catch (Exception ex) { Notice = "Не удалось сохранить историю: " + ex.Message; }
    }

    private void SaveSettings()
    {
        try
        {
            storage.Write("settings.json", settings);
        }
        catch (Exception ex) { Notice = "Не удалось сохранить настройки: " + ex.Message; }
    }

    private void Changed()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        BenchmarkCommand.Refresh();
        StartCommand.Refresh();
        StopCommand.Refresh();
        RefreshModulesCommand.Refresh();
    }
}
