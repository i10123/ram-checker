using System.Diagnostics;

namespace RAMChecker.Services;

public record BenchmarkResult(double ReadMBs, double WriteMBs, double CopyMBs, double LatencyNs);

// Замер скорости чтения, записи, копирования и задержки памяти на буферах больше кэша процессора.
public static class MemoryBenchmark
{
    private const int Words = 16 * 1024 * 1024; // 64 МБ

    public static Task<BenchmarkResult> RunAsync() => Task.Run(() =>
    {
        if (Words * 12L > MemorySnapshot.SafeTestBytes(MemoryMonitor.Read()))
            throw new InvalidOperationException("Для измерения не хватает свободной памяти.");
        var source = new uint[Words];
        var destination = new uint[Words];
        ulong sum = 0;

        // Прогрев, затем медиана пяти замеров в МБ/с.
        static double Measure(Action operation)
        {
            operation();
            var seconds = new double[5];
            for (int i = 0; i < seconds.Length; i++)
            {
                long start = Stopwatch.GetTimestamp();
                operation();
                seconds[i] = Stopwatch.GetElapsedTime(start).TotalSeconds;
            }
            Array.Sort(seconds);
            return Words * 4 / 1048576d / seconds[2];
        }

        double write = Measure(() => Array.Fill(source, 0xA5A5A5A5u));
        // Сумма используется после цикла, поэтому компилятор не может выбросить чтение.
        double read = Measure(() =>
        {
            ulong s = 0;
            foreach (uint word in source)
                s += word;
            sum = s;
        });
        double copy = Measure(() => Array.Copy(source, destination, Words));
        if (sum != 0xA5A5A5A5UL * Words || !source.AsSpan().SequenceEqual(destination))
            throw new InvalidOperationException("Данные после замера не совпали.");

        // Задержка: переходы по случайному циклу, где следующий адрес известен только после чтения текущего.
        // Перемешивание Саттоло даёт один цикл через все элементы.
        var next = new int[Words];
        for (int i = 0; i < Words; i++)
            next[i] = i;
        for (int i = Words - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i);
            (next[i], next[j]) = (next[j], next[i]);
        }
        const int Jumps = 10_000_000;
        int p = 0;
        long started = Stopwatch.GetTimestamp();
        for (int i = 0; i < Jumps; i++)
            p = next[p];
        double latency = Stopwatch.GetElapsedTime(started).TotalNanoseconds / Jumps;
        GC.KeepAlive(p);
        return new BenchmarkResult(read, write, copy, latency);
    });
}
