using System.IO;
using System.Text;
using RAMChecker.Models;
using RAMChecker.Services;
using static RAMChecker.Services.MemoryDiagnosticService;

// Ручная проверка на реальном оборудовании: нужны драйвер PawnIO и права администратора.
if (args is ["spd", var outFile])
{
    var log = new StringBuilder();
    try
    {
        using var reader = new SpdReader();
        foreach (var cyclicRun in reader.ReadAll())
            log.AppendLine(cyclicRun.ToString());
        log.AppendLine("done");
    }
    catch (Exception ex) { log.AppendLine(ex.ToString()); }
    File.WriteAllText(outFile, log.ToString());
    return 0;
}

int failedCount = 0;
void Check(bool passed, string checkName)
{
    Console.WriteLine((passed ? "OK    " : "FAIL  ") + checkName);
    if (!passed) failedCount++;
}

// --- Алгоритм ---
var block = new uint[100_000];
var foundErrors = new List<(long, uint, uint)>();
void OnError(long address, uint expected, uint actual) => foundErrors.Add((address, expected, actual));

FillBlock(block, 1000, 1);
Check(CheckBlock(block, 1000, 1, false, OnError) == 0 && CheckBlock(block, 1000, 1, true, OnError) == 0, "чистый блок: 0 ошибок за оба обхода");
Check(block.Select((value, index) => value == Pattern(1000 + index, 1)).All(matches => matches), "после прохода в памяти снова исходные значения");
Check(new[] { 1, 3, 4 }.All(pass => Enumerable.Range(0, 99_999).All(index => Pattern(index, pass) != Pattern(index + 1, pass))), "соседние адреса получают разные значения");
Check(Pattern(5, 1) != Pattern(5, 2) && Pattern(5, 4) != Pattern(5, 8), "разные проходы дают разные значения");
Check(Enumerable.Range(0, 1000).All(index => Pattern(index, 2) == ~Pattern(index + 1, 2) && Pattern(index, 6) == ~Pattern(index, 2)), "шахматный шаблон: у соседей противоположные биты, рисунок меняется через четыре прохода");
Check(Enumerable.Range(0, 1000).All(index => uint.PopCount(Pattern(index, 3)) == 1 && Pattern(index + 1, 3) == uint.RotateLeft(Pattern(index, 3), 1)), "бегущая единица: один бит, у соседа сдвинут на разряд");
Check(Enumerable.Range(0, 100_000).Average(index => uint.PopCount(Pattern(index, 4))) is > 15.5 and < 16.5, "случайный шаблон: в среднем половина битов единичные");
Check(Enumerable.Range(32, 30).All(bitShift => Pattern(7, 1) != Pattern(7 + (1L << bitShift), 1) && Pattern(7, 4) != Pattern(7 + (1L << bitShift), 4)), "адресный и случайный шаблоны различают ячейки на расстоянии 16 ГБ и больше");
Check(PatternName(1) == "Свой адрес" && PatternName(3) == "Бегущая единица" && PatternName(8) == "Случайный шаблон", "шаблоны сменяются по кругу");

FillBlock(block, 0, 2);
block[500] ^= 1u << 7;
foundErrors.Clear();
Check(CheckBlock(block, 0, 2, false, OnError) == 1 && foundErrors[0].Item1 == 500 && (foundErrors[0].Item2 ^ foundErrors[0].Item3) == 1u << 7, "перевёрнутый бит найден прямым обходом");

FillBlock(block, 0, 3);
block[700] = block[100]; // ошибка адресации: ячейка 700 отвечает данными ячейки 100
foundErrors.Clear();
Check(CheckBlock(block, 0, 3, false, OnError) == 1 && foundErrors[0].Item1 == 700, "ошибка адресации найдена");

FillBlock(block, 0, 4);
CheckBlock(block, 0, 4, false, OnError);
block[42] |= 1u << 31; block[42] &= ~1u; // «залипшие» биты после записи инверсии
foundErrors.Clear();
int backwardErrors = CheckBlock(block, 0, 4, true, OnError);
Check(backwardErrors == 1 && foundErrors[0].Item1 == 42, "залипший бит найден обратным обходом");

