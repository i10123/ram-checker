using System.Net;
using RAMChecker.Models;

namespace RAMChecker.Services;

// Формирует HTML-отчёт о проверке с картой памяти.
public static class ReportExporter
{
    public static string Html(DiagnosticResult r)
    {
        static string E(string value) => WebUtility.HtmlEncode(value);
        // Карта буфера: 32 × 8 клеток, цвет показывает состояние блока (у идущей проверки «ждёт прохода» — тоже зелёный).
        var cells = string.Concat(r.Map.Select((state, i) =>
            $"<rect x='{i % 32 * 22}' y='{i / 32 * 22}' width='20' height='20' rx='3' fill='{(state == 2 ? "#d13438" : state is 1 or 3 ? "#0f7b0f" : "#c8ced6")}'/>"));
        var rows = string.Concat(new[] { ("Дата", r.StartedText), ("Режим", r.Mode), ("Алгоритм", MemoryDiagnosticService.Algorithm),
            ("Длительность", r.DurationText), ("Объём проверки", r.MemoryText), ("Проверено полным проходом", r.CheckedText),
            ("Проходов", r.Passes.ToString()), ("Ошибок", r.ErrorCount.ToString()) }
            .Select(row => $"<tr><td>{row.Item1}</td><td>{E(row.Item2)}</td></tr>"));
        var errors = string.Concat(r.Errors.Select(e =>
            $"<tr><td>{e.OffsetText}</td><td>{e.ExpectedText}</td><td>{e.ActualText}</td><td>{E(e.Stage)}</td><td>{e.Pass}</td></tr>"));
        return $$"""
            <!doctype html><html lang="ru"><meta charset="utf-8"><title>Отчёт MemScope</title>
            <style>body{font:15px 'Segoe UI',sans-serif;color:#1b1b1b;max-width:900px;margin:40px auto;padding:0 24px}
            h1{color:#005fb8}table{border-collapse:collapse;width:100%;margin:16px 0}td,th{text-align:left;padding:8px;border-bottom:1px solid #e5e5e5}
            pre{background:#f5f5f5;padding:12px;white-space:pre-wrap}.legend span{margin-right:16px}</style>
            <h1>MemScope</h1><h2>{{E(r.StatusText)}}</h2><table>{{rows}}</table>
            <p>{{E(r.Detail)}}</p><h2>Рекомендации</h2><p>{{E(r.Recommendation)}}</p>
            <h2>Карта буфера проверки</h2><svg width="704" height="176">{{cells}}</svg>
            <p class="legend"><span style="color:#0f7b0f">■ без ошибок</span><span style="color:#d13438">■ есть ошибки</span><span style="color:#8a8f96">■ не проверено</span></p>
            <p>Карта и смещения относятся к буферу программы, а не к физическим адресам и слотам: по ним нельзя определить
            неисправную планку. Проверяется только выделенный буфер, поэтому отсутствие ошибок не доказывает исправность всей памяти.</p>
            <h2>Конфигурация</h2><pre>{{E(r.Configuration)}}</pre>
            <h2>Ошибки (первые 1000)</h2><table><tr><th>Смещение в буфере</th><th>Ожидалось</th><th>Прочитано</th><th>Шаг</th><th>Проход</th></tr>{{errors}}</table></html>
            """;
    }
}
