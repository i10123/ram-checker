using System.Collections.ObjectModel;
using System.Windows.Threading;
using RAMChecker.Helpers;
using RAMChecker.Models;
using RAMChecker.Services;

namespace RAMChecker.ViewModels;

// Главная модель окна: навигация и история проверок. Страницы «Обзор» и «Тестирование» — отдельные модели.
public class MainViewModel : NabludaemyObject
{
    private readonly LocalStorage storage = new();
    // Мониторинг обновляется раз в секунду.
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool historyWritable = true;
    private Task loading = Task.CompletedTask;

    public ObzorViewModel Obzor { get; }
    public TestirovanieViewModel Test { get; }
    public ObservableCollection<DiagnosticResult> History { get; } = [];
    public string[] Navigation { get; } = ["Обзор", "Тестирование", "Результаты"];
    public bool HasNotice => Notice.Length > 0;
    public bool HasHistory => History.Count > 0;
    public bool HasNoHistory => History.Count == 0;
    public Komanda DeleteResultCommand { get; }

    public MainViewModel()
    {
        Obzor = new(this);
        Test = new(this);
        DeleteResultCommand = new(DeleteResult, () => SelectedResult is { Status: not DiagnosticStatus.Running });
        timer.Tick += (_, _) =>
        {
            Obzor.Tick();
            Test.Tick();
        };
    }

    public int SelectedPage
    {
        get;
        set
        {
            field = value;
            Changed();
        }
    }

    public string Notice
    {
        get;
        set
        {
            field = value;
            Changed();
        }
    } = "";

    public DiagnosticResult? SelectedResult
    {
        get;
        set
        {
            field = value;
            Changed();
        }
    }

    public async Task InitializeAsync()
    {
        try
        {
            foreach (var r in storage.ReadHistory().OrderByDescending(r => r.Started))
                History.Add(r);
            SelectedResult = History.FirstOrDefault();
        }
        catch (Exception ex)
        {
            historyWritable = false;
            Notice = "Не удалось прочитать историю: " + ex.Message;
        }
        Obzor.Tick();
        timer.Start();
        loading = Obzor.LoadModulesAsync();
        await loading;
    }

    public void AddResult(DiagnosticResult result)
    {
        History.Insert(0, result);
        SelectedResult = result;
        SaveHistory();
    }

    private void DeleteResult()
    {
        int index = History.IndexOf(SelectedResult!);
        History.RemoveAt(index);
        SelectedResult = History.Count == 0 ? null : History[Math.Min(index, History.Count - 1)];
        SaveHistory();
    }

    // История сохраняется всегда: после каждого изменения и раз в 10 секунд во время проверки.
    public void SaveHistory()
    {
        if (!historyWritable)
            return;
        try
        {
            storage.Write("history.json", History.ToList());
        }
        catch (Exception ex) { Notice = "Не удалось сохранить историю: " + ex.Message; }
    }

    // Драйвер SPD освобождается только после фоновых операций, которые к нему обращаются: загрузки модулей
    // и чтения температуры. Замер скорости не ждём — он работает лишь со своими массивами и завершится вместе с процессом.
    public async Task ShutdownAsync()
    {
        timer.Stop();
        await Test.StopAsync();
        await loading;
        await Obzor.ShutdownAsync();
    }
}
