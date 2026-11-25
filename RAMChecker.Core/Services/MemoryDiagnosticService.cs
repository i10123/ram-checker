using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RAMChecker.Models;

namespace RAMChecker.Services;

// Map и Errors — копии, которые интерфейс может оставить у себя; null — без изменений с прошлого отчёта.
public record DiagnosticProgress(string Stage, int Pass, int Passes, double Percent, TimeSpan Elapsed,
    long ErrorCount, long ProcessedBytes, byte[]? Map, MemoryError[]? Errors);

// Собственный алгоритм «Свой адрес с инверсией». Один проход состоит из трёх шагов:
// 1) в каждую ячейку записывается значение шаблона; четыре шаблона сменяют друг друга по проходам:
//    свой адрес, шахматный, бегущая единица, случайный;
// 2) прямой обход: ячейка сверяется со своим значением, затем в неё пишется инверсия;
// 3) обратный обход: сверяется инверсия, затем возвращается исходное значение.
// Шаблон адреса находит ошибки адресации (две ячейки отвечают по одному адресу),
// шахматный — влияние соседних ячеек друг на друга, бегущая единица — замыкание соседних линий данных
// внутри слова, случайный — ошибки, которые проявляются только на определённых данных; шаги 2–3 — «залипшие» биты.
public static partial class MemoryDiagnosticService
{
    public const string Algorithm = "Свой адрес с инверсией";
    // Буфер делится на столько же блоков, сколько клеток на карте памяти.
    public const int MapSize = 256;
    // Waiting — блок уже проверялся без ошибок, но текущий проход до него ещё не дошёл.
    public const byte NotChecked = 0, Ok = 1, Error = 2, Waiting = 3;
    private static readonly string[] Steps = ["Заполнение", "Проверка вперёд", "Проверка назад"];
    private static readonly string[] Patterns = ["Свой адрес", "Шахматный шаблон", "Бегущая единица", "Случайный шаблон"];
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
        2 => 1u << (int)((address + pass) % 32),
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
            if (block[index] != expected)
            {
                errorCount++;
                onError(firstAddress + index, expected, block[index]);
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
    public static Task<DiagnosticResult> RunAsync(long bytes, TimeSpan? duration, bool stopOnFirstError,
        IProgress<DiagnosticProgress> progress, CancellationToken cancellationToken, bool imitation = false)
    {
        // Буфер делится на 256 блоков из целых 32-битных слов. В блоке не меньше 8 слов (имитация портит ячейку 7)
        // и не больше int.MaxValue — больше не вмещает Span. Интерфейс передаёт объём, кратный 16 МБ, от 64 МБ.
        long wordsPerBlock = bytes / 4 / MapSize;
        if (bytes % (4 * MapSize) != 0 || wordsPerBlock is < 8 or > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(bytes), bytes, "Объём должен быть кратен 1 КБ и не меньше 8 КБ.");
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "Длительность должна быть больше нуля.");
        return Task.Run(() => Run(bytes, (int)wordsPerBlock, duration, stopOnFirstError, progress, cancellationToken, imitation));
    }

