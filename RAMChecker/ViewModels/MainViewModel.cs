using System.Collections.ObjectModel;
using System.Windows.Threading;
using RAMChecker.Helpers;
using RAMChecker.Models;
using RAMChecker.Services;

namespace RAMChecker.ViewModels;

public class MainViewModel : ObservableObject
{
    private readonly LocalStorage storage = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool historyWritable = true;
    private Task loading = Task.CompletedTask;

    public OverviewViewModel Overview { get; }
    public TestingViewModel Test { get; }
    public ObservableCollection<DiagnosticResult> History { get; } = [];
    public string[] Navigation { get; } = ["Обзор", "Тестирование", "Результаты"];
    public bool HasNotice => Notice.Length > 0;
    public bool HasSelectedResult => SelectedResult is not null;
    public bool HasNoHistory => History.Count == 0;
    public RelayCommand DeleteResultCommand { get; }

    public MainViewModel()
    {
        Overview = new(this);
        Test = new(this);
        DeleteResultCommand = new(DeleteResult, () => SelectedResult is { Status: not DiagnosticStatus.Running });
        timer.Tick += (sender, eventArgs) =>
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
            if (value >= 0)
                field = value;
            NotifyChanged();
            if (field == 1)
                Overview.UpdateChart();
        }
    }

    public string Notice
    {
        get;
        set
        {
            field = value;
            NotifyChanged();
        }
    } = "";

    public DiagnosticResult? SelectedResult
    {
        get;
        set
        {
            field = value;
            NotifyChanged();
        }
    }

    public async Task InitializeAsync()
    {
        try
        {
            var loadedItems = storage.ReadHistory(out int skipped);
            foreach (var historyItem in loadedItems.OrderByDescending(historyItem => historyItem.Started))
                History.Add(historyItem);
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

    public async Task ShutdownAsync()
    {
        timer.Stop();
        await Test.StopAsync();
        await loading;
    }
}
