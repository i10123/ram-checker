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
        foreach (var s in reader.ReadAll())
            log.AppendLine($"{s} temp={reader.Temperature(s.Address)}");
        log.AppendLine("done");
    }
    catch (Exception ex) { log.AppendLine(ex.ToString()); }
    File.WriteAllText(outFile, log.ToString());
    return 0;
}

int failed = 0;
void Check(bool ok, string name)
{
    Console.WriteLine((ok ? "OK    " : "FAIL  ") + name);
    if (!ok) failed++;
}

// --- Алгоритм ---
var block = new uint[100_000];
var errors = new List<(long, uint, uint)>();
void OnError(long a, uint e, uint r) => errors.Add((a, e, r));

FillBlock(block, 1000, 1);
Check(CheckBlock(block, 1000, 1, false, OnError) == 0 && CheckBlock(block, 1000, 1, true, OnError) == 0, "чистый блок: 0 ошибок за оба обхода");
Check(block.Select((v, i) => v == Pattern(1000 + i, 1)).All(x => x), "после прохода в памяти снова исходные значения");
Check(new[] { 1, 3, 4 }.All(p => Enumerable.Range(0, 99_999).All(i => Pattern(i, p) != Pattern(i + 1, p))), "соседние адреса получают разные значения");
Check(Pattern(5, 1) != Pattern(5, 2) && Pattern(5, 4) != Pattern(5, 8), "разные проходы дают разные значения");
Check(Enumerable.Range(0, 1000).All(i => Pattern(i, 2) == ~Pattern(i + 1, 2) && Pattern(i, 6) == ~Pattern(i, 2)), "шахматный шаблон: у соседей противоположные биты, рисунок меняется через четыре прохода");
Check(Enumerable.Range(0, 1000).All(i => uint.PopCount(Pattern(i, 3)) == 1 && Pattern(i + 1, 3) == uint.RotateLeft(Pattern(i, 3), 1)), "бегущая единица: один бит, у соседа сдвинут на разряд");
Check(Enumerable.Range(0, 100_000).Average(i => uint.PopCount(Pattern(i, 4))) is > 15.5 and < 16.5, "случайный шаблон: в среднем половина битов единичные");
Check(PatternName(1) == "Свой адрес" && PatternName(3) == "Бегущая единица" && PatternName(8) == "Случайный шаблон", "шаблоны сменяются по кругу");

FillBlock(block, 0, 2);
block[500] ^= 1u << 7;
errors.Clear();
Check(CheckBlock(block, 0, 2, false, OnError) == 1 && errors[0].Item1 == 500 && (errors[0].Item2 ^ errors[0].Item3) == 1u << 7, "перевёрнутый бит найден прямым обходом");

FillBlock(block, 0, 3);
block[700] = block[100]; // ошибка адресации: ячейка 700 отвечает данными ячейки 100
errors.Clear();
Check(CheckBlock(block, 0, 3, false, OnError) == 1 && errors[0].Item1 == 700, "ошибка адресации найдена");

FillBlock(block, 0, 4);
CheckBlock(block, 0, 4, false, OnError);
block[42] |= 1u << 31; block[42] &= ~1u; // «залипшие» биты после записи инверсии
errors.Clear();
int back = CheckBlock(block, 0, 4, true, OnError);
Check(back == 1 && errors[0].Item1 == 42, "залипший бит найден обратным обходом");

// --- Отмена и несколько ошибок ---
var bad = new uint[1 << 20];
FillBlock(bad, 0, 1);
for (int i = 0; i < bad.Length; i++)
    bad[i] ^= 1; // испорчена каждая ячейка
int found = 0;
using (var stop = new CancellationTokenSource())
    try { CheckBlock(bad, 0, 1, false, (_, _, _) => { found++; stop.Cancel(); }, stop.Token); }
    catch (OperationCanceledException) { }
Check(found == 1, "при остановке на ошибке блок прерывается сразу после первой ошибки");
found = 0;
using (var stop = new CancellationTokenSource())
    try
    {
        Parallel.For(0, 64, new ParallelOptions { CancellationToken = stop.Token }, k => CheckBlock(bad.AsSpan(k << 14, 1 << 14), k << 14, 1, false,
            (_, _, _) => { Interlocked.Increment(ref found); stop.Cancel(); }, stop.Token));
    }
    catch (OperationCanceledException) { }
