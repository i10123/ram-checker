using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RAMChecker.Models;

namespace RAMChecker.Services;

// Map и Errors — копии, которые интерфейс может оставить у себя; null — без изменений с прошлого отчёта.
public record DiagnosticProgress(string Stage, int Pass, int Passes, double Percent, TimeSpan Elapsed,
    long ErrorCount, long ProcessedBytes, byte[]? Map, MemoryError[]? Errors, byte[]? LiveMap = null);

public enum DiagnosticMode { Quick, Full, Continuous, Stress }

public static partial class MemoryDiagnosticService
{
    public const string Algorithm = "Свой адрес с инверсией";
    // Буфер делится на столько же блоков, сколько клеток на карте памяти.
    public const int MapSize = 256;
    // Waiting — блок уже проверялся без ошибок, но текущий проход до него ещё не дошёл.
    public const byte NotChecked = 0, Ok = 1, Error = 2, Waiting = 3, Checking = 4, StepDone = 5;
    private static readonly string[] Steps = ["Заполнение", "Проверка вперёд", "Проверка назад"];
    private static readonly string[] Patterns = ["Свой адрес", "Шахматный шаблон", "Бегущая единица", "Случайный шаблон"];
    private static readonly SemaphoreSlim DiagnosticGate = new(1, 1); // Рабочий набор общий для всего процесса.
    // Отмена проверяется раз в 65 536 ячеек (256 КБ): «Остановить» срабатывает почти сразу,
    // а одна лишняя проверка условия на такой участок не замедляет цикл.
    private const int CancellationCheckMask = 0xFFFF;

    // Шаблон адреса: умножение на нечётное число взаимно однозначно, поэтому соседние ячейки хранят разные значения.
    // Шахматный шаблон: 1010… и 0101… чередуются через ячейку и меняются местами каждые четыре прохода.
    // Бегущая единица: в слове один единичный бит, у соседней ячейки он сдвинут на разряд; инверсия даёт бегущий ноль.
    // Случайный шаблон: адрес перемешивается сдвигами xorshift, поэтому значение можно заново вычислить при проверке.
    // Старшие биты адреса подмешиваются отдельным умножением: у ячеек на расстоянии 16 ГБ и больше младшая
    // половина адреса совпадает, а старшая — нет, поэтому и такие ячейки получают разные значения.
    // Функция вызывается для каждой ячейки, поэтому встраивается прямо в циклы записи и проверки:
    // без вызова и с выбором шаблона, вынесенным за цикл, проверка идёт примерно на треть быстрее.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Pattern(long address, int pass) => ((pass - 1) % 4) switch
    {
        0 => AddressPattern(address, pass),
        1 => ((address + pass / 4) & 1) == 0 ? 0xAAAAAAAAu : 0x55555555u,
        2 => 1u << (int)((address + pass / 4 + 3) % 32),
        _ => Xorshift(AddressPattern(address, pass))
    };

    private static uint AddressPattern(long address, int pass) =>
        unchecked(((uint)address * 0x9E3779B1u ^ (uint)(address >> 32) * 0x85EBCA6Bu) + (uint)pass * 0x7F4A7C15u);

    private static uint Xorshift(uint value)
    {
        value ^= value << 13;
        value ^= value >> 17;
        return value ^ value << 5;
    }

    public static string PatternName(int pass) => Patterns[(pass - 1) % 4];