// --- Отмена и несколько ошибок ---
var corruptedBlock = new uint[1 << 20];
FillBlock(corruptedBlock, 0, 1);
for (int index = 0; index < corruptedBlock.Length; index++)
    corruptedBlock[index] ^= 1; // испорчена каждая ячейка
int foundCount = 0;
using (var stopSource = new CancellationTokenSource())
    try { CheckBlock(corruptedBlock, 0, 1, false, (_, _, _) => { foundCount++; stopSource.Cancel(); }, stopSource.Token); }
    catch (OperationCanceledException) { }
Check(foundCount == 1, "при остановке на ошибке блок прерывается сразу после первой ошибки");
foundCount = 0;
using (var stopSource = new CancellationTokenSource())
    try
    {
        Parallel.For(0, 64, new ParallelOptions { CancellationToken = stopSource.Token }, blockIndex => CheckBlock(corruptedBlock.AsSpan(blockIndex << 14, 1 << 14), blockIndex << 14, 1, false,
            (_, _, _) => { Interlocked.Increment(ref foundCount); stopSource.Cancel(); }, stopSource.Token));
    }
    catch (OperationCanceledException) { }
Check(foundCount >= 1 && foundCount <= Environment.ProcessorCount, $"64 испорченных блока параллельно: {foundCount} ошибок, не больше одной на поток");

// --- Полный запуск ---
var progress = new Progress<DiagnosticProgress>(_ => { });
var shortRun = await RunAsync(64L << 20, TimeSpan.FromSeconds(1), false, progress, CancellationToken.None);
Check(shortRun.Status == DiagnosticStatus.Completed && shortRun.ErrorCount == 0 && shortRun.Passes >= 1, $"64 МБ за 1 с: {shortRun.StatusText}, проходов {shortRun.Passes}");
Check(shortRun.Map.All(cellState => cellState == Ok) && shortRun.CheckedBytes == 64L << 20, "карта памяти полностью зелёная, проверен весь объём");
Check(shortRun.Passes > 1 && shortRun.Detail == "", $"за 1 с несколько проходов, буфер закреплён в оперативной памяти: {shortRun.Detail}");
long before = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
await RunAsync(1L << 30, TimeSpan.FromSeconds(1), false, progress, CancellationToken.None);
long after = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
Check(after - before < 64L << 20, $"после проверки 1 ГБ память возвращена системе (прирост {(after - before) >> 20} МБ)");
var imitationRun = await RunAsync(64L << 20, TimeSpan.FromSeconds(1), false, progress, CancellationToken.None, imitation: true);
Check(imitationRun.Status == DiagnosticStatus.Completed && imitationRun.ErrorCount == imitationRun.Passes && imitationRun.Map.Count(cellState => cellState == Error) == 1 && imitationRun.Map[MapSize / 3] == Error
    && (imitationRun.Errors[0].Expected ^ imitationRun.Errors[0].Actual) == 1u << 5, $"имитация: одна ошибка за проход в блоке {MapSize / 3 + 1}, ошибок {imitationRun.ErrorCount}");
var imitationStopRun = await RunAsync(64L << 20, null, true, progress, CancellationToken.None, imitation: true);
Check(imitationStopRun.Status == DiagnosticStatus.Errors && imitationStopRun.ErrorCount == 1, "имитация с остановкой на первой ошибке");
using (var timeoutSource = new CancellationTokenSource(1000))
{
    double maxPercent = 0;
    var cyclicRun = await RunAsync(1L << 30, null, false, new SyncProgress<DiagnosticProgress>(progressReport => maxPercent = Math.Max(maxPercent, progressReport.Percent)), timeoutSource.Token);
    Check(maxPercent is > 0 and <= 100, $"циклическая проверка: процент прохода не выходит за 100 (наибольший {maxPercent:0})");
    Check(cyclicRun.Status == DiagnosticStatus.Stopped && cyclicRun.ElapsedSeconds < 1.7, $"циклическая проверка 1 ГБ останавливается за {(cyclicRun.ElapsedSeconds - 1) * 1000:0} мс после отмены");
}

