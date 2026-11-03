using System.Net;
using System.Text;
using RAMChecker.Models;

namespace RAMChecker.Services;

public static class ReportExporter
{
    public static string Html(DiagnosticResult r)
    {
        static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
        var errors = string.Join("", r.Errors.Select(e => $"<tr><td>{e.OffsetText}</td><td>{e.ExpectedText}</td><td>{e.ActualText}</td><td>{E(e.Stage)}</td><td>{e.Pass}</td></tr>"));
        return $$"""
            <!doctype html><html lang="ru"><meta charset="utf-8"><title>RAM Diagnostic Suite — отчёт</title>
            <style>body{font:16px 'Segoe UI',sans-serif;color:#172b3a;max-width:960px;margin:48px auto;padding:24px}h1{color:#147d73}table{border-collapse:collapse;width:100%;margin:24px 0}td,th{text-align:left;padding:12px;border-bottom:1px solid #dee5eb}aside{padding:20px;background:#f4f6f8}pre{white-space:pre-wrap}</style>
            <h1>RAM Diagnostic Suite</h1><h2>{{E(r.StatusText)}}</h2>
            <table><tr><td>Дата</td><td>{{E(r.StartedText)}}</td></tr><tr><td>Режим</td><td>{{E(r.Mode)}}</td></tr>
            <tr><td>Продолжительность</td><td>{{E(r.DurationText)}}</td></tr><tr><td>Выделено</td><td>{{E(r.MemoryText)}}</td></tr>
            <tr><td>Полных проходов</td><td>{{r.Passes}}</td></tr><tr><td>Ошибок</td><td>{{r.ErrorCount}}</td></tr></table>
            <p>{{E(r.Detail)}}</p><aside>Результат относится к буферу процесса и выполненным тестам. Windows может выгружать страницы. Это не проверка всей физической RAM и не определение неисправной планки.</aside>
            <h2>Рекомендации</h2><p>{{E(r.Recommendation)}}</p><h2>Методы проверки</h2><p>{{E(r.Algorithms)}}</p>
            <h2>Конфигурация при запуске</h2><pre>{{E(r.Configuration)}}</pre>
            <h2>Ошибки (первые 1000)</h2><table><tr><th>Смещение в буфере</th><th>Ожидалось</th><th>Прочитано</th><th>Этап</th><th>Проход</th></tr>{{errors}}</table>
            <h2>Журнал</h2><pre>{{E(string.Join(Environment.NewLine, r.Events))}}</pre></html>
            """;
    }

    public static string Csv(DiagnosticResult r)
    {
        static string Q(string s) => "\"" + ((s.Length > 0 && "=+-@".Contains(s[0])) ? "'" : "") + s.Replace("\"", "\"\"") + "\"";
        var b = new StringBuilder("Тип;Дата;Статус;Режим;Выделено байт;Секунд;Ошибок;Смещение;Ожидалось;Прочитано;Этап;Проход\r\n");
        b.AppendLine(string.Join(';', new[] { "Итог", r.Started.ToString("O"), r.StatusText, r.Mode,
            r.AllocatedBytes.ToString(), r.ElapsedSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture), r.ErrorCount.ToString(), "", "", "", "", "" }.Select(Q)));
        foreach (var e in r.Errors)
            b.AppendLine(string.Join(';', new[] { "Ошибка", "", "", "", "", "", "", e.OffsetText, e.ExpectedText, e.ActualText, e.Stage, e.Pass.ToString() }.Select(Q)));
        foreach (var item in new[] { ("Рекомендации", r.Recommendation), ("Конфигурация", r.Configuration), ("Методы", r.Algorithms) })
            b.AppendLine(string.Join(';', new[] { item.Item1, "", item.Item2, "", "", "", "", "", "", "", "", "" }.Select(Q)));
        return b.ToString();
    }
}