    public static void FillBlock(Span<uint> block, long firstAddress, int pass, CancellationToken cancellationToken = default)
    {
        for (int index = 0; index < block.Length; index++)
        {
            if ((index & CancellationCheckMask) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            block[index] = Pattern(firstAddress + index, pass);
        }
    }

    // Сверяет блок с ожидаемыми значениями и записывает инверсию. Возвращает число ошибок.
    public static int CheckBlock(Span<uint> block, long firstAddress, int pass, bool backward, Action<long, uint, uint> onError,
        CancellationToken cancellationToken = default)
    {
        int errorCount = 0;
        for (int position = 0; position < block.Length; position++)
        {
            if ((position & CancellationCheckMask) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            int index = backward ? block.Length - 1 - position : position;
            uint expected = backward ? ~Pattern(firstAddress + index, pass) : Pattern(firstAddress + index, pass);
            uint actual = block[index];
            if (actual != expected)
            {
                errorCount++;
                onError(firstAddress + index, expected, actual);
                // При остановке на первой ошибке onError отменяет токен, и поток сразу выходит из блока.
                cancellationToken.ThrowIfCancellationRequested();
            }
            block[index] = ~expected;
        }
        return errorCount;
    }

    // Буфер выделяется вне сборщика мусора: после проверки память сразу возвращается Windows.
    // Каждый блок закрепляется в оперативной памяти (VirtualLock), чтобы Windows не вытеснила его в файл подкачки.
    // imitation — искусственная ошибка для проверки отображения: после заполнения в одном блоке переворачивается бит.
    // stopOnFirstError: блоки проверяются параллельно, и до остановки каждый поток может успеть найти
    // по одной ошибке, поэтому счётчик ошибок не больше числа потоков, а не строго единица.
    public static Task<DiagnosticResult> RunAsync(long bytes, DiagnosticMode mode, bool stopOnFirstError,
        IProgress<DiagnosticProgress> progress, CancellationToken cancellationToken, bool imitation = false)
    {
        // Буфер делится на 256 блоков из целых 32-битных слов. В блоке не меньше 8 слов (имитация портит ячейку 7)
        // и не больше int.MaxValue — больше не вмещает Span. Интерфейс передаёт объём, кратный 16 МБ, от 64 МБ.
        long wordsPerBlock = bytes / 4 / MapSize;
        if (bytes % (4 * MapSize) != 0 || wordsPerBlock is < 8 or > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(bytes), bytes, "Объём должен быть кратен 1 КБ и не меньше 8 КБ.");
        if (bytes > nint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(bytes), bytes, "Объём превышает предел для разрядности процесса.");
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));

        return Task.Run(async () =>
        {
            var result = new DiagnosticResult { RequestedBytes = bytes, Mode = ModeName(mode), AlgorithmName = Describe(mode) };
            try
            {
                await DiagnosticGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result.Status = DiagnosticStatus.Stopped;
                return result;
            }

            try
            {
                return Run(result, (int)wordsPerBlock, mode, stopOnFirstError, progress, cancellationToken, imitation);
            }
            finally
            {
                DiagnosticGate.Release();
            }
        });
    }