// --- Неверные параметры ---
async Task<bool> Rejected(long bytes, double seconds)
{
    try { await RunAsync(bytes, TimeSpan.FromSeconds(seconds), false, progress, CancellationToken.None); }
    catch (ArgumentOutOfRangeException) { return true; }
    return false;
}
Check(await Rejected(0, 1) && await Rejected(-1L << 20, 1) && await Rejected(4096, 1), "объём 0, отрицательный и меньше 8 КБ (имитация вышла бы за блок) отклоняется");
Check(await Rejected((1L << 20) + 100, 1) && await Rejected(1L << 42, 1), "объём, не кратный 1 КБ или с блоком больше int.MaxValue слов, отклоняется");
Check(await Rejected(64L << 20, 0) && await Rejected(64L << 20, -5), "нулевая и отрицательная длительность отклоняется");

// --- Промежуточное сохранение и история ---
// Отчёты о ходе переносятся в сессию так же, как на странице тестирования; затем история записывается,
// как при автосохранении, и читается заново, как после аварийного закрытия.
var session = new DiagnosticResult { RequestedBytes = 64L << 20 };
await RunAsync(64L << 20, TimeSpan.FromSeconds(1), false, new SyncProgress<DiagnosticProgress>(session.ApplyProgress), CancellationToken.None, imitation: true);
var storage = new LocalStorage(Path.Combine(Path.GetTempPath(), "MemScope.Tests"));
storage.Write("history.json", new List<DiagnosticResult> { session });
var restored = storage.ReadHistory(out _)[0];
Check(restored.Status == DiagnosticStatus.Interrupted && restored.ErrorCount > 0 && restored.Errors.Count == restored.ErrorCount
    && restored.Map[MapSize / 3] == Error && !restored.Map.Contains(Waiting), $"после аварии счётчик, список ошибок и карта согласованы: {restored.ErrorCount} = {restored.Errors.Count}");
// Авария сразу после первого отчёта с ошибкой: проход на 64 МБ короче 200 мс, поэтому первая ошибка приходит
// в отчёте конца прохода, раньше очередного периодического снимка карты.
var early = new DiagnosticResult { RequestedBytes = 64L << 20 };
await RunAsync(64L << 20, TimeSpan.FromSeconds(0.5), false, new SyncProgress<DiagnosticProgress>(progressReport =>
{
    if (early.ErrorCount == 0)
        early.ApplyProgress(progressReport);
}), CancellationToken.None, imitation: true);
Check(early.ErrorCount == 1 && early.Errors.Count == 1 && early.Map[MapSize / 3] == Error, $"первая ошибка сохраняется вместе с красной клеткой карты (красных {early.Map.Count(cellState => cellState == Error)})");
string historyPath = Path.Combine(storage.DirectoryPath, "history.json");
File.WriteAllText(historyPath, """[{"Status": "Stopped", "ErrorCount": 0}, {"Status": "Running", "Passes": 1}]""");
var oldFormatHistory = storage.ReadHistory(out _);
storage.Write("history.json", oldFormatHistory);
Check(oldFormatHistory[0].Status == DiagnosticStatus.Stopped && oldFormatHistory[1].Status == DiagnosticStatus.Interrupted && File.ReadAllText(historyPath).Contains("\"Status\": \"Stopped\""),
    "история прежнего формата читается, статус по-прежнему хранится строкой");
File.WriteAllText(historyPath, """[{"Status": "Stopped"}, {"Status": "Completed", "Map": ""}, {"Status": "Completed", "Map": null}, {"Status": 42}]""");
var checkedHistory = storage.ReadHistory(out int skippedRecords);
Check(checkedHistory.Count == 1 && skippedRecords == 3 && File.Exists(Path.Combine(storage.DirectoryPath, "history.bad.json")),
    "записи с пустой или отсутствующей картой и неизвестным статусом пропускаются, исходный файл сохранён");
