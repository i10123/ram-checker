using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Media;
using RAMChecker.Helpers;
using RAMChecker.Models;
using RAMChecker.Services;

namespace RAMChecker.ViewModels;

public record Stolbec(string Name, string Value, double Width);

// Страница «Обзор»: модули памяти, загрузка в реальном времени и замер скорости.
public class ObzorViewModel : NabludaemyObject, IDisposable
{
    private readonly MainViewModel glavnaya;
    private readonly Queue<double> samples = new();
    private SpdReader? spd;
    private Task? temperatureTask;
    private ModuleMemoryModel? selectedModule;

    public ObservableCollection<ModuleMemoryModel> ModulesMemory { get; } = [];
    public MemorySnapshot? Memory { get; private set; }
    public PointCollection UsagePoints { get; private set; } = [];
    public PointCollection UsageArea { get; private set; } = [];
    public List<Stolbec> BenchmarkBars { get; private set; } = [];
    public string BenchmarkText { get; private set; } = "Замер ещё не выполнялся";
    public bool BenchmarkRunning { get; private set; }
    public string SpdStatus { get; private set; } = "";
    public bool NeedsAdministrator { get; } = !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public Komanda BenchmarkCommand { get; }
    public Komanda AdministratorCommand { get; }

    public string TotalText => Memory is null ? "—" : MemorySnapshot.Format(Memory.Total);
    public string UsedText => Memory is null ? "—" : $"{MemorySnapshot.Format(Memory.Used)} · {Memory.Load:0}%";
    public string LoadText => Memory is null ? "—" : $"{Memory.Load:0}%";
    public string AvailableText => Memory is null ? "—" : MemorySnapshot.Format(Memory.Available);
    public string ModulesText => $"{ModulesMemory.Count} шт. · {ModulesMemory.FirstOrDefault()?.RAM_type ?? "—"}";

    public ObzorViewModel(MainViewModel glavnaya)
    {
        this.glavnaya = glavnaya;
        BenchmarkCommand = new(async () => await BenchmarkAsync(), () => !BenchmarkRunning && !glavnaya.Test.IsRunning);
        AdministratorCommand = new(RestartAsAdministrator, () => !BenchmarkRunning && !glavnaya.Test.IsRunning);
    }

    public ModuleMemoryModel? SelectedModule
    {
        get => selectedModule;
        set
        {
            selectedModule = value;
            Changed();
        }
    }

    // Строки паспорта выбранного модуля для таблицы «характеристика — значение».
    public List<KeyValuePair<string, string>> ModuleRows => SelectedModule is not { } m ? [] :
    [
        new("Производитель", m.maker), new("Модель", m.RAM_partNumber), new("Серийный номер", m.RAM_serialNumber),
        new("Тип", $"{m.RAM_type} {m.RAM_formFactor}"), new("Объём", $"{m.size_GB} ГБ"), new("Слот", m.RAM_slot),
        new("Текущая частота", m.RAM_speed), new("Номинальная частота", m.RAM_nominalSpeed),
        new("Тайминги CL-tRCD-tRP-tRAS", m.RAM_timings), new("tRC", m.RAM_tRC),
        new("Напряжение", m.RAM_voltage), new("Температура", m.RAM_temperature)
    ];

    public async Task LoadModulesAsync()
    {
        try
        {
            var modules = await Task.Run(() => new WmiServiceMemory().GetModulesMemory());
            if (!NeedsAdministrator)
                await Task.Run(() => ReadSpd(modules));
            foreach (var m in modules)
                ModulesMemory.Add(m);
            SelectedModule = ModulesMemory.FirstOrDefault();
        }
        catch (Exception ex) { glavnaya.Notice = "Не удалось получить сведения о модулях: " + ex.Message; }
    }

    // Паспорт SPD сопоставляется с модулями WMI по порядку слотов.
    private void ReadSpd(List<ModuleMemoryModel> modules)
    {
        try
        {
            spd = new SpdReader();
            var infos = spd.ReadAll();
            foreach (var (m, info) in modules.Zip(infos))
            {
                m.SpdAddress = info.Address;
                m.RAM_nominalSpeed = $"{info.NominalSpeed} MT/s ({info.NominalSpeed / 2} МГц)";
                m.RAM_timings = $"{info.CL}-{info.RCD}-{info.RP}-{info.RAS}";
                m.RAM_tRC = info.RC.ToString();
                // Датчик температуры есть не на всех планках: о нём сообщает байт 14 паспорта SPD.
                if (!info.HasSensor)
                    (m.SpdAddress, m.RAM_temperature) = (0, "Датчика на модуле нет");
            }
            SpdStatus = infos.Count == 0 ? "Микросхемы SPD не найдены на шине SMBus." : "";
        }
        catch (DllNotFoundException) { SpdStatus = "Для таймингов и температуры установите драйвер PawnIO."; }
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
            // График последних 60 измерений в области 600 × 160; заливка замыкается по нижнему краю.
            var line = samples.Select((load, i) => new Point((60 - samples.Count + i) * 600 / 59d, 160 - load * 1.6)).ToList();
            UsagePoints = new PointCollection(line);
            UsageArea = new PointCollection(line.Append(new(600, 160)).Append(new(line[0].X, 160)));
        }
        catch (Exception ex) { glavnaya.Notice = "Мониторинг недоступен: " + ex.Message; }
        if (spd is not null && temperatureTask is not { IsCompleted: false })
            temperatureTask = Task.Run(() =>
            {
                foreach (var m in ModulesMemory.Where(m => m.SpdAddress != 0))
                    m.RAM_temperature = spd.Temperature(m.SpdAddress) is double t ? $"{t:0.0} °C" : ModuleMemoryModel.NetDannyh;
            });
        Changed();
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
                .Select(b => new Stolbec(b.Item1, $"{b.Item2:N0} МБ/с", b.Item2 / max * 200))];
            BenchmarkText = $"Задержка: {r.LatencyNs:0.0} нс";
        }
        catch (Exception ex) { BenchmarkText = "Замер не выполнен: " + ex.Message; }
        GC.Collect(); // Буферы замера больше не нужны — возвращаем память системе.
        BenchmarkRunning = false;
        Changed();
    }

    private void RestartAsAdministrator()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            Application.Current.MainWindow.Close();
        }
        catch (Exception) { } // Пользователь отменил запрос UAC — продолжаем без прав администратора.
    }

    public void Dispose()
    {
        temperatureTask?.Wait();
        spd?.Dispose();
    }
}
