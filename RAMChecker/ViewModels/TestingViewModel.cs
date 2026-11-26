using System.Media;
using System.Windows;
using System.Windows.Shell;
using RAMChecker.Helpers;
using RAMChecker.Models;
using RAMChecker.Services;

namespace RAMChecker.ViewModels;

public class TestingViewModel : ObservableObject
{
    private static readonly TimeSpan?[] Durations = [TimeSpan.FromMinutes(10), TimeSpan.FromHours(2), null, null];
    private readonly MainViewModel main;
    private CancellationTokenSource? cancellation;
    private Task? running;
    private DiagnosticResult? active;
    private long lastSaved;
    private readonly System.Diagnostics.Stopwatch sampleTime = new();

    public string[] Modes { get; } = ["Быстрый · 10 минут", "Полный · 2 часа", "Циклический · до остановки", "Стресс · вся свободная память"];
    public int ModeIndex { get; set; }
    public bool StopOnError { get; set; }
    public string Stage { get; private set; } = "Готово к запуску";
    public double Percent { get; private set; }
    public string PassText { get; private set; } = "—";
    public MemoryCell[] Cells { get; private set; } = MemoryCell.From(new byte[MemoryDiagnosticService.MapSize], 0);
    public string SpeedText { get; private set; } = "—";
    public DiagnosticResult? LastResult { get; private set; }
    public bool HasLastResult => LastResult is { HasErrors: true } or { Status: not DiagnosticStatus.Stopped };
    private bool resultOnTaskbar;

