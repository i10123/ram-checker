using System.IO;
using System.Globalization;
using MigraDoc.DocumentObjectModel.Shapes.Charts;
using System.Net;
using System.Text;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using RAMChecker.Models;

namespace RAMChecker.Services;

// Формирует отчёт о проверке в одном из форматов: HTML (с картой памяти), PDF, CSV или Markdown.
public static class ReportExporter
{
    // Формат выбирается по расширению имени файла; неизвестное расширение даёт HTML.
    public static void Save(DiagnosticResult result, string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".pdf")
            SavePdf(result, path);
        else
            File.WriteAllText(path, extension switch { ".csv" => CreateCsv(result), ".md" => CreateMarkdown(result), _ => CreateHtml(result) }, new UTF8Encoding(true));
    }

    // Общая таблица «параметр — значение» для всех форматов.
    private static (string, string)[] Summary(DiagnosticResult result) => [("Дата", result.StartedText), ("Режим", result.Mode), ("Алгоритм", MemoryDiagnosticService.Algorithm),
        ("Длительность", result.DurationText), ("Объём проверки", result.MemoryText), ("Проверено полным проходом", result.CheckedText),
        ("Проходов", result.Passes.ToString()), ("Ошибок", result.ErrorCount.ToString())];

    // Разделитель — точка с запятой: так CSV открывается в русской версии Excel.
    private static string CreateCsv(DiagnosticResult result)
    {
        static string Quote(string text) => '"' + text.Replace("\"", "\"\"") + '"';
        return string.Join("\r\n", Summary(result).Prepend(("Результат", result.StatusText)).Select(row => $"{Quote(row.Item1)};{Quote(row.Item2)}")
            .Append("").Append("Смещение в буфере;Ожидалось;Прочитано;Шаг;Проход")
            .Concat(result.Errors.Select(error => $"{error.OffsetText};{error.ExpectedText};{error.ActualText};{Quote(error.Stage)};{error.Pass}"))
            .Append("").Append("Время (с);Физическая память (%);Выделенная память (%);MCLK (MHz)")
            .Concat(result.Samples.Select(s => FormattableString.Invariant($"{s.Seconds:F2};{s.PhysicalPercent:F2};{s.CommitPercent:F2};{s.ClockMHz:F2}"))));
    }

    private static string CreateMarkdown(DiagnosticResult result)
    {
        List<string> lines = [$"# MemScope: {result.StatusText}", "", "| Параметр | Значение |", "|---|---|"];
        lines.AddRange(Summary(result).Select(row => $"| {row.Item1} | {row.Item2} |"));
        lines.AddRange(["", result.Detail, "", "## Рекомендации", "", result.Recommendation, "", "## Конфигурация", "", "```", result.Configuration, "```"]);
        if (result.Errors.Count > 0)
        {
            lines.AddRange(["", "## Ошибки (первые 1000)", "", "| Смещение в буфере | Ожидалось | Прочитано | Шаг | Проход |", "|---|---|---|---|---|"]);
            lines.AddRange(result.Errors.Select(error => $"| {error.OffsetText} | {error.ExpectedText} | {error.ActualText} | {error.Stage} | {error.Pass} |"));
        }
        if (result.Samples.Count > 0)
        {
            lines.AddRange(["", "## Нагрузка памяти", "", "| Время, с | Физическая память, % | Выделенная память, % | MCLK, MHz |", "|---:|---:|---:|---:|"]);
            lines.AddRange(result.Samples.Select(s => $"| {s.Seconds:0.##} | {s.PhysicalPercent:0.#} | {s.CommitPercent:0.#} | {s.ClockMHz:0.##} |"));
        }
        return string.Join("\n", lines);
    }

    // PDF собирает библиотека MigraDoc: абзацы и таблицы раскладываются по страницам сами.
    private static void SavePdf(DiagnosticResult result, string path)
    {
        var document = new Document();
        document.Styles[StyleNames.Normal]!.Font.Name = "Segoe UI";
        var section = document.AddSection();
        void Heading(string text, int size)
        {
            var paragraph = section.AddParagraph(text);
            (paragraph.Format.Font.Size, paragraph.Format.Font.Bold, paragraph.Format.SpaceBefore, paragraph.Format.SpaceAfter) = (size, true, 12, 6);
        }
        void AddRow(MigraDoc.DocumentObjectModel.Tables.Table table, bool bold, params string[] cells)
        {
            var row = table.AddRow();
            for (int column = 0; column < cells.Length; column++)
                row.Cells[column].AddParagraph(cells[column]).Format.Font.Bold = bold;
        }
        Heading("MemScope", 22);
        Heading(result.StatusText, 15);
        var summary = section.AddTable();
        summary.Borders.Bottom.Width = 0.5;
        summary.AddColumn("6cm");
        summary.AddColumn("10cm");
        foreach (var (name, value) in Summary(result))
            AddRow(summary, false, name, value);
        section.AddParagraph(result.Detail).Format.SpaceBefore = 10;
        foreach (var (title, select, scale) in LoadCharts)
        {
            Heading(title, 13);
            if (!result.Samples.Any(s => select(s).HasValue))
            {
                section.AddParagraph("Нет измерений");
                continue;
            }
            var chart = section.AddChart(ChartType.Line);
            chart.Width = "15cm";
            chart.Height = "4cm";
            chart.YAxis.MinimumScale = 0;
            chart.YAxis.MaximumScale = scale ?? Math.Ceiling(result.Samples.Max(select)!.Value / 100) * 100;
            chart.YAxis.MajorTickMark = TickMarkType.Outside;
            var series = chart.SeriesCollection.AddSeries();
            series.MarkerStyle = MarkerStyle.Circle;
            var labels = chart.XValues.AddXSeries();
            foreach (var sample in result.Samples)
            {
                if (select(sample) is double value) series.Add(value);
                else series.AddBlank();
                labels.Add(sample == result.Samples[0] || sample == result.Samples[^1] ? $"{sample.Seconds:0.#} с" : "");
            }
        }
        Heading("Рекомендации", 13);
        section.AddParagraph(result.Recommendation);
        Heading("Конфигурация", 13);
        foreach (var line in result.Configuration.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            section.AddParagraph(line).Format.SpaceAfter = 4;
        if (result.Errors.Count > 0)
        {
            Heading("Ошибки (первые 1000)", 13);
            var errors = section.AddTable();
            errors.Borders.Bottom.Width = 0.5;
            foreach (var width in new[] { "4cm", "3cm", "3cm", "3.5cm", "2.5cm" })
                errors.AddColumn(width);
            AddRow(errors, true, "Смещение в буфере", "Ожидалось", "Прочитано", "Шаг", "Проход");
            errors.Rows[0].HeadingFormat = true;
            foreach (var error in result.Errors)
                AddRow(errors, false, error.OffsetText, error.ExpectedText, error.ActualText, error.Stage, error.Pass.ToString());
        }
        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        renderer.PdfDocument.Save(path);
    }

    private static readonly (string Title, Func<LoadSample,double?> Value, double? Scale)[] LoadCharts =
    [
        ("Физическая память, %", s => s.PhysicalPercent, 100),
        ("Выделенная память, % лимита", s => s.CommitPercent, 100),
        ("Частота MCLK, MHz", s => s.ClockMHz, null)
    ];

    private static string LoadChartHtml(DiagnosticResult result, string title, Func<LoadSample,double?> select, double? scale)
    {
        if (!result.Samples.Any(s => select(s).HasValue)) return $"<h3>{title}</h3><p>Нет измерений</p>";
        var samples = result.Samples;
        double maximum = scale ?? Math.Ceiling(samples.Max(select)!.Value / 100) * 100;
        var start = samples[0].Seconds;
        var duration = Math.Max(0.001, samples[^1].Seconds - start);
        var svg = new StringBuilder();
        string? previous = null;
        foreach (var sample in samples)
        {
            if (select(sample) is not double value) { previous = null; continue; }
            string x = ((sample.Seconds - start) / duration * 590 + 5).ToString("0.##", CultureInfo.InvariantCulture);
            string y = (104 - value * 100 / maximum).ToString("0.##", CultureInfo.InvariantCulture);
            if (previous != null) svg.Append($"<path d='M{previous} L{x} {y}' stroke='#005fb8'/>");
            svg.Append($"<circle cx='{x}' cy='{y}' r='2' fill='#005fb8'/>");
            previous = $"{x} {y}";
        }
        return $"<h3>{title}</h3><svg viewBox='0 0 600 108' style='width:100%;background:#f3f6fa'>{svg}</svg><p>{start:0.#}–{samples[^1].Seconds:0.#} с · 0–{maximum:0}</p>";
    }

    public static string CreateHtml(DiagnosticResult result)
    {
        static string Encode(string text) => WebUtility.HtmlEncode(text);
        // Карта буфера: 32 × 8 клеток, цвет показывает состояние блока (у идущей проверки «ждёт прохода» — тоже зелёный).
        var mapCells = string.Concat(result.Map.Select((state, cellIndex) =>
            $"<rect x='{cellIndex % 32 * 22}' y='{cellIndex / 32 * 22}' width='20' height='20' rx='3' fill='{(state == 2 ? "#d13438" : state is 1 or 3 ? "#0f7b0f" : "#c8ced6")}'/>"));
        var tableRows = string.Concat(Summary(result).Select(row => $"<tr><td>{row.Item1}</td><td>{Encode(row.Item2)}</td></tr>"));
        var errorRows = string.Concat(result.Errors.Select(error =>
            $"<tr><td>{error.OffsetText}</td><td>{error.ExpectedText}</td><td>{error.ActualText}</td><td>{Encode(error.Stage)}</td><td>{error.Pass}</td></tr>"));
        return $$"""
            <!doctype html><html lang="ru"><meta charset="utf-8"><title>Отчёт MemScope</title>
            <style>body{font:15px 'Segoe UI',sans-serif;color:#1b1b1b;max-width:900px;margin:40px auto;padding:0 24px}
            h1{color:#005fb8}table{border-collapse:collapse;width:100%;margin:16px 0}td,th{text-align:left;padding:8px;border-bottom:1px solid #e5e5e5}
            pre{background:#f5f5f5;padding:12px;white-space:pre-wrap}.legend span{margin-right:16px}</style>
            <h1>MemScope</h1><h2>{{Encode(result.StatusText)}}</h2><table>{{tableRows}}</table>
            <p>{{Encode(result.Detail)}}</p><h2>Рекомендации</h2><p>{{Encode(result.Recommendation)}}</p>
            {{string.Concat(LoadCharts.Select(chart => LoadChartHtml(result, chart.Title, chart.Value, chart.Scale)))}}
            <h2>Карта буфера проверки</h2><svg width="704" height="176">{{mapCells}}</svg>
            <p class="legend"><span style="color:#0f7b0f">■ без ошибок</span><span style="color:#d13438">■ есть ошибки</span><span style="color:#8a8f96">■ не проверено</span></p>
            <p>Карта и смещения относятся к буферу программы, а не к физическим адресам и слотам: по ним нельзя определить
            неисправную планку. Проверяется только выделенный буфер, поэтому отсутствие ошибок не доказывает исправность всей памяти.</p>
            <h2>Конфигурация</h2><pre>{{Encode(result.Configuration)}}</pre>
            {{(errorRows.Length == 0 ? "" : $"<h2>Ошибки (первые 1000)</h2><table><tr><th>Смещение в буфере</th><th>Ожидалось</th><th>Прочитано</th><th>Шаг</th><th>Проход</th></tr>{errorRows}</table>")}}</html>
            """;
    }
}
