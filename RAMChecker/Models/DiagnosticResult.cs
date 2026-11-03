using System.Text.Json.Serialization;
using RAMChecker.Services;

namespace RAMChecker.Models;

// Одна найденная ошибка: смещение в буфере, ожидаемое и прочитанное значения.
public record MemoryError(long Offset, uint Expected, uint Actual, string Stage, int Pass)
{
    public string OffsetText => $"0x{Offset:X10}";
    public string ExpectedText => $"0x{Expected:X8}";
    public string ActualText => $"0x{Actual:X8}";
}

// Итог одной проверки памяти; сохраняется в историю и выгружается в отчёт.
public class DiagnosticResult
{
    public DateTimeOffset Started { get; set; } = DateTimeOffset.Now;
    public string Mode { get; set; } = "Быстрая";
    public string Status { get; set; } = "Running";
    public long RequestedBytes { get; set; }
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
    [JsonIgnore] public string Description => $"{Mode} · {DurationText} · {MemoryText}";
    [JsonIgnore] public bool HasErrors => ErrorCount > 0;

    [JsonIgnore]
    public string StatusText => Status switch
    {
        _ when ErrorCount > 0 => "Обнаружены ошибки",
        "Completed" => "Ошибок не обнаружено",
        "Stopped" => "Проверка остановлена",
        "Interrupted" => "Проверка прервана",
        "Failed" => "Проверка не выполнена",
        _ => "Проверка выполняется"
    };

    [JsonIgnore]
    public string Recommendation => Status switch
    {
        _ when ErrorCount > 0 => "Сохраните отчёт. Если включён разгон или XMP, верните штатные настройки BIOS и повторите проверку. Если ошибки остаются, проверяйте планки по одной.",
        "Completed" => "Ошибок не найдено. При редких сбоях запустите полную или циклическую проверку на большем объёме.",
        "Failed" => "Уменьшите объём проверки и повторите запуск.",
        _ => "Проверка не завершена, по ней нельзя судить об исправности памяти."
    };
}
