using RAMChecker.Models;
using RAMChecker.Services;
using static RAMChecker.Services.MemoryDiagnosticService;

internal static class DiagnosticFeatureTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        // 11 слов в блоке: группы Modulo-20 пересекают границы блоков.
        const long bytes = MapSize * 11 * sizeof(uint);
        var progress = new SyncProgress<DiagnosticProgress>(_ => { });
        foreach (var (mode, traffic) in new[] { (DiagnosticMode.Quick, 10), (DiagnosticMode.Full, 217) })
        {
            var clean = await MemoryDiagnosticService.RunAsync(bytes, mode, false, progress, CancellationToken.None);
            check(clean.Status == DiagnosticStatus.Completed && clean.ErrorCount == 0 && clean.Map.All(cell => cell == Ok)
                && clean.CheckedBytes == bytes && clean.ProcessedBytes == bytes * traffic,
                $"{mode}: чистый проход с неполными группами Modulo-20 и точный учёт трафика");
        }

        foreach (var mode in Enum.GetValues<DiagnosticMode>())
        {
            var cancelled = await MemoryDiagnosticService.RunAsync(bytes, mode, false, progress, new CancellationToken(true));
            check(cancelled.Status == DiagnosticStatus.Stopped && cancelled.Passes == 0 && cancelled.CheckedBytes == 0
                && cancelled.ProcessedBytes == 0 && cancelled.Map.All(cell => cell == NotChecked),
                $"{mode}: отменённый до старта сеанс сохраняет пустую карту и нулевые счётчики");
        }

        var directory = Directory.CreateTempSubdirectory("MemScopeWorkflow");
        try
        {
            var run = await MemoryDiagnosticService.RunAsync(bytes, DiagnosticMode.Stress, true, progress,
                CancellationToken.None, imitation: true);
            var history = new LocalStorage(directory.FullName);
            history.WriteHistory([run]);
            var loaded = history.ReadHistory(out int skipped).Single();
            long expectedOffset = ((MapSize / 3) * 11 + 7) * sizeof(uint);
            check(skipped == 0 && loaded.Mode == "Стресс" && loaded.Status == DiagnosticStatus.Errors
                && loaded.Errors.Count == 1 && loaded.Errors[0].Offset == expectedOffset
                && loaded.Map[expectedOffset / (bytes / MapSize)] == Error && loaded.ProcessedBytes == run.ProcessedBytes,
                "Стресс: остановка на ошибке, сохранение сеанса и восстановление смещения на карте");

            var path = Path.Combine(directory.FullName, "result.html");
            ReportExporter.Save(loaded, path);
            string html = File.ReadAllText(path);
            check(html.Contains(loaded.Errors.Single().OffsetText) && html.Contains(loaded.Errors.Single().ActualText)
                && html.Contains(loaded.Mode) && html.Contains(loaded.AlgorithmName),
                "HTML восстановленного сеанса содержит режим, алгоритм и данные сбойного слова");

            string unsupported = Path.Combine(directory.FullName, "result.txt");
            File.WriteAllText(unsupported, "Исходный текст");
            bool rejected = false;
            try { ReportExporter.Save(loaded, unsupported); }
            catch (NotSupportedException) { rejected = true; }
            check(rejected && File.ReadAllText(unsupported) == "Исходный текст",
                "Экспорт с неподдерживаемым расширением сохраняет содержимое существующего файла");
        }
        finally { directory.Delete(true); }
    }
}
