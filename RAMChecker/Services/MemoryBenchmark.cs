using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace RAMChecker.Services;

public record BenchmarkResult(double ReadMBs, double WriteMBs, double CopyMBs, double LatencyNs);

// Приблизительный однопоточный замер скорости чтения, записи, копирования и задержки памяти.
// Буферы по 256 МБ вдвое больше самого большого кэша L3 настольных процессоров (128 МБ), но у серверных кэш
// бывает больше. Результат зависит от частоты процессора и фоновой нагрузки и не заменяет специальные тесты.
public static class MemoryBenchmark
{
    private const int Words = 64 * 1024 * 1024; // 256 МБ

    public static Task<BenchmarkResult> RunAsync() => Task.Run(() =>
    {
        if (Words * 8L > MemorySnapshot.SafeTestBytes(MemoryMonitor.Read()))
            throw new InvalidOperationException("Для измерения не хватает свободной памяти.");
        var source = new uint[Words];
        var destination = new uint[Words];
        uint sum = 0;

        // Первый запуск — прогрев: Windows выделяет страницы при первом обращении, и это время не должно попасть в замер.
        // Затем медиана пяти замеров в МБ/с.
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
        // Чтение складывает слова векторами (по 4–8 слов за команду): обычный цикл по одному слову упирается в процессор
        // и показывает в несколько раз меньше настоящей скорости памяти. Сумма проверяется после цикла,
        // поэтому компилятор не может выбросить чтение.
        double read = Measure(() =>
        {
            var total = Vector<uint>.Zero;
            foreach (var vector in MemoryMarshal.Cast<uint, Vector<uint>>(source))
                total += vector;
            sum = Vector.Sum(total);
        });
        double copy = Measure(() => Array.Copy(source, destination, Words));
        if (sum != unchecked(0xA5A5A5A5u * Words) || !source.AsSpan().SequenceEqual(destination))
            throw new InvalidOperationException("Данные после замера не совпали.");

        // Задержка: переходы по случайному циклу, где следующий адрес известен только после чтения текущего.
        // Цикл проходит по одному элементу в каждой строке кэша (64 байта = 16 слов), поэтому каждый переход —
        // новая строка. Перемешивание Саттоло даёт один цикл через все строки. Массив копирования уже не нужен
        // и используется повторно. В задержку входит и промах буфера TLB — так же, как у обычных программ.
        var next = MemoryMarshal.Cast<uint, int>(destination.AsSpan());
        const int Line = 16;
        for (int i = 0; i < Words; i += Line)
            next[i] = i;
        for (int i = Words / Line - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i);
            (next[i * Line], next[j * Line]) = (next[j * Line], next[i * Line]);
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
