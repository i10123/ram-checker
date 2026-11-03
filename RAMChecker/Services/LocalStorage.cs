using System.IO;
using System.Text.Json;
using RAMChecker.Models;

namespace RAMChecker.Services;

public class LocalStorage
{
    public string DirectoryPath { get; }
    private readonly JsonSerializerOptions options = new() { WriteIndented = true };
    public LocalStorage(string? directory = null) => DirectoryPath = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MemScope");

    public T? Read<T>(string name)
    {
        var path = Path.Combine(DirectoryPath, name);
        return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), options) : default;
    }
    public void Write<T>(string name, T value)
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, name);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, options));
        File.Move(temp, path, overwrite: true);
    }
    // Правильный JSON может содержать неверные данные (файл правили вручную или он повреждён): такие записи
    // пропускаются, иначе они сломали бы карту и отчёт. Записи разбираются по одной: неизвестный статус или
    // поле не того типа портят только свою запись, а не всю историю. Исходный файл сохраняется рядом,
    // потому что следующее сохранение истории его перезапишет.
    public List<DiagnosticResult> ReadHistory(out int skipped)
    {
        var all = Read<List<JsonElement>>("history.json") ?? [];
        var items = new List<DiagnosticResult>();
        foreach (var element in all)
        {
            try
            {
                if (element.Deserialize<DiagnosticResult>(options) is { } i && IsValid(i))
                    items.Add(i);
            }
            catch (JsonException) { } // Запись не разбирается — пропускаем её.
        }
        skipped = all.Count - items.Count;
        if (skipped > 0)
            File.Copy(Path.Combine(DirectoryPath, "history.json"), Path.Combine(DirectoryPath, "history.bad.json"), overwrite: true);
        // Незавершённая проверка сохранена промежуточным автосохранением: счётчик, карта и ошибки на тот момент.
        foreach (var item in items.Where(i => i.Status == DiagnosticStatus.Running))
        {
            item.Status = DiagnosticStatus.Interrupted;
            item.Detail = "Приложение завершилось до получения итогового результата.";
            item.Map.AsSpan().Replace(MemoryDiagnosticService.Waiting, MemoryDiagnosticService.Ok); // Как в итоге проверки.
        }
        return items;
    }

    private static bool IsValid(DiagnosticResult i) => i is { Mode: not null, Detail: not null, Configuration: not null, Errors: not null,
            Map.Length: MemoryDiagnosticService.MapSize, RequestedBytes: >= 0, ErrorCount: >= 0, Passes: >= 0, ElapsedSeconds: >= 0 and < 1e9 }
        && Enum.IsDefined(i.Status) && i.Map.All(s => s <= MemoryDiagnosticService.Waiting) && !i.Errors.Contains(null!);
}
