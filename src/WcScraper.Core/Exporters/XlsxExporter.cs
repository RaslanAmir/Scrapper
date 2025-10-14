using System.Collections;
using ClosedXML.Excel;

namespace WcScraper.Core.Exporters;

public static class XlsxExporter
{
    public static void Write(string path, IEnumerable<IDictionary<string, object?>> rows)
    {
        var list = rows.ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Sheet1");

        if (!list.Any())
        {
            wb.SaveAs(path);
            return;
        }

        var headers = list.First().Keys.ToList();
        for (int c = 0; c < headers.Count; c++)
            ws.Cell(1, c + 1).Value = headers[c];

        int r = 2;
        foreach (var row in list)
        {
            for (int c = 0; c < headers.Count; c++)
            {
                var value = row.TryGetValue(headers[c], out var val) ? val : null;
                ws.Cell(r, c + 1).Value = Normalize(value);
            }
            r++;
        }

        ws.Columns().AdjustToContents();
        wb.SaveAs(path);
    }

    private static object Normalize(object? value)
    {
        if (value is null)
            return string.Empty;

        if (value is string s)
            return Truncate(s);

        if (value is DateTime || value is bool || value is sbyte || value is byte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong || value is float || value is double || value is decimal)
            return value;

        if (value is DateOnly dateOnly)
            return dateOnly.ToDateTime(TimeOnly.MinValue);

        if (value is TimeOnly timeOnly)
            return timeOnly.ToTimeSpan();

        if (value is IEnumerable enumerable and not string)
        {
            var parts = new List<string>();
            foreach (var item in enumerable)
            {
                if (item is null) continue;
                parts.Add(item.ToString() ?? string.Empty);
            }
            return Truncate(string.Join(", ", parts));
        }

        return Truncate(value.ToString() ?? string.Empty);
    }

    private static string Truncate(string text)
    {
        const int maxLength = XLHelper.MaxTextLength;
        if (text.Length <= maxLength)
            return text;

        const string ellipsis = "…";
        var truncated = text[..Math.Max(0, maxLength - ellipsis.Length)] + ellipsis;
        return truncated;
    }
}
