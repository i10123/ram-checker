using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using RAMChecker.Helpers;
using RAMChecker.Models;
using RAMChecker.Services;

namespace RAMChecker.ViewModels;

// Главная модель окна: навигация, история проверок и настройки. Страницы «Обзор» и «Тестирование» — отдельные модели.
public class MainViewModel : NabludaemyObject
{
    private readonly LocalStorage storage = new();
    private readonly DispatcherTimer timer = new();
    private AppSettings settings = new();
    private bool historyWritable = true;
    private int selectedPage;
    private string notice = "";
    private DiagnosticResult? selectedResult;

    public ObzorViewModel Obzor { get; }
    public TestirovanieViewModel Test { get; }
    public ObservableCollection<DiagnosticResult> History { get; } = [];
    public string[] Navigation { get; } = ["Обзор", "Тестирование", "Результаты", "Настройки"];
    public string[] RefreshOptions { get; } = ["1 секунда", "2 секунды", "5 секунд"];
    public string[] Themes { get; } = ["Светлая", "Тёмная"];
    public bool HasNotice => notice.Length > 0;
    public bool HasHistory => History.Count > 0;
    public bool HasNoHistory => History.Count == 0;

    public MainViewModel()
    {
        Obzor = new(this);
        Test = new(this);
        timer.Tick += (_, _) =>
        {
            Obzor.Tick();
            Test.Tick();
        };
    }

    public int SelectedPage
    {
        get => selectedPage;
        set
        {
            selectedPage = value;
            Changed();
        }
    }

    public string Notice
    {
        get => notice;
        set
        {
            notice = value;
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
        set => ChangeSettings(() => settings.AutoSave = value);
    }

    public bool StopOnError
    {
        get => settings.StopOnFirstError;
        set => ChangeSettings(() => settings.StopOnFirstError = value);
    }

    public int RefreshIndex
    {
        get => Array.IndexOf(new[] { 1, 2, 5 }, settings.RefreshSeconds) is var i and >= 0 ? i : 0;
        set => ChangeSettings(() => settings.RefreshSeconds = new[] { 1, 2, 5 }[value]);
    }

    public int ThemeIndex
    {
        get => settings.DarkTheme ? 1 : 0;
        set => ChangeSettings(() => settings.DarkTheme = value == 1);
    }

    public async Task InitializeAsync()
    {
        try
        {
            settings = storage.Read<AppSettings>("settings.json") ?? new();
            foreach (var r in storage.ReadHistory().OrderByDescending(r => r.Started))
                History.Add(r);
            SelectedResult = History.FirstOrDefault();
        }
        catch (Exception ex)
        {
            historyWritable = false;
            Notice = "Не удалось прочитать историю или настройки: " + ex.Message;
        }
        ApplySettings();
        Obzor.Tick();
        timer.Start();
        await Obzor.LoadModulesAsync();
    }

    public void AddResult(DiagnosticResult result)
    {
        History.Insert(0, result);
        SelectedResult = result;
        SaveHistory();
    }

    public void SaveHistory()
    {
        if (!historyWritable || !settings.AutoSave)
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
        Obzor.Dispose();
    }

    private void ChangeSettings(Action change)
    {
        change();
        ApplySettings();
        try
        {
            storage.Write("settings.json", settings);
        }
        catch (Exception ex) { Notice = "Не удалось сохранить настройки: " + ex.Message; }
        Changed();
    }

    private void ApplySettings()
    {
        timer.Interval = TimeSpan.FromSeconds(settings.RefreshSeconds);
        // Тема Fluent встроена в WPF и сама перекрашивает стандартные элементы.
        Application.Current.ThemeMode = settings.DarkTheme ? ThemeMode.Dark : ThemeMode.Light;
    }
}