Check(found >= 1 && found <= Environment.ProcessorCount, $"64 испорченных блока параллельно: {found} ошибок, не больше одной на поток");

// --- Полный запуск ---
var progress = new Progress<DiagnosticProgress>(_ => { });
var r = await RunAsync(64L << 20, TimeSpan.FromSeconds(1), false, progress, CancellationToken.None);
Check(r.Status == DiagnosticStatus.Completed && r.ErrorCount == 0 && r.Passes >= 1, $"64 МБ за 1 с: {r.StatusText}, проходов {r.Passes}");
Check(r.Map.All(c => c == Ok) && r.AllocatedBytes == 64L << 20 && r.CheckedBytes == 64L << 20, "карта памяти полностью зелёная, выделен и проверен весь объём");
long before = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
await RunAsync(1L << 30, TimeSpan.FromSeconds(1), false, progress, CancellationToken.None);
long after = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
Check(after - before < 64L << 20, $"после проверки 1 ГБ память возвращена системе (прирост {(after - before) >> 20} МБ)");
var im = await RunAsync(64L << 20, TimeSpan.FromSeconds(1), false, progress, CancellationToken.None, imitation: true);
Check(im.Status == DiagnosticStatus.Completed && im.ErrorCount == im.Passes && im.Map.Count(c => c == Error) == 1 && im.Map[MapSize / 3] == Error
    && (im.Errors[0].Expected ^ im.Errors[0].Actual) == 1u << 5, $"имитация: одна ошибка за проход в блоке {MapSize / 3 + 1}, ошибок {im.ErrorCount}");
