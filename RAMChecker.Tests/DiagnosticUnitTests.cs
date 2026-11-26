using RAMChecker.Models;
using RAMChecker.Services;
using static RAMChecker.Services.MemoryDiagnosticService;

internal static class DiagnosticUnitTests
{
    public static void Run(Action<bool, string> check)
    {
        const long first = (1L << 34) + 19;
        uint[] storage = [123, 0, 0, 0, 456];
        var words = storage.AsSpan(1, 3);
        for (int pattern = 1; pattern <= 4; pattern++)
        {
            FillBlock(words, first, pattern);
            uint[] original = words.ToArray();
            var mismatches = new List<(long Address, uint Expected, uint Actual)>();
            void Record(long address, uint expected, uint actual) => mismatches.Add((address, expected, actual));

            words[0] ^= 3;
            words[^1] ^= 4;
            int count = CheckBlock(words, first, pattern, false, Record);
            check(count == 2 && mismatches.SequenceEqual(new[] {
                (first, original[0], original[0] ^ 3u), (first + 2, original[2], original[2] ^ 4u)
            }), $"Шаблон {pattern}: точные адреса и оба значения на границах среза");

            words[0] ^= 1;
            words[^1] ^= 1;
            mismatches.Clear();
            CheckBlock(words, first, pattern, true, Record);
            check(mismatches.Select(e => e.Address).SequenceEqual(new[] { first + 2, first })
                && words.SequenceEqual(original) && storage[0] == 123 && storage[^1] == 456,
                $"Шаблон {pattern}: обратный порядок ошибок, восстановление и сохранность соседей");
        }

        var result = new DiagnosticResult { RequestedBytes = MapSize * 4096L };
        result.Map[0] = Ok;
        result.Map[^1] = Error;
        check(result.CheckedBytes == 4096 && result.CheckedText.StartsWith("не менее "),
            "Незавершённый проход учитывает только полностью проверенный блок");
        var oldMap = result.Map;
        var oldError = new MemoryError(8, 3, 1, "Проверка", 1);
        result.Errors.Add(oldError);
        result.ApplyProgress(new("Проверка", 2, 1, 10, TimeSpan.FromSeconds(3), 1, 8192, null, null));
        check(result.Map == oldMap && result.Errors.Single() == oldError && result.ProcessedBytes == 8192,
            "Промежуточный отчёт без снимка сохраняет карту и журнал ошибок");
    }
}