File.WriteAllText(historyPath, """[{"Status": "Stopped"}, {"Status": "UnknownStatus"}, {"Status": "Completed", "Passes": "abc"}, 5]""");
bool readable = true;
try { checkedHistory = storage.ReadHistory(out skippedRecords); }
catch (System.Text.Json.JsonException) { readable = false; }
Check(readable && checkedHistory.Count == 1 && skippedRecords == 3, "неизвестный статус и неверный тип поля не мешают прочитать остальные записи");
File.WriteAllText(historyPath, "{ это не список записей");
checkedHistory = storage.ReadHistory(out skippedRecords);
Check(checkedHistory.Count == 0 && skippedRecords == -1 && File.ReadAllText(Path.Combine(storage.DirectoryPath, "history.bad.json")).StartsWith("{ это"),
    "файл истории, который не разбирается целиком, сохраняется рядом, история начинается заново");
storage.Write("history.json", new[] {
    new DiagnosticResult { Status = DiagnosticStatus.Stopped, Samples = [new(2, 10, null), new(1, 20, null)] },
    new DiagnosticResult { Status = DiagnosticStatus.Completed, Samples = [new(0, 10, null), new(0, 20, null)] }
});
Check(storage.ReadHistory(out skippedRecords).Count == 1 && skippedRecords == 1,
    "история принимает совпадающие отметки времени и отбрасывает обратный ход времени");
var commitSnapshot = new MemorySnapshot(100, 25, 120, 150, null);
Check(commitSnapshot.RemainingCommit == 30 && (commitSnapshot with { Committed = 180 }).RemainingCommit == 0
    && (commitSnapshot with { CommitLimit = null }).RemainingCommit is null,
    "остаток лимита выделения учитывает занятый объём и отсутствие данных");
int notices = 0;
early.PropertyChanged += (_, _) => notices++;
early.ApplyProgress(new("", 1, 1, 0, TimeSpan.FromHours(25), 1, 0, null, null));
Check(notices == 1 && early.DurationText == "25:00:00", $"идущая проверка сообщает экрану об изменениях, длительность больше суток: {early.DurationText}");

// --- Замер скорости ---
// Внутри замера сверяются сумма прочитанных слов и копия буфера, при расхождении он выбрасывает исключение.
var benchmark = await MemoryBenchmark.RunAsync();
Check(benchmark is { ReadMegabytesPerSecond: > 0, WriteMegabytesPerSecond: > 0, CopyMegabytesPerSecond: > 0, LatencyNanoseconds: > 0 }, $"замер скорости выполняется и проходит сверку данных: {benchmark}");

