using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace RAMChecker.Services;

public record BenchmarkResult(double ReadMegabytesPerSecond, double WriteMegabytesPerSecond, double CopyMegabytesPerSecond, double LatencyNanoseconds);

// Замер скорости чтения, записи, копирования и задержки памяти. Чтение, запись и копирование идут на всех ядрах:
// один поток не загружает контроллер памяти полностью. Буферы вчетверо больше кэша L3 этого процессора
// (от 256 МБ до 1 ГБ), поэтому данные не помещаются в кэш. Результат зависит от фоновой нагрузки.
public static partial class MemoryBenchmark
{
    public static Task<BenchmarkResult> RunAsync() => Task.Run(() =>
    {
        // Каждый поток работает со своей частью буфера; часть кратна строке кэша (16 слов).
        int threadCount = Environment.ProcessorCount;
        int wordsPerThread = (int)(Math.Clamp(4 * GetL3CacheBytes(), 256L << 20, 1L << 30) / 4 / threadCount) & ~15;
        int totalWords = wordsPerThread * threadCount;
        if (totalWords * 8L > MemorySnapshot.SafeTestBytes(MemoryMonitor.Read()))
            throw new InvalidOperationException("Для измерения не хватает свободной памяти.");
        var source = new uint[totalWords];
        var destination = new uint[totalWords];
        var threadSums = new uint[threadCount];
        void RunOnAllCores(Action<int, int> threadWork) => Parallel.For(0, threadCount, threadIndex => threadWork(threadIndex, threadIndex * wordsPerThread));

        // Первый запуск — прогрев: Windows выделяет страницы при первом обращении, и это время не должно попасть в замер.
        // Затем медиана пяти замеров в МБ/с.
        double Measure(Action measuredOperation)
        {
            measuredOperation();
            var measuredSeconds = new double[5];
            for (int attempt = 0; attempt < measuredSeconds.Length; attempt++)
            {
                long startTimestamp = Stopwatch.GetTimestamp();
                measuredOperation();
                measuredSeconds[attempt] = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;
            }
            Array.Sort(measuredSeconds);
            return totalWords * 4d / 1048576 / measuredSeconds[2];
        }

        double writeSpeed = Measure(() => RunOnAllCores((_, firstWord) => source.AsSpan(firstWord, wordsPerThread).Fill(0xA5A5A5A5u)));
        // Чтение складывает слова векторами (по 4–8 слов за команду): обычный цикл по одному слову упирается в процессор
        // и показывает в несколько раз меньше настоящей скорости памяти. Сумма проверяется после цикла,
        // поэтому компилятор не может выбросить чтение.
        double readSpeed = Measure(() => RunOnAllCores((threadIndex, firstWord) =>
        {
            var vectorSum = Vector<uint>.Zero;
            foreach (var vector in MemoryMarshal.Cast<uint, Vector<uint>>(source.AsSpan(firstWord, wordsPerThread)))
                vectorSum += vector;
            threadSums[threadIndex] = Vector.Sum(vectorSum);
        }));
        double copySpeed = Measure(() => RunOnAllCores((_, firstWord) => source.AsSpan(firstWord, wordsPerThread).CopyTo(destination.AsSpan(firstWord, wordsPerThread))));
        // Сумма каждой части считается по модулю 2^32, поэтому и ожидаемое значение умножается в uint.
        if (threadSums.Any(threadSum => threadSum != unchecked(0xA5A5A5A5u * (uint)wordsPerThread)) || !source.AsSpan().SequenceEqual(destination))
            throw new InvalidOperationException("Данные после замера не совпали.");

        // Задержка: переходы по случайному циклу, где следующий адрес известен только после чтения текущего.
        // Цикл проходит по одному элементу в каждой строке кэша (64 байта = 16 слов), поэтому каждый переход —
        // новая строка. Перемешивание Саттоло даёт один цикл через все строки. Массив копирования уже не нужен
        // и используется повторно. В задержку входит и промах буфера TLB — так же, как у обычных программ.
        var nextIndex = MemoryMarshal.Cast<uint, int>(destination.AsSpan());
        const int WordsPerCacheLine = 16;
        for (int index = 0; index < totalWords; index += WordsPerCacheLine)
            nextIndex[index] = index;
        for (int lineIndex = totalWords / WordsPerCacheLine - 1; lineIndex > 0; lineIndex--)
        {
            int otherLineIndex = Random.Shared.Next(lineIndex);
            (nextIndex[lineIndex * WordsPerCacheLine], nextIndex[otherLineIndex * WordsPerCacheLine]) = (nextIndex[otherLineIndex * WordsPerCacheLine], nextIndex[lineIndex * WordsPerCacheLine]);
        }
        const int JumpCount = 10_000_000;
        int currentIndex = 0;
        long latencyStart = Stopwatch.GetTimestamp();
        for (int jump = 0; jump < JumpCount; jump++)
            currentIndex = nextIndex[currentIndex];
        double latencyNanoseconds = Stopwatch.GetElapsedTime(latencyStart).TotalNanoseconds / JumpCount;
        GC.KeepAlive(currentIndex);
        return new BenchmarkResult(readSpeed, writeSpeed, copySpeed, latencyNanoseconds);
    });

    // Суммарный объём кэшей L3 по сведениям Windows (у процессоров из нескольких кристаллов их несколько).
    // В 64-битном процессе записи по 32 байта: тип записи в байтах 8–11 (2 — кэш), уровень кэша в байте 16, размер в байтах 20–23.
    private static long GetL3CacheBytes()
    {
        uint bufferLength = 0;
        GetLogicalProcessorInformation(null, ref bufferLength);
        var buffer = new byte[bufferLength];
        long totalCacheBytes = 0;
        if (GetLogicalProcessorInformation(buffer, ref bufferLength))
            for (int recordOffset = 0; recordOffset + 32 <= bufferLength; recordOffset += 32)
                if (BitConverter.ToInt32(buffer, recordOffset + 8) == 2 && buffer[recordOffset + 16] == 3)
                    totalCacheBytes += BitConverter.ToUInt32(buffer, recordOffset + 20);
        return totalCacheBytes;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLogicalProcessorInformation([Out] byte[]? buffer, ref uint bufferLength);
}
