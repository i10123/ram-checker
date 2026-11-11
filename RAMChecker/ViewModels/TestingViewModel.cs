using System.Collections.ObjectModel;
using System.Media;
using System.Windows;
using System.Windows.Shell;
using RAMChecker.Helpers;
using RAMChecker.Models;
using RAMChecker.Services;

namespace RAMChecker.ViewModels;

// Страница «Тестирование»: выбор режима и объёма, ход проверки и карта памяти.
public class TestingViewModel : ObservableObject
{
    private static readonly TimeSpan?[] Durations = [TimeSpan.FromMinutes(10), TimeSpan.FromHours(2), null, null];
    private readonly MainViewModel main;
    private CancellationTokenSource? cancellation;
    private Task? running;
    private DiagnosticResult? active;
    private long lastSaved;

    public string[] Modes { get; } = ["Быстрая · 10 минут", "Полная · 2 часа", "Циклическая · до остановки", "Стресс · вся свободная память"];
    public int ModeIndex { get; set; }
    public bool StopOnError { get; set; }
    public ObservableCollection<string> Events { get; } = [];
    public string Stage { get; private set; } = "Готово к запуску";
    public double Percent { get; private set; }
    public string PassText { get; private set; } = "—";
    public MemoryCell[] Cells { get; private set; } = MemoryCell.From(new byte[MemoryDiagnosticService.MapSize], 0);
    public string SpeedText { get; private set; } = "—";
    // Итог последней проверки для плашки на странице; при новом запуске плашка скрывается.
    public DiagnosticResult? LastResult { get; private set; }
    // Плашка нужна только для настоящего итога: остановку пользователь выбрал сам, о ней сообщает заголовок.
    // Найденные до остановки ошибки показываются всегда.
    public bool HasLastResult => LastResult is { HasErrors: true } or { Status: not DiagnosticStatus.Stopped };
    private bool resultOnTaskbar;