// --- SPD ---
Check(SpdReader.Crc16(Encoding.ASCII.GetBytes("123456789"), 9) == 0x31C3, "CRC16 совпадает с эталоном CRC-16/XMODEM");
var spd = new byte[128];
spd[2] = 0x0C; spd[18] = 6;                                 // DDR4, tCK = 0,75 нс
spd[4] = 0x05; spd[12] = 0x01; spd[13] = 0x03;              // кристаллы 8 Гбит x8, один ранг, шина 64 бита → 8 ГБ
spd[20] = 0b1111_1000; spd[21] = 0xFF; spd[22] = 0x03;      // CL10–CL24
spd[24] = 96; spd[25] = 108; spd[26] = 108;                 // tAA 12 нс, tRCD/tRP 13,5 нс
spd[27] = 0x10; spd[28] = 210; spd[29] = 62;                // tRAS 26,25 нс, tRC 39,75 нс
int crc = SpdReader.Crc16(spd, 126);
spd[126] = (byte)crc; spd[127] = (byte)(crc >> 8);
var info = SpdReader.DecodeDdr4(0x50, spd);
Check(info is { NominalSpeed: 2666, CasLatency: 16, RasToCasDelay: 18, RowPrecharge: 18, RowActiveTime: 35, RowCycleTime: 53, SizeGB: 8 }, $"DDR4-2666 16-18-18-35, 8 ГБ: {info}");
spd[24] = 95; spd[123] = unchecked((byte)-125);             // 11,875 − 0,125 = 11,75 нс → 15,67 такта → CL16
Check(SpdReader.DecodeDdr4(0x50, spd) is null, "испорченная контрольная сумма отклоняется");
crc = SpdReader.Crc16(spd, 126); spd[126] = (byte)crc; spd[127] = (byte)(crc >> 8);
Check(SpdReader.DecodeDdr4(0x50, spd)?.CasLatency == 16, "поправка в пикосекундах учитывается");
// Испорченные поля с верной контрольной суммой: CRC защищает только от сбоев чтения.
byte[] CorruptDdr4(Action<byte[]> change)
{
    var spdCopy = (byte[])spd.Clone();
    change(spdCopy);
    int cellState = SpdReader.Crc16(spdCopy, 126);
    (spdCopy[126], spdCopy[127]) = ((byte)cellState, (byte)(cellState >> 8));
    return spdCopy;
}
Check(SpdReader.DecodeDdr4(0x50, CorruptDdr4(spdCopy => (spdCopy[18], spdCopy[125]) = (0, 0))) is null && SpdReader.DecodeDdr4(0x50, CorruptDdr4(spdCopy => spdCopy[18] = 1)) is null, "нулевой и слишком короткий tCK отклоняются");
Check(SpdReader.DecodeDdr4(0x50, CorruptDdr4(spdCopy => spdCopy[20] = spdCopy[21] = spdCopy[22] = 0)) is null, "пустой список поддерживаемых CL отклоняется");
// Поддерживается только CL7, а tAA = 30 нс требует CL40 — за пределами списка.
Check(SpdReader.DecodeDdr4(0x50, CorruptDdr4(spdCopy => (spdCopy[20], spdCopy[21], spdCopy[22], spdCopy[24], spdCopy[123]) = (1, 0, 0, 240, 0))) is null, "расчётный CL выше списка поддерживаемых отклоняется");
Check(SpdReader.DecodeDdr4(0x50, spd[..100]) is null, "слишком короткий паспорт отклоняется");
// tCK = 0,938 нс (8 шагов по 0,125 нс минус 62 пс) — это DDR4-2133, хотя 2 000 000 / 938 = 2132.
Check(SpdReader.DecodeDdr4(0x50, CorruptDdr4(spdCopy => (spdCopy[18], spdCopy[125]) = (8, unchecked((byte)-62))))?.NominalSpeed == 2133, "скорость округляется до стандартной: 2133, а не 2132");
var xmp = new byte[128];
xmp[0] = 0x0C; xmp[1] = 0x4A; xmp[2] = 1;                    // XMP 2.0, профиль 1
const int xmpProfile = 9;
xmp[xmpProfile] = 0xA3;                                      // 1,35 В
xmp[xmpProfile + 3] = 5;                                    // tCK = 0,625 нс → DDR4-3200
xmp[xmpProfile + 8] = 81; xmp[xmpProfile + 9] = 90; xmp[xmpProfile + 10] = 90;
xmp[xmpProfile + 37] = unchecked((byte)-125);                // tAA = 81 × 125 − 125 пс
xmp[xmpProfile + 11] = 0x10;
xmp[xmpProfile + 12] = 180; xmp[xmpProfile + 13] = 14;       // tRAS/tRC
var xmpProfileInfo = SpdReader.DecodeDdr4Xmp(xmp);
Check(xmpProfileInfo is { SpeedMTs: 3200, CasLatency: 16, RasToCasDelay: 18, RowPrecharge: 18, RowActiveTime: 36, RowCycleTime: 54, Voltage: 1.35 },
    $"XMP 2.0: DDR4-3200 16-18-18-36, 1,35 В: {xmpProfileInfo}");
Check(SpdReader.DecodeDdr4Xmp(new byte[128]) is null, "XMP без сигнатуры и профиля не принимается");