    private static unsafe DiagnosticResult Run(DiagnosticResult result, int blockWords, DiagnosticMode mode, bool stopOnFirstError,
        IProgress<DiagnosticProgress> progress, CancellationToken cancellationToken, bool imitation)
    {
        long bytes = result.RequestedBytes;
        using var currentProcess = Process.GetCurrentProcess();
        var (originalMinWorkingSet, originalMaxWorkingSet) = (currentProcess.MinWorkingSet, currentProcess.MaxWorkingSet);
        bool bufferLocked = true;
        bool restoreWorkingSet = false;
        var stopwatch = new Stopwatch();
        using var stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var blockAddresses = new nint[MapSize];
        int pass = 0, sentErrorCount = 0;
        long processedBytes = 0, cycleStart = 0, cycleBytes = 0;
        long lastReport = -200;
        var liveMap = new byte[MapSize];
        long lastCompletedFrame = -1000;
        bool mapChanged = true;
        string stage = "";
        var resultLock = new Lock();
        Span<uint> GetBlock(int index) => new((void*)blockAddresses[index], blockWords);

        void Report(bool force = false)
        {
            long now = stopwatch.ElapsedMilliseconds;
            if (!force && now - Interlocked.Read(ref lastReport) < 200) return;
            lock (resultLock)
            {
                if (!force && now - lastReport < 200) return;
                Interlocked.Exchange(ref lastReport, now);
                long done = Interlocked.Read(ref processedBytes);
                progress.Report(new(stage, pass, result.Passes, 100d * (done - cycleStart) / cycleBytes,
                    stopwatch.Elapsed, result.ErrorCount, done, mapChanged ? (byte[])result.Map.Clone() : null,
                    result.Errors.Count != sentErrorCount ? [.. result.Errors] : null, (byte[])liveMap.Clone()));
                mapChanged = false;
                sentErrorCount = result.Errors.Count;
            }
        }

        void OnError(int block, long address, uint expected, uint actual, bool parity = false)
        {
            lock (resultLock)
            {
                result.ErrorCount++;
                liveMap[block] = result.Map[block] = Error;
                mapChanged = true;
                if (result.Errors.Count < 1000)
                    result.Errors.Add(new(address * 4, expected, actual, parity ? stage + " · Нарушение чётности" : stage, pass));
            }
            if (stopOnFirstError)
            {
                stopSource.Cancel();
                stopSource.Token.ThrowIfCancellationRequested();
            }
        }

        try
        {
            stopSource.Token.ThrowIfCancellationRequested();
            // Предел закрепления зависит от минимального рабочего набора. Увеличиваем сначала максимум, затем минимум.
            try
            {
                currentProcess.MaxWorkingSet = checked(originalMaxWorkingSet + (nint)bytes);
                restoreWorkingSet = true;
                currentProcess.MinWorkingSet = checked(originalMinWorkingSet + (nint)bytes);
            }
            catch (Exception exception) when (exception is Win32Exception or OverflowException) { bufferLocked = false; }
            stopwatch.Start();

            // Блоки обрабатываются параллельно на всех ядрах; шаги идут по всему буферу,
            // поэтому данные не успевают остаться в кэше процессора между записью и проверкой.
            var parallelOptions = new ParallelOptions { CancellationToken = stopSource.Token };
            for (int index = 0; index < MapSize; index++)
            {
                parallelOptions.CancellationToken.ThrowIfCancellationRequested();
                blockAddresses[index] = VirtualAlloc(0, (nuint)blockWords * 4, 0x3000, 0x04);
                if (blockAddresses[index] == 0) throw new OutOfMemoryException();
                bufferLocked = bufferLocked && VirtualLock(blockAddresses[index], (nuint)blockWords * 4);
            }
            do
            {
                pass++;
                var plan = BuildPlan(mode, pass);
                cycleStart = processedBytes;
                cycleBytes = plan.Sum(step => step.WorkBytes(bytes));
                foreach (var step in plan)
                {
                    stage = step.Name;
                    result.Map.CopyTo(liveMap, 0);
                    liveMap.AsSpan().Replace(Ok, Waiting);
                    Parallel.For(0, MapSize, parallelOptions, order =>
                    {
                        int block = step.Kind == Operation.Backward ? MapSize - 1 - order : order;
                        bool copy = step.Kind == Operation.Copy;
                        if (copy && (block & step.PairMask) != step.Offset) return;
                        int partner = block ^ step.PairMask;
                        void MarkLive(byte state)
                        {
                            if (result.Map[block] != Error) liveMap[block] = state;
                            if (copy && result.Map[partner] != Error) liveMap[partner] = state;
                        }
                        lock (resultLock) MarkLive(Checking);
                        Report();
                        long first = (long)block * blockWords;
                        var data = GetBlock(block);
                        var token = parallelOptions.CancellationToken;
                        void ErrorAt(long word, uint expected, uint actual) =>
                            OnError(block, word, expected, actual, step.Kind == Operation.Parity &&
                                (uint.PopCount(expected ^ actual) & 1) != 0);
                        if (imitation && block == MapSize / 3 && step.Kind == Operation.CheckCopy)
                            data[7] ^= 1u << 5;
                        long traffic = RunStep(step, data, first, GetBlock(partner), block, ErrorAt, token);
                        if (imitation && block == MapSize / 3 && step.Inject)
                            data[7] ^= 1u << 5;
                        lock (resultLock)
                        {
                            if (step.VerifiesBlock && result.Map[block] == NotChecked)
                            {
                                result.Map[block] = Ok;
                                mapChanged = true;
                            }
                            MarkLive(StepDone);
                        }
                        Interlocked.Add(ref processedBytes, traffic);
                        Report();
                    });
                }
                result.Passes = pass;
                result.Map.CopyTo(liveMap, 0);
                // Publish a complete frame even when a sweep fits between periodic updates.
                if (pass == 1 || stopwatch.ElapsedMilliseconds - lastCompletedFrame >= 1000 ||
                    mode is DiagnosticMode.Quick or DiagnosticMode.Full)
                {
                    Report(force: true);
                    lastCompletedFrame = stopwatch.ElapsedMilliseconds;
                }
            } while (mode is DiagnosticMode.Continuous or DiagnosticMode.Stress);
            result.Status = DiagnosticStatus.Completed;
        }
        catch (OperationCanceledException exception) when (exception.CancellationToken == stopSource.Token && stopSource.IsCancellationRequested)
        {
            result.Status = cancellationToken.IsCancellationRequested ? DiagnosticStatus.Stopped : DiagnosticStatus.Errors;
        }
        catch (Exception exception)
        {
            result.Status = DiagnosticStatus.Failed;
            result.Detail = exception is OutOfMemoryException ? "Не удалось выделить память. Уменьшите объём и повторите проверку." : exception.Message;
        }
        finally
        {
            result.ProcessedBytes = processedBytes;
            result.ElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            if (!bufferLocked && result.Detail == "")
                result.Detail = "Буфер не удалось закрепить в оперативной памяти, часть страниц могла уйти в файл подкачки.";
            Free(blockAddresses);
            if (restoreWorkingSet)
            {
                try
                {
                    currentProcess.MinWorkingSet = originalMinWorkingSet;
                    currentProcess.MaxWorkingSet = originalMaxWorkingSet;
                }
                catch (Win32Exception) { }
            }
        }
        return result;
    }

    private static void Free(nint[] blockAddresses)
    {
        for (int blockIndex = 0; blockIndex < blockAddresses.Length; blockIndex++)
            if (blockAddresses[blockIndex] != 0)
                VirtualFree(blockAddresses[blockIndex], 0, 0x8000); // MEM_RELEASE освобождает и снимает закрепление.
        Array.Clear(blockAddresses);
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protection);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualLock(nint address, nuint size);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFree(nint address, nuint size, uint freeType);
}