    // Значок в панели задач: полоса растёт по ходу проверки и краснеет при ошибках.
    // Итог остаётся на значке, пока пользователь не вернётся в окно.
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
    // После завершения цифры берутся из итога, чтобы они совпадали с плашкой.
    public string ElapsedText => (active ?? LastResult)?.DurationText ?? "00:00:00";
    public string ErrorCountText => ((active ?? LastResult)?.ErrorCount ?? 0).ToString();
    // Для циклического и стресс-режима длительность не задана — оставшееся время неизвестно.
    public string RemainingText => active is not null && Durations[ModeIndex] is { } d
        ? TimeSpan.FromSeconds(Math.Max(0, d.TotalSeconds - active.ElapsedSeconds)).ToString(@"hh\:mm\:ss") : "—";
    public string TestMemoryText => $"{MemoryMiB:0} МБ";
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }

    // Резерв для Windows: проверять можно только часть свободной памяти. Безопасный объём может быть и меньше
    // минимальных 64 МБ — тогда запуск запрещён, а ползунку всё равно нужен максимум не меньше минимума.
    private static double SafeMiB(MemorySnapshot? m) => m is null ? 0 : MemorySnapshot.SafeTestBytes(m) >> 20;
    public double MaxMemoryMiB => Math.Max(64, SafeMiB(main.Overview.Memory));
    public bool MemoryShort => IsIdle && MemoryMiB > SafeMiB(main.Overview.Memory);

    // Объём кратен 16 МБ, чтобы буфер делился на 256 равных блоков карты.
    public double MemoryMiB
    {
        get;
        set
        {
            field = Math.Max(64, Math.Floor(value / 16) * 16);
            Changed();
        }
    } = 512;

    public TestingViewModel(MainViewModel main)
    {
        this.main = main;
        // Задача запуска запоминается, чтобы при закрытии окна дождаться сохранения результата.
        StartCommand = new(() => running = StartAsync(), () => !IsRunning && !main.Overview.BenchmarkRunning && !MemoryShort);
        StopCommand = new(() => cancellation?.Cancel(), () => IsRunning);
    }

    // Промежуточное автосохранение каждые 10 секунд: история переживёт аварийное закрытие.
    public void Tick()
    {
        if (active is not null && active.ElapsedSeconds - lastSaved >= 10)
        {
            lastSaved = (long)active.ElapsedSeconds;
            main.SaveHistory();
        }
        Changed(nameof(MaxMemoryMiB), nameof(MemoryShort)); // Свободная память меняется, вместе с ней — допустимый объём проверки.
    }

    public async Task StopAsync()
    {
        cancellation?.Cancel();
        if (running is not null)
            await running;
    }

    // Кнопка не ожидает эту задачу, поэтому сбой проверки обрабатывается внутри метода.
    private async Task StartAsync()
    {
        // Стресс-режим берёт весь допустимый объём, чтобы нагрузить память и все ядра до предела.
        if (ModeIndex == 3)
            MemoryMiB = MaxMemoryMiB;
        // Предупреждение об опасной операции: большой буфер может замедлить Windows и другие программы.
        if ((ModeIndex == 3 || MemoryMiB * 2 > MaxMemoryMiB) && MessageBox.Show(
            $"Проверка займёт {TestMemoryText} и загрузит все ядра процессора. Компьютер может временно замедлиться, " +
            "сохраните открытые документы. Продолжить?", "Большая нагрузка", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        // Пока был открыт вопрос, свободной памяти могло стать меньше, поэтому перед выделением буфера проверяем снова.
        if (MemoryMiB > SafeMiB(MemoryMonitor.Read()))
        {
            MessageBox.Show("Свободной памяти стало меньше, чем нужно для проверки с резервом для Windows. Уменьшите объём.",
                "Недостаточно памяти", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var session = new DiagnosticResult
        {
            Mode = Modes[ModeIndex].Split(' ')[0],
            RequestedBytes = (long)MemoryMiB << 20,
            Configuration = string.Join("\n", main.Overview.ModulesMemory.Select(m =>
                $"{m.RAM_slot}: {m.RAM_maker} {m.RAM_partNumber}, {m.size_GB} ГБ, {m.RAM_speed}, тайминги {m.RAM_timings}, паспорт SPD: {m.RAM_spd}"))
        };
        (lastSaved, LastResult) = (0, null);
        Events.Clear();
        Log($"Запуск: {session.Mode}, {TestMemoryText}");
        main.AddResult(session);

        // Сервис сообщает прогресс из фонового потока, Progress переносит его в поток интерфейса.
        var last = (Seconds: 0d, Bytes: 0L);
        var progress = new Progress<DiagnosticProgress>(p =>
        {
            // Скорость — сколько гигабайт буфера записано и проверено за время с прошлого отчёта.
            if (p.Elapsed.TotalSeconds - last.Seconds >= 0.5)
            {
                SpeedText = $"{(p.ProcessedBytes - last.Bytes) / (p.Elapsed.TotalSeconds - last.Seconds) / (1L << 30):0.0} ГБ/с";
                last = (p.Elapsed.TotalSeconds, p.ProcessedBytes);
            }
            if (p.Map is not null)
                Cells = MemoryCell.From(p.Map, session.RequestedBytes);
            if (p.Passes > session.Passes)
                Log($"Проход {p.Passes} завершён, ошибок: {p.ErrorCount}");
            (Stage, Percent, PassText) = (p.Stage, p.Percent, p.Pass.ToString());
            session.ApplyProgress(p);
            Changed();
        });
        // Запуск с ключом --imitation добавляет искусственную ошибку, чтобы показать, как выглядит неисправная память.
        bool imitation = Environment.GetCommandLineArgs().Contains("--imitation");
        if (imitation)
            Log("Имитация ошибки памяти включена");
        (active, cancellation) = (session, new());
        MemoryMonitor.KeepAwake(true);
        DiagnosticResult result;
        try
        {
            result = await MemoryDiagnosticService.RunAsync(session.RequestedBytes, Durations[ModeIndex], StopOnError, progress, cancellation.Token, imitation);
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
        Log(result.StatusText);
        (Stage, Cells, Percent, SpeedText, LastResult) = (result.StatusText, result.Cells, result.Status == DiagnosticStatus.Completed ? 100 : Percent, "—", result);
        MemoryMonitor.ReturnFreeMemory(); // Убираем накопившиеся за проверку снимки прогресса.
        // Если окно свёрнуто или в фоне, о завершении сообщают итог на значке и системный звук.
        resultOnTaskbar = !Application.Current.MainWindow.IsActive;
        (result.HasErrors ? SystemSounds.Hand : SystemSounds.Asterisk).Play();
        Changed();
    }

    public void WindowActivated()
    {
        resultOnTaskbar = false;
        Changed(nameof(TaskbarState));
    }

    private void Log(string text)
    {
        Events.Insert(0, $"{DateTime.Now:HH:mm:ss} · {text}");
        if (Events.Count > 200)
            Events.RemoveAt(Events.Count - 1);
    }
}
