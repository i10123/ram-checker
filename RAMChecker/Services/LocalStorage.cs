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
    public List<DiagnosticResult> ReadHistory()
    {
        var items = Read<List<DiagnosticResult>>("history.json") ?? [];
        foreach (var item in items.Where(i => i.Status == "Running"))
        {
            item.Status = "Interrupted";
            item.Detail = "Приложение завершилось до получения итогового результата.";
        }
        return items;
    }
}
