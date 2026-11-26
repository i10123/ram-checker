using System.Text.Json;
using RAMChecker.Models;

namespace RAMChecker.Services;

public class LocalStorage(string? customDirectory = null)
{
    private const string HistoryFile = "history.json", HistoryBackupFile = "history.bad.json";
    public string DirectoryPath { get; } = customDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MemScope");
    private readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };

    public T? Read<T>(string fileName)
    {
        using var stream = OpenRead(fileName);
        return stream is null ? default : JsonSerializer.Deserialize<T>(stream, jsonOptions);
    }

    public void Write<T>(string fileName, T value) =>
        ReplaceFile(fileName, stream => JsonSerializer.Serialize(stream, value, jsonOptions));

    public void WriteHistory(IReadOnlyCollection<DiagnosticResult> records)
    {
        CheckHistoryCount(records.Count);
        Write(HistoryFile, records);
    }

    private FileStream? OpenRead(string fileName)
    {
        FileStream stream;
        try { stream = File.OpenRead(Path.Combine(DirectoryPath, fileName)); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        try
        {
            CheckSize(fileName, stream);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void CheckSize(string fileName, FileStream stream)
    {
        const int historyLimit = 32 * 1024 * 1024;
        const int settingsLimit = 64 * 1024;
        long limit = fileName == "settings.json" ? settingsLimit : historyLimit;
        if (stream.Length > limit)
            throw new IOException($"Файл {fileName} превышает допустимый размер ({limit:N0} байт).");
    }

    private static void CheckHistoryCount(int count)
    {
        const int maxRecords = 10_000;
        if (count > maxRecords)
            throw new IOException($"В файле {HistoryFile} больше {maxRecords:N0} записей.");
    }

    private void ReplaceFile(string fileName, Action<Stream> write)
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, fileName);
        var temporaryPath = Path.Combine(Path.GetDirectoryName(path)!, Path.GetRandomFileName());
        bool created = false;
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                write(stream);
                CheckSize(fileName, stream);
            }
            File.Move(temporaryPath, path, overwrite: true);
            created = false;
        }
        finally
        {
            if (created)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
    // Правильный JSON может содержать неверные данные (файл правили вручную или он повреждён): такие записи
    // пропускаются, иначе они сломали бы карту и отчёт. Записи разбираются по одной: неизвестный статус или
    // поле не того типа портят только свою запись, а не всю историю. Исходный файл сохраняется рядом,
    // потому что следующее сохранение истории его перезапишет. Если файл не разбирается целиком,
    // история начинается заново, а skipped равен -1.
    public List<DiagnosticResult> ReadHistory(out int skipped)
    {
        using var source = OpenRead(HistoryFile);
        skipped = 0;
        if (source is null)
            return [];

        List<DiagnosticResult> items;
        try { items = ReadHistoryRecords(source, out skipped); }
        catch (JsonException)
        {
            items = [];
            skipped = -1;
        }
        if (skipped != 0)
        {
            source.Position = 0;
            ReplaceFile(HistoryBackupFile, source.CopyTo);
        }
        // Незавершённая проверка сохранена промежуточным автосохранением: счётчик, карта и ошибки на тот момент.
        foreach (var item in items.Where(record => record.Status == DiagnosticStatus.Running))
        {
            item.Status = DiagnosticStatus.Interrupted;
            item.Detail = "Приложение завершилось до получения итогового результата.";
            item.Map.AsSpan().Replace(MemoryDiagnosticService.Waiting, MemoryDiagnosticService.Ok); // Как в итоге проверки.
        }
        return items;
    }

    private List<DiagnosticResult> ReadHistoryRecords(Stream source, out int skipped)
    {
        using var history = JsonDocument.Parse(source);
        skipped = 0;
        var root = history.RootElement;
        if (root.ValueKind == JsonValueKind.Null)
            return [];
        if (root.ValueKind != JsonValueKind.Array)
        {
            skipped = -1;
            return [];
        }

        CheckHistoryCount(root.GetArrayLength());
        var items = new List<DiagnosticResult>();
        foreach (var element in root.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object ||
                element.EnumerateObject().Any(property => property.Value.ValueKind == JsonValueKind.Array &&
                    ((property.NameEquals(nameof(DiagnosticResult.Errors)) && property.Value.GetArrayLength() > 1000) ||
                     (property.NameEquals(nameof(DiagnosticResult.Samples)) && property.Value.GetArrayLength() > 120))))
                continue;

            try
            {
                if (element.Deserialize<DiagnosticResult>(jsonOptions) is { } record && IsValid(record))
                    items.Add(record);
            }
            catch (JsonException) { } // Запись не разбирается — пропускаем её.
        }
        skipped = root.GetArrayLength() - items.Count;
        return items;
    }

    private static bool IsValid(DiagnosticResult record) => record is
        { Mode.Length: <= 256, AlgorithmName.Length: <= 256, Detail.Length: <= 65536, Configuration.Length: <= 65536, Errors.Count: <= 1000,
            Map.Length: MemoryDiagnosticService.MapSize, RequestedBytes: >= 0, ErrorCount: >= 0, Passes: >= 0, ElapsedSeconds: >= 0 and < 1e9 }
        && record.Samples is { Count: <= 120 } && record.Samples.All(s => s is { Seconds: >= 0 and < 1e9, PhysicalPercent: >= 0 and <= 100, CommitPercent: null or (>= 0 and <= 100) })
        && record.Samples.Zip(record.Samples.Skip(1)).All(pair => pair.First.Seconds <= pair.Second.Seconds)
        && Enum.IsDefined(record.Status) && record.Map.All(cellState => cellState <= MemoryDiagnosticService.Waiting)
        && record.Errors.All(error => error is { Stage.Length: <= 256 });
}