    public TaskbarItemProgressState TaskbarState => (active ?? (resultOnTaskbar ? LastResult : null)) switch
    {
        null => TaskbarItemProgressState.None,
        { ErrorCount: > 0 } => TaskbarItemProgressState.Error,
        { Status: DiagnosticStatus.Running or DiagnosticStatus.Completed } => TaskbarItemProgressState.Normal,
        _ => TaskbarItemProgressState.Paused
    };
    public double TaskbarValue => IsRunning ? Percent / 100 : 1;
    public OverviewViewModel Overview => main.Overview;
    public bool IsRunning => cancellation is not null;
    public bool IsIdle => !IsRunning;
    public string ElapsedText => (active ?? LastResult)?.DurationText ?? "00:00:00";
    public string ErrorCountText => ((active ?? LastResult)?.ErrorCount ?? 0).ToString();
    public string RemainingText => active is not null && Durations[ModeIndex] is { } modeDuration
        ? TimeSpan.FromSeconds(Math.Max(0, modeDuration.TotalSeconds - active.ElapsedSeconds)).ToString(@"hh\:mm\:ss") : "—";
    public string TestMemoryText => $"{MemoryMiB:0} МБ";
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }

    private static double SafeMiB(MemorySnapshot? module) => module is null ? 0 : MemorySnapshot.SafeTestBytes(module) >> 20;
    public double MaxMemoryMiB => Math.Max(IsRunning ? MemoryMiB : 64, SafeMiB(main.Overview.Memory));
    public bool MemoryShort => IsIdle && (ModeIndex == 3 ? 64 : MemoryMiB) > SafeMiB(main.Overview.Memory);

    // Объём кратен 16 МБ, чтобы буфер делился на 256 равных блоков карты.
    public double MemoryMiB
    {
        get;
        set
        {
            field = Math.Max(64, Math.Floor(value / 16) * 16);
            NotifyChanged();
        }
    } = 512;

    public TestingViewModel(MainViewModel main)
    {
        this.main = main;
        StartCommand = new(() => running = StartAsync(), () => !IsRunning && !main.Overview.BenchmarkRunning && !main.Overview.Loading && !MemoryShort);
        StopCommand = new(() => cancellation?.Cancel(), () => IsRunning);
    }

    // Промежуточное автосохранение каждые 10 секунд
    public void Tick()
    {
        if (active != null && main.Overview.Memory is { } snapshot)
            active.RecordLoad(sampleTime.Elapsed.TotalSeconds, snapshot, Overview.RecentClockMHz);
        if (active is not null && active.ElapsedSeconds - lastSaved >= 10)
        {
            lastSaved = (long)active.ElapsedSeconds;
            main.SaveHistory();
        }
        NotifyChanged(nameof(MaxMemoryMiB), nameof(MemoryShort));
    }

    public async Task StopAsync()
    {
        cancellation?.Cancel();
        if (running is not null)
            await running;
    }

    private async Task StartAsync()
    {
        // Стресс-режим берёт весь допустимый объём, чтобы нагрузить память и все ядра до предела.
        if (ModeIndex == 3)
            MemoryMiB = MaxMemoryMiB;
        // Предупреждение об опасной операции: большой буфер может замедлить Windows и другие программы.
        // Предупреждение об опасной операции: большой буфер может замедлить Windows и другие программы.
        if ((ModeIndex == 3 || MemoryMiB * 2 > MaxMemoryMiB) && MessageBox.Show(
            UiLanguage.Current.English ? $"The test will use {UiLanguage.Current.Display(TestMemoryText)} and load all CPU cores. Save open documents before continuing. Continue?" : $"Проверка займёт {TestMemoryText} и загрузит все ядра процессора. Компьютер может временно замедлиться, " +
            "сохраните открытые документы. Продолжить?", RAMChecker.Helpers.UiLanguage.Current.Display("Большая нагрузка"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        // Пока был открыт вопрос, свободной памяти могло стать меньше, поэтому перед выделением буфера проверяем снова.
        if (MemoryMiB > SafeMiB(MemoryMonitor.Read()))
        {
            MessageBox.Show(RAMChecker.Helpers.UiLanguage.Current.Display("Свободной памяти стало меньше, чем нужно для проверки с резервом для Windows. Уменьшите объём."),
                RAMChecker.Helpers.UiLanguage.Current.Display("Недостаточно памяти"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // Запуск с ключом --imitation добавляет искусственную ошибку, чтобы показать, как выглядит неисправная память.
        bool imitation = Environment.GetCommandLineArgs().Contains("--imitation");
        var session = new DiagnosticResult
        {
            Mode = Modes[ModeIndex].Split(' ')[0],
            RequestedBytes = (long)MemoryMiB << 20,
            Configuration = string.Join("\n", main.Overview.ModulesMemory.Select(module =>
                $"{module.RAM_slot}: {module.RAM_maker} {module.RAM_partNumber}, {module.Size_GB} ГБ, {module.RAM_speed.Split(" (")[0]}"))
        };
        (lastSaved, LastResult) = (0, null);
        sampleTime.Restart();
        if (main.Overview.Memory is { } first) session.RecordLoad(0, first, Overview.RecentClockMHz);
        main.AddResult(session);

        // Сервис сообщает прогресс из фонового потока, Progress переносит его в поток интерфейса.
        var lastSpeedSample = (Seconds: 0d, Bytes: 0L);
        var progress = new Progress<DiagnosticProgress>(progressReport =>
        {
            // Скорость — сколько гигабайт буфера записано и проверено за время с прошлого отчёта.
            if (progressReport.Elapsed.TotalSeconds - lastSpeedSample.Seconds >= 0.5)
            {
                SpeedText = $"{(progressReport.ProcessedBytes - lastSpeedSample.Bytes) / (progressReport.Elapsed.TotalSeconds - lastSpeedSample.Seconds) / (1L << 30):0.0} ГБ/с";
                lastSpeedSample = (progressReport.Elapsed.TotalSeconds, progressReport.ProcessedBytes);
            }
            if (progressReport.Map is not null)
                Cells = MemoryCell.From(progressReport.Map, session.RequestedBytes);
            (Stage, Percent, PassText) = (progressReport.Stage, progressReport.Percent, progressReport.Pass.ToString());
            session.ApplyProgress(progressReport);
            NotifyChanged();
        });
        (active, cancellation) = (session, new());
        MemoryMonitor.KeepAwake(true);
        DiagnosticResult result;
        try
        {
            result = await MemoryDiagnosticService.RunAsync(session.RequestedBytes, Durations[ModeIndex], StopOnError, progress, cancellation.Token, imitation);
            result.Samples = session.Samples;
            result.Started = session.Started;
            result.Mode = session.Mode;
            result.Configuration = session.Configuration;
        }
        catch (Exception ex)
        {
            // Ошибки самой проверки сервис превращает в статус; сюда попадают неверные параметры и непредвиденные сбои.
            result = session;
            (result.Status, result.Detail) = (DiagnosticStatus.Failed, ex.Message);
        }
        finally
        {
            // При любом исходе снимается запрет сна и освобождается отмена — проверку можно запустить снова.
            MemoryMonitor.KeepAwake(false);
            cancellation.Dispose();
            (active, cancellation) = (null, null);
        }
        main.History[main.History.IndexOf(session)] = result;
        main.SelectedResult = result;
        main.SaveHistory();
        (Stage, Cells, Percent, SpeedText, LastResult) = (result.StatusText, result.Cells, result.Status == DiagnosticStatus.Completed ? 100 : Percent, "—", result);
        await main.Overview.ReadWheaAsync(); // За время проверки Windows могла записать новые ошибки памяти.
        MemoryMonitor.ReturnFreeMemory(); // Убираем накопившиеся за проверку снимки прогресса.
        // Если окно свёрнуто или в фоне, о завершении сообщают итог на значке и системный звук.
        resultOnTaskbar = !Application.Current.MainWindow.IsActive;
        (result.HasErrors ? SystemSounds.Hand : SystemSounds.Asterisk).Play();
        NotifyChanged();
    }

    public void RefreshLanguage() => NotifyChanged();

    public void WindowActivated()
    {
        resultOnTaskbar = false;
        NotifyChanged(nameof(TaskbarState));
    }
}
