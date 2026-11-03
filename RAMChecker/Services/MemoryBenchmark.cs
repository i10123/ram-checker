using System.Diagnostics;

namespace RAMChecker.Services;

public record BenchmarkResult(double ReadMiBs, double WriteMiBs, double CopyMiBs, ulong Checksum)
{
    public string Summary => $"Чтение: {ReadMiBs:N0} МиБ/с · Запись: {WriteMiBs:N0} МиБ/с · Копирование: {CopyMiBs:N0} МиБ/с";
}

public static class MemoryBenchmark
{
    public static Task<BenchmarkResult> RunAsync(int bytes = 64 * 1024 * 1024) => Task.Run(() =>
    {
        if (bytes < 4096 || bytes > 64 * 1024 * 1024 || bytes % 4 != 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        if (bytes * 2L > MemorySnapshot.SafeTestBytes(MemoryMonitor.Read()))
            throw new InvalidOperationException("Для измерения не хватает свободной памяти с резервом для Windows.");
        // Выделение двух буферов не входит в измеряемое время.
        var source = new uint[bytes / 4];
        var destination = new uint[source.Length];
        ulong checksum = 0;
        double Measure(Action operation)
        {
            operation(); // Прогрев JIT и страниц памяти.
            var seconds = new double[5];
            for (int i = 0; i < seconds.Length; i++)
            {
                long start = Stopwatch.GetTimestamp();
                operation();
                seconds[i] = Stopwatch.GetElapsedTime(start).TotalSeconds;
            }
            Array.Sort(seconds);
            return bytes / 1048576d / Math.Max(seconds[2], 1d / Stopwatch.Frequency);
        }
        double write = Measure(() => Array.Fill(source, 0xA5A5A5A5u));
        double read = Measure(() =>
        {
            ulong sum = 0;
            foreach (uint word in source)
                sum += word;
            checksum = sum; // Наблюдаемый результат чтения препятствует удалению цикла JIT.
        });
        double copy = Measure(() => Array.Copy(source, destination, source.Length));
        if (!source.AsSpan().SequenceEqual(destination))
            throw new InvalidOperationException("Данные после копирования не совпали.");
        return new BenchmarkResult(read, write, copy, checksum);
    });
}