    private static unsafe DiagnosticResult Run(long bytes, int blockWords, TimeSpan? duration, bool stopOnFirstError,
        IProgress<DiagnosticProgress> progress, CancellationToken cancellationToken, bool imitation)
    {
        var result = new DiagnosticResult { RequestedBytes = bytes };
        // Закрепить можно не больше минимального рабочего набора процесса, поэтому он увеличивается на объём буфера
        // (сначала максимум — минимум не может его превышать). Если Windows отказала, проверка идёт без закрепления.
        using var currentProcess = Process.GetCurrentProcess();
        var (originalMinWorkingSet, originalMaxWorkingSet) = (currentProcess.MinWorkingSet, currentProcess.MaxWorkingSet);
        bool bufferLocked = true;
        try
        {
            currentProcess.MaxWorkingSet = originalMaxWorkingSet + (nint)bytes;
            currentProcess.MinWorkingSet = originalMinWorkingSet + (nint)bytes;
        }
        catch (Win32Exception) { bufferLocked = false; }
        var stopwatch = Stopwatch.StartNew();
        using var stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var blockAddresses = new nint[MapSize];
        int pass = 0, stepIndex = 0, finishedBlocks = 0, sentErrorCount = 0;
        var resultLock = new Lock(); // System.Threading.Lock (.NET 9) — отдельный объект только для синхронизации
        long processedBlockCount = 0; // сколько раз блоки уже записаны или проверены — для скорости проверки
        long lastReportMilliseconds = -1000, errorCountAtLastMap = 0; // счётчик ошибок на момент последнего снимка карты
        Span<uint> GetBlock(int blockIndex) => new((void*)blockAddresses[blockIndex], blockWords);

        // Не чаще пяти раз в секунду из любого потока, иначе интерфейс не успевает перерисовываться.
        // В конце прохода отчёт отправляется всегда, чтобы счётчик проходов на экране не отставал; карту он обычно не несёт,
        // иначе быстрые проходы всегда показывали бы полностью проверенную карту.
        // Счётчик, карта и список ошибок копируются под той же блокировкой, что и в OnError, поэтому в одном отчёте
        // они согласованы. Отчёт отправляется внутри блокировки, чтобы отчёты разных потоков не обгоняли друг друга.
        // Список ошибок копируется, только когда в нём появились новые записи. Если с прошлого снимка карты
        // появились ошибки, карта отправляется и в конце прохода — иначе автосохранение записало бы новые ошибки
        // вместе со старой картой без красных клеток.
        void Report(bool forceReport = false)
        {
            long nowMilliseconds = stopwatch.ElapsedMilliseconds, lastMilliseconds = Interlocked.Read(ref lastReportMilliseconds);
            if (!forceReport && (nowMilliseconds - lastMilliseconds < 200 || Interlocked.CompareExchange(ref lastReportMilliseconds, nowMilliseconds, lastMilliseconds) != lastMilliseconds))
                return;
            lock (resultLock)
            {
                MemoryError[]? errorsCopy = result.Errors.Count == sentErrorCount ? null : [.. result.Errors];
                byte[]? mapCopy = forceReport && result.ErrorCount == errorCountAtLastMap ? null : (byte[])result.Map.Clone();
                (sentErrorCount, errorCountAtLastMap) = (result.Errors.Count, mapCopy is null ? errorCountAtLastMap : result.ErrorCount);
                // В отчёте конца прохода stepIndex уже равен 3, а finishedBlocks — 256: шаг берётся последний, процент не выше 100.
                progress.Report(new($"{PatternName(pass)} · {Steps[Math.Min(stepIndex, 2)]}", pass, result.Passes, Math.Min(100, duration is { } testDuration
                    ? stopwatch.Elapsed / testDuration * 100
                    : (stepIndex * MapSize + finishedBlocks) * 100.0 / (3 * MapSize)), stopwatch.Elapsed, result.ErrorCount,
                    Interlocked.Read(ref processedBlockCount) * blockWords * 4, mapCopy, errorsCopy));
            }
        }

        void OnError(int block, long address, uint expected, uint actual)
        {
            lock (resultLock)
            {
                result.ErrorCount++;
                result.Map[block] = Error;
                if (result.Errors.Count < 1000)
                    result.Errors.Add(new(address * 4, expected, actual, $"{PatternName(pass)} · {Steps[stepIndex]}", pass));
            }
            if (stopOnFirstError)
                stopSource.Cancel();
        }

        try
        {
            // Блоки обрабатываются параллельно на всех ядрах; шаги идут по всему буферу,
            // поэтому данные не успевают остаться в кэше процессора между записью и проверкой.
            var parallelOptions = new ParallelOptions { CancellationToken = stopSource.Token };
            do
            {
                pass++;
                // Каждый проход получает буфер заново: Windows может выдать другие физические страницы,
                // тогда за несколько проходов проверяется больше памяти, чем один буфер. Это не гарантировано.
                Free(blockAddresses);
                for (int blockIndex = 0; blockIndex < MapSize; blockIndex++)
                {
                    stopSource.Token.ThrowIfCancellationRequested();
                    blockAddresses[blockIndex] = VirtualAlloc(0, (nuint)blockWords * 4, 0x3000, 0x04); // MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE
                    if (blockAddresses[blockIndex] == 0)
                        throw new OutOfMemoryException();
                    bufferLocked = bufferLocked && VirtualLock(blockAddresses[blockIndex], (nuint)blockWords * 4);
                }
                // В начале прохода проверенные блоки снова ждут своей очереди: по карте видно, как идёт проход.
                result.Map.AsSpan().Replace(Ok, Waiting);
                for (stepIndex = 0; stepIndex < Steps.Length; stepIndex++)
                {
                    finishedBlocks = 0;
                    Parallel.For(0, MapSize, parallelOptions, order =>
                    {
                        int blockIndex = stepIndex == 2 ? MapSize - 1 - order : order;
                        long firstAddress = (long)blockIndex * blockWords;
                        if (stepIndex == 0)
                        {
                            FillBlock(GetBlock(blockIndex), firstAddress, pass, stopSource.Token);
                            if (imitation && blockIndex == MapSize / 3)
                                GetBlock(blockIndex)[7] ^= 1u << 5;
                        }
                        // Клетка карты зеленеет, когда её блок прошёл текущий проход без ошибок. Карта меняется только
                        // под блокировкой, как и в OnError, поэтому снимок в Report всегда согласован со счётчиком.
                        else if (CheckBlock(GetBlock(blockIndex), firstAddress, pass, stepIndex == 2, (address, expectedValue, actualValue) => OnError(blockIndex, address, expectedValue, actualValue), stopSource.Token) == 0 && stepIndex == 2)
                            lock (resultLock)
                                if (result.Map[blockIndex] != Error)
                                    result.Map[blockIndex] = Ok;
                        Interlocked.Increment(ref finishedBlocks);
                        Interlocked.Increment(ref processedBlockCount);
                        Report();
                    });
                }
                result.Passes = pass;
                Report(forceReport: true);
            } while (duration is not { } durationLimit || stopwatch.Elapsed < durationLimit);
            result.Status = DiagnosticStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            result.Status = cancellationToken.IsCancellationRequested ? DiagnosticStatus.Stopped : DiagnosticStatus.Errors;
        }
        // Память заняли другие программы уже после первых проходов: их результат сохраняется.
        catch (OutOfMemoryException) when (pass > 1)
        {
            result.Status = DiagnosticStatus.Stopped;
            result.Detail = $"На проходе {pass} не удалось снова выделить память, выполнено проходов: {result.Passes}.";
        }
        catch (Exception exception)
        {
            result.Status = DiagnosticStatus.Failed;
            result.Detail = exception is OutOfMemoryException ? "Не удалось выделить память. Уменьшите объём и повторите проверку." : exception.Message;
        }
        finally
        {
            result.ElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            result.Map.AsSpan().Replace(Waiting, Ok); // В итоге важно лишь, проверялся ли блок.
            if (!bufferLocked && result.Detail == "")
                result.Detail = "Буфер не удалось закрепить в оперативной памяти, часть страниц могла уйти в файл подкачки.";
            Free(blockAddresses);
            try
            {
                currentProcess.MinWorkingSet = originalMinWorkingSet;
                currentProcess.MaxWorkingSet = originalMaxWorkingSet;
            }
            catch (Win32Exception) { }
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
