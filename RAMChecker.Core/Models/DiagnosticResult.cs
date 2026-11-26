using System.ComponentModel;
using System.Text.Json.Serialization;
using RAMChecker.Services;

namespace RAMChecker.Models;

// Состояние проверки. В history.json хранится названием ("Completed")
[JsonConverter(typeof(JsonStringEnumConverter<DiagnosticStatus>))]
public enum DiagnosticStatus
{
    Running,    // идёт сейчас
    Completed,  // все проходы завершены
    Stopped,    // остановлена пользователем
    Errors,     // остановлена на первой ошибке
    Failed,     // не выполнена, например не удалось выделить память
    Interrupted // приложение закрылось во время проверки, в истории промежуточный результат
}

// Одна найденная ошибка: смещение в буфере программы (не физический адрес), ожидаемое и прочитанное значения.
public record MemoryError(long Offset, uint Expected, uint Actual, string Stage, int Pass)
{
    public string OffsetText => $"0x{Offset:X10}";
    public string ExpectedText => $"0x{Expected:X8}";
    public string ActualText => $"0x{Actual:X8}";
}

// Клетка карты памяти: состояние блока буфера и подсказка с его диапазоном адресов.
public record MemoryCell(int Index, byte State, long BlockBytes)
{
    public string TooltipText => $"Блок {Index + 1} · 0x{Index * BlockBytes:X9}–0x{(Index + 1) * BlockBytes - 1:X9} · " + State switch
    {
        MemoryDiagnosticService.Error => "найдены ошибки",
        MemoryDiagnosticService.NotChecked => "ещё не проверен",
        MemoryDiagnosticService.Checking => "обрабатывается сейчас",
        MemoryDiagnosticService.StepDone => "этап выполнен",
        MemoryDiagnosticService.Waiting => "ждёт текущего прохода",
        _ => "без ошибок"
    };

    public static MemoryCell[] From(byte[] map, long bufferBytes) => [.. map.Select((state, cellIndex) => new MemoryCell(cellIndex, state, bufferBytes / map.Length))];
}

public record LoadSample(double Seconds, double PhysicalPercent, double? CommitPercent);

public record ModuleLine(string Slot, string Title, string Specs);