// --- SMBIOS ---
// Таблица из двух записей: планка DDR4 на 8 ГБ с тремя строками и конец таблицы. Первая запись — другого типа и пропускается.
byte[] Record(byte type, int formattedLength, Action<byte[]> fill, params string[] strings)
{
    var recordBytes = new byte[formattedLength];
    (recordBytes[0], recordBytes[1]) = (type, (byte)formattedLength);
    fill(recordBytes);
    var stringBytes = strings.Length == 0 ? [(byte)0, (byte)0] : strings.SelectMany(value => Encoding.ASCII.GetBytes(value + "\0")).Append((byte)0).ToArray();
    return [.. recordBytes, .. stringBytes];
}
byte[] Table(params byte[][] records)
{
    byte[] table = [.. records.SelectMany(record => record), .. Record(127, 4, _ => { })];
    return [0, 3, 3, 0, .. BitConverter.GetBytes(table.Length), .. table]; // заголовок RawSMBIOSData
}
Action<byte[]> Dimm(ushort size, byte type) => record =>
{
    BitConverter.GetBytes(size).CopyTo(record, 0x0C);
    (record[0x0E], record[0x10], record[0x11], record[0x12], record[0x17], record[0x18], record[0x1A]) = (13, 1, 2, type, 3, 4, 5);
    BitConverter.GetBytes((ushort)3200).CopyTo(record, 0x20);
    BitConverter.GetBytes((ushort)1350).CopyTo(record, 0x26);
    if (size == 0x7FFF)
        BitConverter.GetBytes(16384).CopyTo(record, 0x1C); // расширенный размер в МБ
};
var smbios = SmbiosMemory.Parse(Table(Record(1, 8, _ => { }, "BIOS"),
    Record(17, 0x28, Dimm(8192, 26), "DIMM 1", "Channel A", "Kingston ", "1234ABCD", "KHX3200 "),
    Record(17, 0x28, Dimm(0, 26), "DIMM 2", "Channel B"),   // пустой слот
    Record(17, 0x28, Dimm(0x7FFF, 34), "DIMM 3", "Channel C", "Kingston ", "1234ABCD", "KHX3200 ")));
Check(smbios.Count == 2 && smbios[0] is { Size_GB: 8, RAM_maker: "Kingston", RAM_partNumber: "KHX3200", RAM_serialNumber: "1234ABCD", RAM_type: "DDR4",
    RAM_formFactor: "SO-DIMM", RAM_slot: "Channel A · DIMM 1", RAM_speed: "3200 MT/s (1600 МГц)" } && smbios[0].RAM_voltage == $"{1350 / 1000m:0.00} В", "SMBIOS: планка разобрана, пустой слот и чужая запись пропущены");
Check(smbios[1] is { RAM_type: "DDR5", Number: 2, Size_GB: 16 }, "SMBIOS: нумерация идёт по найденным планкам, большой размер берётся из расширенного поля");
Check(SmbiosMemory.Parse(Table(Record(17, 0x1B, record => (record[0x0C], record[0x0D], record[0x12]) = (0, 0x10, 24), "A", "B", "C", "D", "E")))[0] is { Size_GB: 4, RAM_speed: ModuleMemoryModel.NoData, RAM_voltage: ModuleMemoryModel.NoData },
    "SMBIOS: у короткой записи нет скорости и напряжения");
Check(SmbiosMemory.Parse(Table(Record(17, 0x28, Dimm(8192, 26), "A"), [17, 0xFF, 0, 0], Record(17, 0x28, Dimm(8192, 26), "A"))).Count == 1, "SMBIOS: испорченная запись обрывает разбор");
Check(SmbiosMemory.Read().Count >= 0, "SMBIOS: таблица этого компьютера читается");

