using System.Text.Json.Serialization;

namespace RAMChecker.Models;

public record MemoryError(long Offset, uint Expected, uint Actual, string Stage, int Pass)
{
    public string OffsetText => $"0x{Offset:X12}";
    public string ExpectedText => $"0x{Expected:X8}";
    public string ActualText => $"0x{Actual:X8}";
}

public class DiagnosticResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset Started { get; set; } = DateTimeOffset.Now;
    public string Mode { get; set; } = "Быстрая";
    public string Status { get; set; } = "Running";
    public long RequestedBytes
    {
        get; set;
    }
    public long AllocatedBytes
    {
        get; set;
    }
    public long VerifiedBytes
    {
        get; set;
    }
    public long ErrorCount
    {
        get; set;
    }
    public double ElapsedSeconds
    {
        get; set;
    }
    public int Passes
    {
        get; set;
    }
    public string Detail { get; set; } = "";
    public string Configuration { get; set; } = "";
    public string Algorithms { get; set; } = "";
    public List<MemoryError> Errors { get; set; } = [];
    public List<string> Events { get; set; } = [];
    [JsonIgnore] public string StartedText => Started.ToLocalTime().ToString("dd MMMM yyyy, HH:mm");
    [JsonIgnore] public string DurationText => TimeSpan.FromSeconds(ElapsedSeconds).ToString(@"hh\:mm\:ss");
    [JsonIgnore] public string MemoryText => Services.MemorySnapshot.Format((ulong)AllocatedBytes);
    [JsonIgnore] public bool HasErrors => ErrorCount > 0;
    [JsonIgnore]
    public string StatusText => Status switch
    {
        "Completed" when ErrorCount == 0 => "Ошибок не обнаружено",
        "Completed" or "Errors" => "Обнаружены ошибки",
        "Stopped" => "Проверка остановлена",
        "Interrupted" => "Проверка прервана",
        "Failed" => "Проверка не выполнена",
        _ => "Проверка выполняется"
    };
    [JsonIgnore]
    public string StatusColor => Status switch
    {
        "Completed" when ErrorCount == 0 => "#147D73",
        "Errors" or "Failed" => "#BD3B45",
        "Completed" => "#BD3B45",
        _ => "#9A671D"
    };
    [JsonIgnore] public string Description => $"{Mode} · {DurationText} · {MemoryText}";
    [JsonIgnore]
    public string Recommendation => ErrorCount > 0
        ? "Сохраните отчёт. Если включён разгон/XMP, повторите проверку на штатных настройках BIOS. Уточните причину загрузочным тестом; ошибка не определяет неисправную планку."
        : Status == "Completed" ? "В выполненных тестах ошибок нет. Это не гарантия исправности всей RAM; при сбоях выполните более длительную и загрузочную проверку."
        : Status == "Failed" ? "Проверьте причину сбоя выше. При нехватке памяти уменьшите объём проверки и повторите запуск."
        : "Проверка не завершена. По этому результату нельзя сделать вывод об исправности памяти.";
}

public class AppSettings
{
    public int RefreshSeconds { get; set; } = 1;
    public bool StopOnFirstError
    {
        get; set;
    }
    public bool AutoSave { get; set; } = true;
}