// Итог одной проверки памяти; сохраняется в историю и выгружается в отчёт.
public class DiagnosticResult : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public DateTimeOffset Started { get; set; } = DateTimeOffset.Now;
    public string AlgorithmName { get; set; } = MemoryDiagnosticService.Algorithm;
    public string Mode { get; set; } = "Быстрый";
    public DiagnosticStatus Status { get; set; } = DiagnosticStatus.Running;
    public long RequestedBytes { get; set; }
    public long ProcessedBytes { get; set; }
    public long ErrorCount { get; set; }
    public double ElapsedSeconds { get; set; }
    public int Passes { get; set; }
    public string Detail { get; set; } = "";
    public string Configuration { get; set; } = "";
    public List<LoadSample> Samples { get; set; } = [];
    private long observation, stride = 1;
    public void RecordLoad(double seconds, MemorySnapshot snapshot)
    {
        // Keep both endpoints across a long run. When full, halve the historical resolution.
        var points = new List<LoadSample>(Samples);
        if (observation > 0 && (observation - 1) % stride != 0)
            points.RemoveAt(points.Count - 1);
        if (points.Count >= 120)
        {
            points = points.Where((_, index) => index % 2 == 0).ToList();
            stride *= 2;
        }
        points.Add(new(seconds, snapshot.LoadPercent, snapshot.CommitLimit is > 0 && snapshot.Committed is ulong used
            ? Math.Clamp(100d * used / snapshot.CommitLimit.Value, 0, 100) : null));
        Samples = points;
        observation++;
        PropertyChanged?.Invoke(this, new(nameof(Samples)));
    }
    public List<MemoryError> Errors { get; set; } = [];
    public byte[] Map { get; set; } = new byte[MemoryDiagnosticService.MapSize];

    [JsonIgnore] public string StartedText => Started.ToLocalTime().ToString("dd MMMM yyyy, HH:mm");
    // Часы считаются целиком, без перехода в сутки: циклическая проверка может идти дольше 24 часов.
    [JsonIgnore] public string DurationText => $"{(long)(ElapsedSeconds / 3600):00}:{TimeSpan.FromSeconds(ElapsedSeconds):mm':'ss}";
    [JsonIgnore] public string MemoryText => MemorySnapshot.Format((ulong)RequestedBytes);
    // Объём, прошедший хотя бы один полный проход: после первого прохода — весь буфер, до него — только
    // зелёные блоки карты. При ранней остановке запрошенный объём проверен не весь.
    [JsonIgnore] public long CheckedBytes => RequestedBytes / Map.Length * (Passes > 0 ? Map.Length : Map.Count(cellState => cellState == MemoryDiagnosticService.Ok));
    // Красный блок первого прохода мог пройти оба обхода, а мог быть остановлен на ошибке — карта этого не различает.
    // Поэтому в таком случае объём указан как нижняя граница.
    [JsonIgnore] public string CheckedText => (Passes == 0 && Map.Contains(MemoryDiagnosticService.Error) ? "не менее " : "")
        + MemorySnapshot.Format((ulong)CheckedBytes);
    [JsonIgnore] public string Description => $"{Mode} · {DurationText} · {MemoryText}";
    [JsonIgnore] public bool HasErrors => ErrorCount > 0;
    // Уровень для цвета в списке: 0 — не завершена, 1 — без ошибок, 2 — есть ошибки.
    [JsonIgnore] public int Level => ErrorCount > 0 ? 2 : Status == DiagnosticStatus.Completed ? 1 : 0;
    [JsonIgnore] public MemoryCell[] Cells => MemoryCell.From(Map, RequestedBytes);
    // Строка конфигурации «слот: производитель модель, объём, скорость, тайминги» делится на три части для карточки планки.
    [JsonIgnore] public ModuleLine[] ConfigurationModules => [.. Configuration.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
    {
        var slotAndRest = line.Split(": ", 2);
        var titleAndSpecs = slotAndRest[^1].Split(", ", 2);
        return new ModuleLine(slotAndRest.Length > 1 ? slotAndRest[0] : "", titleAndSpecs[0], titleAndSpecs.Length > 1 ? titleAndSpecs[1].Replace(", ", " · ") : "");
    })];

    // Переносит в идущую проверку данные отчёта о ходе. Автосохранение раз в 10 секунд должно хранить
    // согласованные счётчик, карту и список ошибок (не больше первых 1000, как в итоге), чтобы после
    // аварийного закрытия история показывала то же, что было на экране.
    public void ApplyProgress(DiagnosticProgress progress)
    {
        ProcessedBytes = progress.ProcessedBytes;
        (ElapsedSeconds, ErrorCount, Passes) = (progress.Elapsed.TotalSeconds, progress.ErrorCount, progress.Passes);
        if (progress.Map is not null)
            Map = progress.Map;
        if (progress.Errors is not null)
            Errors = [.. progress.Errors];
        // Пустое имя свойства — все привязки к объекту перечитываются, в том числе вычисляемые (статус, карта).
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    [JsonIgnore]
    public string StatusText => Status switch
    {
        _ when ErrorCount > 0 => "Обнаружены ошибки",
        DiagnosticStatus.Completed => "Ошибок не обнаружено",
        DiagnosticStatus.Stopped => "Проверка остановлена",
        DiagnosticStatus.Interrupted => "Проверка прервана",
        DiagnosticStatus.Failed => "Проверка не выполнена",
        _ => "Проверка выполняется"
    };

    [JsonIgnore]
    public string Recommendation => Status switch
    {
        _ when ErrorCount > 0 => "Сохраните отчёт. Если включён разгон или XMP, верните штатные настройки BIOS и повторите проверку. Если ошибки остаются, проверяйте планки по одной.",
        DiagnosticStatus.Completed => "В проверенном объёме ошибок не найдено. При редких сбоях запустите полную или циклическую проверку на большем объёме.",
        DiagnosticStatus.Failed => "Уменьшите объём проверки и повторите запуск.",
        _ => "Проверка не завершена, по ней нельзя судить об исправности памяти."
    };
}