// Паспорт DDR5-4800 40-39-39-77, 16 ГБ: кристаллы 16 Гбит x8, один ранг, два подканала по 32 бита.
var spd5 = new byte[640];
(spd5[2], spd5[4], spd5[6], spd5[235]) = (0x12, 0x04, 0x20, 0x22);
(spd5[20], spd5[21], spd5[24], spd5[25]) = (0xA0, 0x01, 0xFF, 0xFF);           // tCK 416 пс, CL20–CL50
(spd5[30], spd5[31], spd5[32], spd5[33], spd5[34], spd5[35]) = (0x80, 0x3E, 0x80, 0x3E, 0x80, 0x3E); // tAA, tRCD, tRP 16 нс
(spd5[36], spd5[37], spd5[38], spd5[39]) = (0x00, 0x7D, 0x80, 0xBB);           // tRAS 32 нс, tRC 48 нс
byte[] CorruptDdr5(Action<byte[]> change)
{
    var spdCopy = (byte[])spd5.Clone();
    change(spdCopy);
    int cellState = SpdReader.Crc16(spdCopy, 510);
    (spdCopy[510], spdCopy[511]) = ((byte)cellState, (byte)(cellState >> 8));
    return spdCopy;
}
var info5 = SpdReader.DecodeDdr5(0x50, CorruptDdr5(_ => { }));
Check(info5 is { NominalSpeed: 4800, CasLatency: 40, RasToCasDelay: 39, RowPrecharge: 39, RowActiveTime: 77, RowCycleTime: 116, SizeGB: 16 }, $"DDR5-4800 40-39-39-77, 16 ГБ: {info5}");
Check(SpdReader.DecodeDdr5(0x50, spd5) is null, "DDR5: испорченная контрольная сумма отклоняется");
Check(SpdReader.DecodeDdr5(0x50, CorruptDdr5(spdCopy => spdCopy[2] = 0x0C)) is null && SpdReader.DecodeDdr4(0x50, CorruptDdr5(_ => { })) is null, "DDR5 и DDR4 не принимают паспорта друг друга");
Check(SpdReader.DecodeDdr5(0x50, CorruptDdr5(spdCopy => spdCopy[24] = spdCopy[25] = 0)) is null && SpdReader.DecodeDdr5(0x50, CorruptDdr5(spdCopy => spdCopy[20] = spdCopy[21] = 0)) is null, "DDR5: пустой список CL и нулевой tCK отклоняются");
Check(SpdReader.DecodeDdr5(0x50, CorruptDdr5(spdCopy => (spdCopy[24], spdCopy[25], spdCopy[26], spdCopy[27], spdCopy[28]) = (1, 0, 0, 0, 0))) is null, "DDR5: расчётный CL выше списка поддерживаемых отклоняется");
Check(SpdReader.DecodeDdr5(0x50, CorruptDdr5(spdCopy => spdCopy[4] = 0)) is null && SpdReader.DecodeDdr5(0x50, spd5[..300]) is null, "DDR5: нулевая плотность и короткий паспорт отклоняются");
Check(SpdReader.DecodeDdr5(0x50, CorruptDdr5(spdCopy => (spdCopy[234], spdCopy[235]) = (0x08, 0x2A)))?.SizeGB == 32, "DDR5: два ранга удваивают объём, байты ECC в него не входят");
Check(SpdReader.DecodeDdr5(0x50, CorruptDdr5(spdCopy => (spdCopy[20], spdCopy[21]) = (0x90, 0x01)))?.NominalSpeed == 5000 && SpdReader.DecodeDdr5(0x50, CorruptDdr5(spdCopy => (spdCopy[20], spdCopy[21]) = (0x43, 0x01)))?.NominalSpeed == 6200,
    "DDR5: скорость округляется до шага 200 MT/s");

