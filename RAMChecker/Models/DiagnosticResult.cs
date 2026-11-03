using System.Text.Json.Serialization;
using RAMChecker.Services;

namespace RAMChecker.Models;

// Состояние проверки. В history.json хранится названием ("Completed"), как раньше хранились строки,
// поэтому история прежних версий читается без изменений, а опечатку в названии поймает компилятор.
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
public record Kletka(int Index, byte State, long BlockBytes)
{
    public string Tip => $"Блок {Index + 1} · 0x{Index * BlockBytes:X9}–0x{(Index + 1) * BlockBytes - 1:X9} · " + State switch
    {
        MemoryDiagnosticService.Error => "найдены ошибки",
        MemoryDiagnosticService.NotChecked => "ещё не проверен",
        MemoryDiagnosticService.Waiting => "ждёт текущего прохода",
        _ => "без ошибок"
    };

    public static Kletka[] Iz(byte[] map, long bytes) => [.. map.Select((state, i) => new Kletka(i, state, bytes / map.Length))];
}

// Итог одной проверки памяти; сохраняется в историю и выгружается в отчёт.
public class DiagnosticResult
{
    public DateTimeOffset Started { get; set; } = DateTimeOffset.Now;
    public string Mode { get; set; } = "Быстрая";
    public DiagnosticStatus Status { get; set; } = DiagnosticStatus.Running;
    public long RequestedBytes { get; set; }
    // Сколько удалось выделить; при сбое выделения меньше запрошенного. Выделенный не значит проверенный.
    public long AllocatedBytes { get; set; }
    public long ErrorCount { get; set; }
    public double ElapsedSeconds { get; set; }
    public int Passes { get; set; }
    public string Detail { get; set; } = "";
    public string Configuration { get; set; } = "";
    public List<MemoryError> Errors { get; set; } = [];
    public byte[] Map { get; set; } = new byte[MemoryDiagnosticService.MapSize];

    [JsonIgnore] public string StartedText => Started.ToLocalTime().ToString("dd MMMM yyyy, HH:mm");
    [JsonIgnore] public string DurationText => TimeSpan.FromSeconds(ElapsedSeconds).ToString(@"hh\:mm\:ss");
    [JsonIgnore] public string MemoryText => MemorySnapshot.Format((ulong)RequestedBytes);
    // Объём, прошедший хотя бы один полный проход: после первого прохода — весь буфер, до него — только
    // зелёные блоки карты. При ранней остановке запрошенный объём проверен не весь.
    [JsonIgnore] public long CheckedBytes => RequestedBytes / Map.Length * (Passes > 0 ? Map.Length : Map.Count(s => s == MemoryDiagnosticService.Ok));
    // Красный блок первого прохода мог пройти оба обхода, а мог быть остановлен на ошибке — карта этого не различает.
    // Поэтому в таком случае объём указан как нижняя граница.
    [JsonIgnore] public string CheckedText => (Passes == 0 && Map.Contains(MemoryDiagnosticService.Error) ? "не менее " : "")
        + MemorySnapshot.Format((ulong)CheckedBytes);
    [JsonIgnore] public string Description => $"{Mode} · {DurationText} · {MemoryText}";
    [JsonIgnore] public bool HasErrors => ErrorCount > 0;
    // Уровень для цвета в списке: 0 — не завершена, 1 — без ошибок, 2 — есть ошибки.
    [JsonIgnore] public int Uroven => ErrorCount > 0 ? 2 : Status == DiagnosticStatus.Completed ? 1 : 0;
    [JsonIgnore] public Kletka[] Kletki => Kletka.Iz(Map, RequestedBytes);

    // Переносит в идущую проверку данные отчёта о ходе. Автосохранение раз в 10 секунд должно хранить
    // согласованные счётчик, карту и список ошибок (не больше первых 1000, как в итоге), чтобы после
    // аварийного закрытия история показывала то же, что было на экране.
    public void ApplyProgress(DiagnosticProgress p)
    {
        (ElapsedSeconds, ErrorCount, Passes) = (p.Elapsed.TotalSeconds, p.ErrorCount, p.Passes);
        if (p.Map is not null)
            Map = p.Map;
        if (p.Errors is not null)
            Errors = [.. p.Errors];
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
