using System.Collections.ObjectModel;
using System.Windows;
using RAMChecker.Helpers;
using RAMChecker.Models;
using RAMChecker.Services;

namespace RAMChecker.ViewModels;

// Страница «Тестирование»: выбор режима и объёма, ход проверки и карта памяти.
public class TestirovanieViewModel : NabludaemyObject
{
    private static readonly TimeSpan?[] Durations = [TimeSpan.FromMinutes(10), TimeSpan.FromHours(2), null, null];
    private readonly MainViewModel glavnaya;
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
    public Kletka[] Kletki { get; private set; } = Kletka.Iz(new byte[MemoryDiagnosticService.MapSize], 0);
    public string SpeedText { get; private set; } = "—";
    public ObzorViewModel Obzor => glavnaya.Obzor;
    public bool IsRunning => cancellation is not null;
    public bool IsIdle => !IsRunning;
    public string ElapsedText => active?.DurationText ?? "00:00:00";
    public string ErrorCountText => (active?.ErrorCount ?? 0).ToString();
    // Для циклического и стресс-режима длительность не задана — оставшееся время неизвестно.
    public string RemainingText => active is not null && Durations[ModeIndex] is { } d
        ? TimeSpan.FromSeconds(Math.Max(0, d.TotalSeconds - active.ElapsedSeconds)).ToString(@"hh\:mm\:ss") : "—";
    public string TestMemoryText => $"{MemoryMiB:0} МБ";
    public Komanda StartCommand { get; }
    public Komanda StopCommand { get; }

    // Резерв для Windows: проверять можно только часть свободной памяти.
    public double MaxMemoryMiB => glavnaya.Obzor.Memory is { } m ? Math.Max(64, MemorySnapshot.SafeTestBytes(m) >> 20) : 64;

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

    public TestirovanieViewModel(MainViewModel glavnaya)
    {
        this.glavnaya = glavnaya;
        StartCommand = new(async () => await StartAsync(), () => !IsRunning && !glavnaya.Obzor.BenchmarkRunning && MemoryMiB <= MaxMemoryMiB);
        StopCommand = new(() => cancellation?.Cancel(), () => IsRunning);
    }

    // Промежуточное автосохранение каждые 10 секунд: история переживёт аварийное закрытие.
    public void Tick()
    {
        if (active is not null && active.ElapsedSeconds - lastSaved >= 10)
        {
            lastSaved = (long)active.ElapsedSeconds;
            glavnaya.SaveHistory();
        }
        Changed(nameof(MaxMemoryMiB)); // Свободная память меняется, вместе с ней — допустимый объём проверки.
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
        if ((ModeIndex == 3 || MemoryMiB * 2 > MaxMemoryMiB) && MessageBox.Show(
            $"Проверка займёт {TestMemoryText} и загрузит все ядра процессора. Компьютер может временно замедлиться, " +
            "сохраните открытые документы. Продолжить?", "Большая нагрузка", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        var session = active = new DiagnosticResult
        {
            Mode = Modes[ModeIndex].Split(' ')[0],
            RequestedBytes = (long)MemoryMiB << 20,
            Configuration = string.Join("\n", glavnaya.Obzor.ModulesMemory.Select(m =>
                $"{m.RAM_slot}: {m.maker} {m.RAM_partNumber}, {m.size_GB} ГБ, {m.RAM_speed}, тайминги {m.RAM_timings}"))
        };
        cancellation = new();
        lastSaved = 0;
        Events.Clear();
        Log($"Запуск: {session.Mode}, {TestMemoryText}");
        glavnaya.AddResult(session);

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
                Kletki = Kletka.Iz(p.Map, session.RequestedBytes);
            if (p.Passes > session.Passes)
                Log($"Проход {p.Passes} завершён, ошибок: {p.ErrorCount}");
            (Stage, Percent, PassText) = (p.Stage, p.Percent, p.Pass.ToString());
            session.ElapsedSeconds = p.Elapsed.TotalSeconds;
            (session.ErrorCount, session.Passes) = (p.ErrorCount, p.Passes);
            Changed();
        });
        // Запуск с ключом --imitaciya добавляет искусственную ошибку, чтобы показать, как выглядит неисправная память.
        bool imitation = Environment.GetCommandLineArgs().Contains("--imitaciya");
        if (imitation)
            Log("Имитация ошибки памяти включена");
        var task = MemoryDiagnosticService.RunAsync(session.RequestedBytes, Durations[ModeIndex], StopOnError, progress, cancellation.Token, imitation);
        running = task;
        var result = await task;
        result.Started = session.Started;
        result.Mode = session.Mode;
        result.Configuration = session.Configuration;
        glavnaya.History[glavnaya.History.IndexOf(session)] = result;
        glavnaya.SelectedResult = result;
        glavnaya.SaveHistory();
        Log(result.StatusText);
        (Stage, Kletki, Percent, SpeedText, active) = (result.StatusText, result.Kletki, result.Status == "Completed" ? 100 : Percent, "—", null);
        cancellation.Dispose();
        cancellation = null;
        MemoryMonitor.ReturnFreeMemory(); // Убираем накопившиеся за проверку снимки прогресса.
        Changed();
    }

    private void Log(string text)
    {
        Events.Insert(0, $"{DateTime.Now:HH:mm:ss} · {text}");
        if (Events.Count > 200)
            Events.RemoveAt(Events.Count - 1);
    }
}
