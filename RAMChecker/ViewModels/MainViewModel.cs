using System.Collections.ObjectModel;
using System.Windows.Threading;
using RAMChecker.Helpers;
using RAMChecker.Models;
using RAMChecker.Services;

namespace RAMChecker.ViewModels;

public record Preferences(string Language = "ru", int IntervalMs = 1000);

public class MainViewModel : ObservableObject
{
    private readonly LocalStorage storage;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool historyWritable = true;
    private Task loading = Task.CompletedTask;

    public OverviewViewModel Overview { get; }
    public TestingViewModel Test { get; }
    public ObservableCollection<DiagnosticResult> History { get; } = [];
    public int[] Intervals { get; } = [250, 500, 1000, 2000, 5000];
    private Preferences preferences = new();
    public int IntervalMs
    {
        get => preferences.IntervalMs;
        set
        {
            if (!Intervals.Contains(value) || value == IntervalMs) return;
            preferences = preferences with { IntervalMs = value };
            timer.Interval = TimeSpan.FromMilliseconds(value);
            SavePreferences();
        }
    }
    public int LanguageIndex
    {
        get => preferences.Language == "en" ? 1 : 0;
        set
        {
            if (value is not (0 or 1) || value == LanguageIndex) return;
            preferences = preferences with { Language = value == 1 ? "en" : "ru" };
            ApplyLanguage();
            SavePreferences();
        }
    }
    private void SavePreferences()
    {
        try { storage.Write("settings.json", preferences); }
        catch (Exception ex) { Notice = ex.Message; }
    }
    private void ApplyLanguage()
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo(LanguageIndex == 1 ? "en-US" : "ru-RU");
        System.Globalization.CultureInfo.CurrentUICulture = culture;
        System.Globalization.CultureInfo.CurrentCulture = culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        var language = new System.Windows.ResourceDictionary { Source = new Uri($"/RAMChecker;component/Languages/{(LanguageIndex == 1 ? "en" : "ru")}.xaml", UriKind.Relative) };
        var old = dictionaries.FirstOrDefault(d => d.Source?.OriginalString.StartsWith("/RAMChecker;component/Languages/") == true);
        dictionaries.Add(language);
        if (old != null) dictionaries.Remove(old);
        UiLanguage.Current.Set(LanguageIndex == 1);
        Overview.RefreshLanguage();
        Test.RefreshLanguage();
        foreach (var result in History) result.Refresh();
        NotifyChanged();
    }
    public bool HasNotice => Notice.Length > 0;
    public bool HasSelectedResult => SelectedResult is not null;
    public bool HasNoHistory => History.Count == 0;
    public RelayCommand DeleteResultCommand { get; }

    public MainViewModel() : this(new LocalStorage()) { }

    public MainViewModel(LocalStorage storage)
    {
        this.storage = storage;
        Overview = new(this);
        Test = new(this);
        try { preferences = storage.Read<Preferences>("settings.json") ?? new(); }
        catch (Exception ex) { Notice = ex.Message; }
        if (!Intervals.Contains(IntervalMs)) preferences = preferences with { IntervalMs = 1000 };
        timer.Interval = TimeSpan.FromMilliseconds(IntervalMs);
        DeleteResultCommand = new(DeleteResult, () => SelectedResult is { Status: not DiagnosticStatus.Running });
        ApplyLanguage();
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
        await Overview.CloseSensorsAsync();
    }
}