// Сопоставление с SMBIOS, как на ноутбуке с распаянной памятью: у первого модуля микросхемы SPD нет,
// паспорт по адресу 0x50 принадлежит второму модулю — порядок адресов и слотов не совпадает.
ModuleMemoryModel CreateModule(string serial, string part) => new() { Size_GB = 8, RAM_serialNumber = serial, RAM_partNumber = part };
var soldered = CreateModule("00000000", "M471A1G44BB0-CWE");
var socket = CreateModule("32FD2187", "M471A1K43EB1-CWE");
var found0x50 = info! with { SerialNumber = "32FD2187", PartNumber = "M471A1K43EB1-CWE" };
var pairs = SpdMatcher.Match([soldered, socket], [found0x50], out string matchMethod);
Check(pairs is [(var pairedModule, var pairedInfo)] && pairedModule == socket && pairedInfo == found0x50 && matchMethod.Contains("серийный"), "паспорт отнесён к модулю с тем же серийным номером и моделью, а не к первому слоту");
pairs = SpdMatcher.Match([CreateModule("0", "A"), CreateModule("0", "A")], [found0x50 with { SerialNumber = "0", PartNumber = "A" }, found0x50 with { SerialNumber = "0", PartNumber = "A" }], out matchMethod);
Check(pairs.Count == 2 && matchMethod.Contains("предположительно"), "одинаковые серийные номера не дают пары по номеру, остаётся предположительный порядок слотов");

// --- Отчёт ---
var report = new DiagnosticResult { ErrorCount = 1, Errors = [new(64, 1, 3, "Проверка вперёд", 1)], Status = DiagnosticStatus.Errors };
string html = ReportExporter.CreateHtml(report);
Check(html.Split("<rect").Length - 1 == 256 && html.Contains("0x0000000040"), "HTML-отчёт: 256 клеток карты и строка ошибки");
var partial = new DiagnosticResult { RequestedBytes = 512L << 20, Status = DiagnosticStatus.Stopped };
partial.Map.AsSpan(0, 64).Fill(Ok); // до остановки первый проход завершили 64 блока из 256
html = ReportExporter.CreateHtml(partial);
Check(html.Contains("<td>Проверено полным проходом</td><td>128 МБ</td>"), "отчёт об остановленной проверке показывает проверенный объём, а не запрошенный");
partial.Map.AsSpan().Fill(Ok);
partial.Map[0] = Error; // красный блок мог пройти проход целиком, а мог быть остановлен на ошибке
Check(partial.CheckedText == "не менее 510 МБ", $"с ошибками в первом проходе объём указан как нижняя граница: {partial.CheckedText}");

var series = new DiagnosticResult();
for (int i = 0; i < 12000; i++)
    series.RecordLoad(i * 0.25, new MemorySnapshot(100, 40, 20, 80, null));
Check(series.Samples is { Count: <= 120 } && series.Samples.First().Seconds == 0 && series.Samples.Last().Seconds == 2999.75
    && series.Samples.Last().CommitPercent == 25, "график сохраняет начало и конец длительной проверки");
Check(ReportExporter.CreateHtml(series).Contains("data:image/png;base64,") && !ReportExporter.CreateHtml(report).Contains("data:image/png;base64,"),
    "график использует только сохранённые измерения");
var exportDirectory = Directory.CreateTempSubdirectory("MemScopeReport");
try
{
    foreach (string extension in new[] { "md", "pdf", "html" })
    {
        var path = Path.Combine(exportDirectory.FullName, "test." + extension);
        ReportExporter.Save(series, path);
        Check(new FileInfo(path).Length > 100, $"экспорт {extension} с графиками или исходными данными");
    }
    var intervals = new DiagnosticResult { ElapsedSeconds = 4.5, Samples = [new(0, 10, null), new(0.25, 30, 50), new(3.1, 40, null)] };
    var markdownPath = Path.Combine(exportDirectory.FullName, "intervals.md");
    ReportExporter.Save(intervals, markdownPath);
    var markdown = File.ReadAllText(markdownPath);
    Check(markdown.Contains(string.Join("\n", "| 0 | 30 | 50 |", "| 1 | 30 | 50 |", "| 2 | 30 | 50 |",
        "| 3 | 40 | — |", "| 4 | 40 | — |")), "Markdown показывает по одной строке на секунду до конца проверки");
}
finally { exportDirectory.Delete(true); }

Console.WriteLine(failedCount == 0 ? "\nВсе проверки пройдены" : $"\nНе пройдено: {failedCount}");
return failedCount;

// Передаёт отчёт сразу в том же потоке, без очереди интерфейса: так видно последнее состояние сразу после проверки.
class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
