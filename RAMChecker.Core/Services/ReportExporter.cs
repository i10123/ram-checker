using System.Net;
using RAMChecker.Models;

namespace RAMChecker.Services;

// Формирует HTML-отчёт о проверке с картой памяти.
public static class ReportExporter
{
    public static string CreateHtml(DiagnosticResult result)
    {
        static string Encode(string text) => WebUtility.HtmlEncode(text);
        // Карта буфера: 32 × 8 клеток, цвет показывает состояние блока (у идущей проверки «ждёт прохода» — тоже зелёный).
        var mapCells = string.Concat(result.Map.Select((state, cellIndex) =>
            $"<rect x='{cellIndex % 32 * 22}' y='{cellIndex / 32 * 22}' width='20' height='20' rx='3' fill='{(state == 2 ? "#d13438" : state is 1 or 3 ? "#0f7b0f" : "#c8ced6")}'/>"));
        var tableRows = string.Concat(new[] { ("Дата", result.StartedText), ("Режим", result.Mode), ("Алгоритм", MemoryDiagnosticService.Algorithm),
            ("Длительность", result.DurationText), ("Объём проверки", result.MemoryText), ("Проверено полным проходом", result.CheckedText),
            ("Проходов", result.Passes.ToString()), ("Ошибок", result.ErrorCount.ToString()) }
            .Select(row => $"<tr><td>{row.Item1}</td><td>{Encode(row.Item2)}</td></tr>"));
        var errorRows = string.Concat(result.Errors.Select(error =>
            $"<tr><td>{error.OffsetText}</td><td>{error.ExpectedText}</td><td>{error.ActualText}</td><td>{Encode(error.Stage)}</td><td>{error.Pass}</td></tr>"));
        return $$"""
            <!doctype html><html lang="ru"><meta charset="utf-8"><title>Отчёт MemScope</title>
            <style>body{font:15px 'Segoe UI',sans-serif;color:#1b1b1b;max-width:900px;margin:40px auto;padding:0 24px}
            h1{color:#005fb8}table{border-collapse:collapse;width:100%;margin:16px 0}td,th{text-align:left;padding:8px;border-bottom:1px solid #e5e5e5}
            pre{background:#f5f5f5;padding:12px;white-space:pre-wrap}.legend span{margin-right:16px}</style>
            <h1>MemScope</h1><h2>{{Encode(result.StatusText)}}</h2><table>{{tableRows}}</table>
            <p>{{Encode(result.Detail)}}</p><h2>Рекомендации</h2><p>{{Encode(result.Recommendation)}}</p>
            <h2>Карта буфера проверки</h2><svg width="704" height="176">{{mapCells}}</svg>
            <p class="legend"><span style="color:#0f7b0f">■ без ошибок</span><span style="color:#d13438">■ есть ошибки</span><span style="color:#8a8f96">■ не проверено</span></p>
            <p>Карта и смещения относятся к буферу программы, а не к физическим адресам и слотам: по ним нельзя определить
            неисправную планку. Проверяется только выделенный буфер, поэтому отсутствие ошибок не доказывает исправность всей памяти.</p>
            <h2>Конфигурация</h2><pre>{{Encode(result.Configuration)}}</pre>
            {{(errorRows.Length == 0 ? "" : $"<h2>Ошибки (первые 1000)</h2><table><tr><th>Смещение в буфере</th><th>Ожидалось</th><th>Прочитано</th><th>Шаг</th><th>Проход</th></tr>{errorRows}</table>")}}</html>
            """;
    }
}
