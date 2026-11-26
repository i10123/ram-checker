using System.Text.Json;
using RAMChecker.Models;

namespace RAMChecker.Services;

public class LocalStorage(string? customDirectory = null)
{
    public string DirectoryPath { get; } = customDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MemScope");
    private readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };

    public T? Read<T>(string fileName)
    {
        var path = Path.Combine(DirectoryPath, fileName);
        return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), jsonOptions) : default;
    }
    public void Write<T>(string fileName, T value)
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, fileName);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, jsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }
    // Правильный JSON может содержать неверные данные (файл правили вручную или он повреждён): такие записи
    // пропускаются, иначе они сломали бы карту и отчёт. Записи разбираются по одной: неизвестный статус или
    // поле не того типа портят только свою запись, а не всю историю. Исходный файл сохраняется рядом,
    // потому что следующее сохранение истории его перезапишет. Если файл не разбирается целиком,
    // история начинается заново, а skipped равен -1.
    public List<DiagnosticResult> ReadHistory(out int skipped)
    {
        List<JsonElement>? allElements = null;
        try
        {
            allElements = Read<List<JsonElement>>("history.json") ?? [];
        }
        catch (JsonException) { } // Файл не является списком записей.
        var items = new List<DiagnosticResult>();
        foreach (var element in allElements ?? [])
        {
            try
            {
                if (element.Deserialize<DiagnosticResult>(jsonOptions) is { } record && IsValid(record))
                    items.Add(record);
            }
            catch (JsonException) { } // Запись не разбирается — пропускаем её.
        }
        skipped = allElements is null ? -1 : allElements.Count - items.Count;
        if (skipped != 0)
            File.Copy(Path.Combine(DirectoryPath, "history.json"), Path.Combine(DirectoryPath, "history.bad.json"), overwrite: true);
        // Незавершённая проверка сохранена промежуточным автосохранением: счётчик, карта и ошибки на тот момент.
        foreach (var item in items.Where(record => record.Status == DiagnosticStatus.Running))
        {
            item.Status = DiagnosticStatus.Interrupted;
            item.Detail = "Приложение завершилось до получения итогового результата.";
            item.Map.AsSpan().Replace(MemoryDiagnosticService.Waiting, MemoryDiagnosticService.Ok); // Как в итоге проверки.
        }
        return items;
    }

    private static bool IsValid(DiagnosticResult record) => record is { Mode: not null, Detail: not null, Configuration: not null, Errors: not null,
            Map.Length: MemoryDiagnosticService.MapSize, RequestedBytes: >= 0, ErrorCount: >= 0, Passes: >= 0, ElapsedSeconds: >= 0 and < 1e9 }
        && record.Samples is { Count: <= 120 } && record.Samples.All(s => s is { Seconds: >= 0 and < 1e9, PhysicalPercent: >= 0 and <= 100, CommitPercent: null or (>= 0 and <= 100) })
        && Enum.IsDefined(record.Status) && record.Map.All(cellState => cellState <= MemoryDiagnosticService.Waiting) && !record.Errors.Contains(null!);
}
