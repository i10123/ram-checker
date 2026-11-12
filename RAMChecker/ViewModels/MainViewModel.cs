using System.Collections.ObjectModel;
using System.Windows.Threading;
using RAMChecker.Helpers;
using RAMChecker.Models;
using RAMChecker.Services;

namespace RAMChecker.ViewModels;

// Главная модель окна: навигация и история проверок. Страницы «Обзор» и «Тестирование» — отдельные модели.
public class MainViewModel : ObservableObject
{
    private readonly LocalStorage storage = new();
    // Мониторинг обновляется раз в секунду.
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool historyWritable = true;
    private Task loading = Task.CompletedTask;

    public OverviewViewModel Overview { get; }
    public TestingViewModel Test { get; }
    public ObservableCollection<DiagnosticResult> History { get; } = [];
    public string[] Navigation { get; } = ["Обзор", "Тестирование", "Результаты"];
    public bool HasNotice => Notice.Length > 0;
    // Подробности показываются только для выбранной проверки: выделение в списке можно снять щелчком с Ctrl.
    public bool HasSelectedResult => SelectedResult is not null;
    public bool HasNoHistory => History.Count == 0;
    public RelayCommand DeleteResultCommand { get; }

    public MainViewModel()
    {
        Overview = new(this);
        Test = new(this);
        DeleteResultCommand = new(DeleteResult, () => SelectedResult is { Status: not DiagnosticStatus.Running });
        timer.Tick += (_, _) =>
        {
            Overview.Tick();
            Test.Tick();
        };
    }

    public int SelectedPage
    {
        get;
        set
        {
            // Щелчок с Ctrl снимает выделение в меню (индекс -1) — тогда остаётся прежняя страница.
            if (value >= 0)
                field = value;
            Changed();
            if (field == 1)
                Overview.UpdateChart(); // График не пересчитывался, пока страница теста была скрыта.
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
            var items = storage.ReadHistory(out int skipped);
            foreach (var r in items.OrderByDescending(r => r.Started))
                History.Add(r);
            if (skipped != 0)
                Notice = (skipped > 0 ? $"Пропущено повреждённых записей истории: {skipped}." : "Файл истории повреждён, история начата заново.")
                    + " Исходный файл сохранён как history.bad.json.";
            SelectedResult = History.FirstOrDefault();
        }
        catch (Exception ex)
        {
            historyWritable = false;
            Notice = "Не удалось прочитать историю: " + ex.Message;
        }
        Overview.Tick();
        timer.Start();
        loading = Overview.LoadModulesAsync();
        await loading;
        // Запуск окна и чтение WMI оставляют много временных объектов — сразу возвращаем эту память Windows.
        MemoryMonitor.ReturnFreeMemory();
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
        await Overview.ShutdownAsync();
    }
}
