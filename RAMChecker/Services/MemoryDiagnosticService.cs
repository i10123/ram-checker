using System.Diagnostics;
using System.Threading;
using RAMChecker.Models;

namespace RAMChecker.Services;

public record DiagnosticProgress(string Stage, int Pass, double Percent, double ElapsedSeconds,
    long AllocatedBytes, long VerifiedBytes, long ErrorCount);

public static class MemoryDiagnosticService
{
    private static uint Expected(long word, int stage, int pass)
    {
        if (stage == 0)
            return 0;
        if (stage == 1)
            return uint.MaxValue;
        if (stage == 2)
            return ((word + pass) & 1) == 0 ? 0xAAAAAAAAu : 0x55555555u;
        if (stage == 4)
            return 1u << (int)((word + pass) & 31);
        uint x = unchecked((uint)word + (uint)pass * 0x9E3779B9u);
        x ^= x >> 16;
        x = unchecked(x * 0x7FEB352Du);
        x ^= x >> 15;
        x = unchecked(x * 0x846CA68Bu);
        return x ^ (x >> 16);
    }

    private static readonly string[] Stages = ["Нули", "Единицы", "Шахматный шаблон", "Псевдослучайные данные", "Бегущая единица"];
    public static string Profile(bool extended) => "Moving Inversions: " + string.Join(", ", Stages.Take(extended ? 5 : 4));
    private static readonly string[] Sweeps = [" · заполнение", " · проверка →", " · проверка ←"];
    private const int BlockWords = 4 * 1024 * 1024;

    // Методика Memtest86+: заполнение, проверка/инверсия вперёд, проверка/инверсия назад.
    private static bool MovingInversions(List<uint[]> buffers, int stage, int pass, DiagnosticResult result,
        bool stopOnFirstError, CancellationToken token, Action<int> checkpoint)
    {
        for (int sweep = 0; sweep < 3; sweep++)
        {
            bool reverse = sweep == 2;
            long baseWord = reverse ? buffers.Sum(b => (long)b.Length) : 0;
            for (int b = reverse ? buffers.Count - 1 : 0; b >= 0 && b < buffers.Count; b += reverse ? -1 : 1)
            {
                var buffer = buffers[b];
                if (reverse)
                    baseWord -= buffer.Length;
                checkpoint(sweep);
                for (int i = reverse ? buffer.Length - 1 : 0; i >= 0 && i < buffer.Length; i += reverse ? -1 : 1)
                {
                    if ((i & 16383) == 0)
                    {
                        token.ThrowIfCancellationRequested();
                        checkpoint(sweep);
                    }
                    uint expected = Expected(baseWord + i, stage, pass);
                    if (reverse)
                        expected = ~expected;
                    if (sweep > 0)
                    {
                        uint actual = Volatile.Read(ref buffer[i]);
                        result.VerifiedBytes += 4;
                        if (actual != expected)
                        {
                            result.ErrorCount++;
                            if (result.Errors.Count < 1000)
                                result.Errors.Add(new((baseWord + i) * 4, expected, actual, Stages[stage], pass));
                            if (stopOnFirstError)
                                return false;
                        }
                    }
                    Volatile.Write(ref buffer[i], sweep == 0 ? expected : ~expected);
                }
                if (!reverse)
                    baseWord += buffer.Length;
            }
        }
        return true;
    }

    public static Task<DiagnosticResult> RunAsync(long bytes, TimeSpan? duration, bool stopOnFirstError,
        IProgress<DiagnosticProgress>? progress, CancellationToken token, bool extended = false) => Task.Run(() =>
    {
        if (bytes < 4 || bytes % 4 != 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        if (duration.HasValue && duration.Value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));
        if (bytes > MemorySnapshot.SafeTestBytes(MemoryMonitor.Read()))
            throw new InvalidOperationException("Свободной памяти стало меньше. Уменьшите объём проверки.");
        var result = new DiagnosticResult { RequestedBytes = bytes, Algorithms = Profile(extended) };
        var buffers = new List<uint[]>();
        var clock = Stopwatch.StartNew();
        long lastReport = -1000;
        string stageName = "Выделение памяти";
        int pass = 0;
        void Report(bool force = false)
        {
            if (!force && clock.ElapsedMilliseconds - lastReport < 200)
                return;
            lastReport = clock.ElapsedMilliseconds;
            progress?.Report(new(stageName, pass,
                duration.HasValue ? Math.Min(99, clock.Elapsed.TotalSeconds / duration.Value.TotalSeconds * 100) : 0,
                clock.Elapsed.TotalSeconds, result.AllocatedBytes, result.VerifiedBytes, result.ErrorCount));
        }
        try
        {
            for (long remaining = bytes / 4; remaining > 0;)
            {
                token.ThrowIfCancellationRequested();
                int words = (int)Math.Min(remaining, BlockWords);
                buffers.Add(new uint[words]);
                remaining -= words;
                result.AllocatedBytes += words * 4L;
                Report();
            }
            do
            {
                pass++;
                for (int stage = 0; stage < (extended ? 5 : 4); stage++)
                {
                    stageName = Stages[stage];
                    result.Events.Add($"{DateTimeOffset.Now:HH:mm:ss} · Проход {pass}: {stageName}");
                    // Ограничиваем журнал при длительной проверке; сами ошибки хранятся отдельно.
                    if (result.Events.Count > 300)
                        result.Events.RemoveAt(0);
                    Report(true);
                    var sweepNames = Sweeps.Select(suffix => Stages[stage] + suffix).ToArray();
                    if (!MovingInversions(buffers, stage, pass, result, stopOnFirstError, token, sweep =>
                    {
                        stageName = sweepNames[sweep];
                        Report();
                    }))
                    {
                        result.Status = "Errors";
                        return result;
                    }
                }
                result.Passes = pass;
            } while (!duration.HasValue || clock.Elapsed < duration.Value);
            result.Status = "Completed";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { result.Status = "Stopped"; }
        catch (Exception ex)
        {
            result.Status = "Failed";
            result.Detail = ex is OutOfMemoryException ? "Не удалось выделить память. Уменьшите объём и повторите проверку." : ex.Message;
        }
        finally
        {
            result.ElapsedSeconds = clock.Elapsed.TotalSeconds;
            result.Events.Add($"{DateTimeOffset.Now:HH:mm:ss} · {result.StatusText}");
            buffers.Clear();
            // Освобождаем большие буферы перед возвращением к мониторингу.
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
        }
        return result;
    });
}