var im2 = await RunAsync(64L << 20, null, true, progress, CancellationToken.None, imitation: true);
Check(im2.Status == DiagnosticStatus.Errors && im2.ErrorCount == 1, "имитация с остановкой на первой ошибке");
using (var cts = new CancellationTokenSource(300))
{
    var s = await RunAsync(1L << 30, null, false, progress, cts.Token);
    Check(s.Status == DiagnosticStatus.Stopped && s.ElapsedSeconds < 1, $"циклическая проверка 1 ГБ останавливается за {(s.ElapsedSeconds - 0.3) * 1000:0} мс после отмены");
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
var restored = storage.ReadHistory()[0];
Check(restored.Status == DiagnosticStatus.Interrupted && restored.ErrorCount > 0 && restored.Errors.Count == restored.ErrorCount
    && restored.Map[MapSize / 3] == Error && !restored.Map.Contains(Waiting), $"после аварии счётчик, список ошибок и карта согласованы: {restored.ErrorCount} = {restored.Errors.Count}");
// Авария сразу после первого отчёта с ошибкой: проход на 64 МБ короче 200 мс, поэтому первая ошибка приходит
// в отчёте конца прохода, раньше очередного периодического снимка карты.
var early = new DiagnosticResult { RequestedBytes = 64L << 20 };
await RunAsync(64L << 20, TimeSpan.FromSeconds(0.5), false, new SyncProgress<DiagnosticProgress>(p =>
{
    if (early.ErrorCount == 0)
        early.ApplyProgress(p);
}), CancellationToken.None, imitation: true);
Check(early.ErrorCount == 1 && early.Errors.Count == 1 && early.Map[MapSize / 3] == Error, $"первая ошибка сохраняется вместе с красной клеткой карты (красных {early.Map.Count(c => c == Error)})");
string historyPath = Path.Combine(storage.DirectoryPath, "history.json");
File.WriteAllText(historyPath, """[{"Status": "Stopped", "ErrorCount": 0}, {"Status": "Running", "Passes": 1}]""");
var old = storage.ReadHistory();
storage.Write("history.json", old);
Check(old[0].Status == DiagnosticStatus.Stopped && old[1].Status == DiagnosticStatus.Interrupted && File.ReadAllText(historyPath).Contains("\"Status\": \"Stopped\""),
    "история прежнего формата читается, статус по-прежнему хранится строкой");

// --- SPD ---
Check(SpdReader.Crc16(Encoding.ASCII.GetBytes("123456789"), 9) == 0x31C3, "CRC16 совпадает с эталоном CRC-16/XMODEM");
var spd = new byte[128];
spd[2] = 0x0C; spd[14] = 0x80; spd[18] = 6;                 // DDR4, датчик есть, tCK = 0,75 нс
spd[4] = 0x05; spd[12] = 0x01; spd[13] = 0x03;              // кристаллы 8 Гбит x8, один ранг, шина 64 бита → 8 ГБ
spd[20] = 0b1111_1000; spd[21] = 0xFF; spd[22] = 0x03;      // CL10–CL24
spd[24] = 96; spd[25] = 108; spd[26] = 108;                 // tAA 12 нс, tRCD/tRP 13,5 нс
spd[27] = 0x10; spd[28] = 210; spd[29] = 62;                // tRAS 26,25 нс, tRC 39,75 нс
int crc = SpdReader.Crc16(spd, 126);
spd[126] = (byte)crc; spd[127] = (byte)(crc >> 8);
var info = SpdReader.Decode(0x50, spd);
Check(info is { NominalSpeed: 2666, CL: 16, RCD: 18, RP: 18, RAS: 35, RC: 53, SizeGB: 8, HasSensor: true }, $"DDR4-2666 16-18-18-35, 8 ГБ: {info}");
spd[24] = 95; spd[123] = unchecked((byte)-125);             // 11,875 − 0,125 = 11,75 нс → 15,67 такта → CL16
Check(SpdReader.Decode(0x50, spd) is null, "испорченная контрольная сумма отклоняется");
crc = SpdReader.Crc16(spd, 126); spd[126] = (byte)crc; spd[127] = (byte)(crc >> 8);
Check(SpdReader.Decode(0x50, spd)?.CL == 16, "поправка в пикосекундах учитывается");
// Испорченные поля с верной контрольной суммой: CRC защищает только от сбоев чтения.
byte[] Spd(Action<byte[]> change)
{
    var b = (byte[])spd.Clone();
    change(b);
    int c = SpdReader.Crc16(b, 126);
    (b[126], b[127]) = ((byte)c, (byte)(c >> 8));
    return b;
}
Check(SpdReader.Decode(0x50, Spd(b => (b[18], b[125]) = (0, 0))) is null && SpdReader.Decode(0x50, Spd(b => b[18] = 1)) is null, "нулевой и слишком короткий tCK отклоняются");
Check(SpdReader.Decode(0x50, Spd(b => b[20] = b[21] = b[22] = 0)) is null, "пустой список поддерживаемых CL отклоняется");
// Поддерживается только CL7, а tAA = 30 нс требует CL40 — за пределами списка.
Check(SpdReader.Decode(0x50, Spd(b => (b[20], b[21], b[22], b[24], b[123]) = (1, 0, 0, 240, 0))) is null, "расчётный CL выше списка поддерживаемых отклоняется");
Check(SpdReader.Decode(0x50, spd[..100]) is null, "слишком короткий паспорт отклоняется");

// --- Отчёт ---
var report = new DiagnosticResult { ErrorCount = 1, Errors = [new(64, 1, 3, "Проверка вперёд", 1)], Status = DiagnosticStatus.Errors };
string html = ReportExporter.Html(report);
Check(html.Split("<rect").Length - 1 == 256 && html.Contains("0x0000000040"), "HTML-отчёт: 256 клеток карты и строка ошибки");
var partial = new DiagnosticResult { RequestedBytes = 512L << 20, Status = DiagnosticStatus.Stopped };
partial.Map.AsSpan(0, 64).Fill(Ok); // до остановки первый проход завершили 64 блока из 256
html = ReportExporter.Html(partial);
Check(html.Contains("<td>Проверено полным проходом</td><td>128 МБ</td>"), "отчёт об остановленной проверке показывает проверенный объём, а не запрошенный");
partial.Map.AsSpan().Fill(Ok);
partial.Map[0] = Error; // красный блок мог пройти проход целиком, а мог быть остановлен на ошибке
Check(partial.CheckedText == "не менее 510 МБ", $"с ошибками в первом проходе объём указан как нижняя граница: {partial.CheckedText}");

Console.WriteLine(failed == 0 ? "\nВсе проверки пройдены" : $"\nНе пройдено: {failed}");
return failed;

// Передаёт отчёт сразу в том же потоке, без очереди интерфейса: так видно последнее состояние сразу после проверки.
class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
