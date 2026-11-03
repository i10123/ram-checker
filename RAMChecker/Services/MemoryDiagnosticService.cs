using System.Diagnostics;
using System.Runtime.InteropServices;
using RAMChecker.Models;

namespace RAMChecker.Services;

public record DiagnosticProgress(string Stage, int Pass, int Passes, double Percent, TimeSpan Elapsed, long ErrorCount, long ProcessedBytes, byte[]? Map);

// Собственный алгоритм «Свой адрес с инверсией». Один проход состоит из трёх шагов:
// 1) в каждую ячейку записывается значение шаблона: в нечётных проходах — вычисленное из её адреса,
//    в чётных — шахматный шаблон, где у соседних ячеек противоположные биты;
// 2) прямой обход: ячейка сверяется со своим значением, затем в неё пишется инверсия;
// 3) обратный обход: сверяется инверсия, затем возвращается исходное значение.
// Шаблон адреса находит ошибки адресации (две ячейки отвечают по одному адресу),
// шахматный — влияние соседних ячеек друг на друга; шаги 2–3 — «залипшие» биты.
public static class MemoryDiagnosticService
{
    public const string Algorithm = "Свой адрес с инверсией";
    // Буфер делится на столько же блоков, сколько клеток на карте памяти.
    public const int MapSize = 256;
    // Waiting — блок уже проверялся без ошибок, но текущий проход до него ещё не дошёл.
    public const byte NotChecked = 0, Ok = 1, Error = 2, Waiting = 3;
    private static readonly string[] Steps = ["Заполнение", "Проверка вперёд", "Проверка назад"];

    // Шаблон адреса: умножение на нечётное число взаимно однозначно, поэтому соседние ячейки хранят разные значения.
    // Шахматный шаблон: 1010… и 0101… чередуются через ячейку и меняются местами с каждым чётным проходом.
    public static uint Pattern(long address, int pass) => pass % 2 == 1
        ? unchecked((uint)address * 0x9E3779B1u + (uint)pass * 0x7F4A7C15u)
        : ((address + pass / 2) & 1) == 0 ? 0xAAAAAAAAu : 0x55555555u;

    public static string PatternName(int pass) => pass % 2 == 1 ? "Свой адрес" : "Шахматный шаблон";

    public static void FillBlock(Span<uint> block, long start, int pass)
    {
        for (int i = 0; i < block.Length; i++)
            block[i] = Pattern(start + i, pass);
    }

    // Сверяет блок с ожидаемыми значениями и записывает инверсию. Возвращает число ошибок.
    public static int CheckBlock(Span<uint> block, long start, int pass, bool backward, Action<long, uint, uint> onError)
    {
        int errors = 0;
        for (int k = 0; k < block.Length; k++)
        {
            int i = backward ? block.Length - 1 - k : k;
            uint expected = backward ? ~Pattern(start + i, pass) : Pattern(start + i, pass);
            if (block[i] != expected)
            {
                errors++;
                onError(start + i, expected, block[i]);
            }
            block[i] = ~expected;
        }
        return errors;
    }

    // Буфер выделяется вне сборщика мусора: после проверки память сразу возвращается Windows.
    public static unsafe Task<DiagnosticResult> RunAsync(long bytes, TimeSpan? duration, bool stopOnFirstError,
        IProgress<DiagnosticProgress> progress, CancellationToken token) => Task.Run(() =>
    {
        var result = new DiagnosticResult { RequestedBytes = bytes };
        var clock = Stopwatch.StartNew();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var blocks = new nint[MapSize];
        int blockWords = (int)(bytes / 4 / MapSize);
        int pass = 0, step = 0, done = 0;
        long blockSteps = 0; // сколько раз блоки уже записаны или проверены — для скорости проверки
        long lastReport = -1000;
        Span<uint> Block(int b) => new((void*)blocks[b], blockWords);

        // Не чаще пяти раз в секунду из любого потока, иначе интерфейс не успевает перерисовываться.
        // В конце прохода отчёт отправляется всегда, чтобы журнал не пропускал проходы; карту он не несёт,
        // иначе быстрые проходы всегда показывали бы полностью проверенную карту.
        void Report(bool force = false)
        {
            long now = clock.ElapsedMilliseconds, last = Interlocked.Read(ref lastReport);
            if (!force && (now - last < 200 || Interlocked.CompareExchange(ref lastReport, now, last) != last))
                return;
            progress.Report(new($"{PatternName(pass)} · {Steps[step % 3]}", pass, result.Passes, duration is { } d
                ? Math.Min(100, clock.Elapsed / d * 100)
                : (step * MapSize + done) * 100.0 / (3 * MapSize), clock.Elapsed, result.ErrorCount,
                Interlocked.Read(ref blockSteps) * blockWords * 4, force ? null : (byte[])result.Map.Clone()));
        }

        void OnError(int block, long address, uint expected, uint actual)
        {
            lock (result)
            {
                result.ErrorCount++;
                result.Map[block] = Error;
                if (result.Errors.Count < 1000)
                    result.Errors.Add(new(address * 4, expected, actual, $"{PatternName(pass)} · {Steps[step]}", pass));
            }
            if (stopOnFirstError)
                stop.Cancel();
        }

        try
        {
            for (int b = 0; b < MapSize; b++)
            {
                stop.Token.ThrowIfCancellationRequested();
                blocks[b] = (nint)NativeMemory.Alloc((nuint)blockWords * 4);
                result.AllocatedBytes += blockWords * 4L;
            }
            // Блоки обрабатываются параллельно на всех ядрах; шаги идут по всему буферу,
            // поэтому данные не успевают остаться в кэше процессора между записью и проверкой.
            var options = new ParallelOptions { CancellationToken = stop.Token };
            do
            {
                pass++;
                // В начале прохода проверенные блоки снова ждут своей очереди: по карте видно, как идёт проход.
                result.Map.AsSpan().Replace(Ok, Waiting);
                for (step = 0; step < Steps.Length; step++)
                {
                    done = 0;
                    Parallel.For(0, MapSize, options, k =>
                    {
                        int b = step == 2 ? MapSize - 1 - k : k;
                        long start = (long)b * blockWords;
                        if (step == 0)
                            FillBlock(Block(b), start, pass);
                        // Клетка карты зеленеет, когда её блок прошёл текущий проход без ошибок.
                        else if (CheckBlock(Block(b), start, pass, step == 2, (a, e, r) => OnError(b, a, e, r)) == 0 && step == 2 && result.Map[b] != Error)
                            result.Map[b] = Ok;
                        Interlocked.Increment(ref done);
                        Interlocked.Increment(ref blockSteps);
                        Report();
                    });
                }
                result.Passes = pass;
                Report(force: true);
            } while (duration is not { } limit || clock.Elapsed < limit);
            result.Status = "Completed";
        }
        catch (OperationCanceledException)
        {
            result.Status = token.IsCancellationRequested ? "Stopped" : "Errors";
        }
        catch (Exception ex)
        {
            result.Status = "Failed";
            result.Detail = ex is OutOfMemoryException ? "Не удалось выделить память. Уменьшите объём и повторите проверку." : ex.Message;
        }
        finally
        {
            result.ElapsedSeconds = clock.Elapsed.TotalSeconds;
            result.Map.AsSpan().Replace(Waiting, Ok); // В итоге важно лишь, проверялся ли блок.
            foreach (nint block in blocks)
                NativeMemory.Free((void*)block);
        }
        return result;
    });
}
