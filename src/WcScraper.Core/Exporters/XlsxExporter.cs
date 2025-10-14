using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;

namespace WcScraper.Core.Exporters;

public static class XlsxExporter
{
    public static void Write(string path, IEnumerable<IDictionary<string, object?>> rows)
    {
        var list = rows.ToList();

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Sheet1");

        if (!list.Any())
        {
            wb.SaveAs(path);
            return;
        }

        var headers = list.First().Keys.ToList();
        for (int c = 0; c < headers.Count; c++)
            ws.Cell(1, c + 1).SetValue(headers[c]);

        int r = 2;
        foreach (var row in list)
        {
            for (int c = 0; c < headers.Count; c++)
            {
                var value = row.TryGetValue(headers[c], out var val) ? val : null;
                SetCellValue(ws.Cell(r, c + 1), value);
            }
            r++;
        }

        ws.Columns().AdjustToContents();
        wb.SaveAs(path);
    }

    private static void SetCellValue(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
                cell.SetValue(string.Empty);
                break;
            case string s:
                cell.SetValue(Truncate(s));
                break;
            case DateOnly dateOnly:
                cell.SetValue(dateOnly.ToDateTime(TimeOnly.MinValue));
                break;
            case TimeOnly timeOnly:
                cell.SetValue(timeOnly.ToTimeSpan());
                break;
            case bool b:
                cell.SetValue(b);
                break;
            case sbyte sb:
                cell.SetValue(sb);
                break;
            case byte by:
                cell.SetValue(by);
                break;
            case short sh:
                cell.SetValue(sh);
                break;
            case ushort ush:
                cell.SetValue(ush);
                break;
            case int i:
                cell.SetValue(i);
                break;
            case uint ui:
                cell.SetValue(ui);
                break;
            case long l:
                cell.SetValue(l);
                break;
            case ulong ul:
                cell.SetValue(ul);
                break;
            case float f:
                cell.SetValue(f);
                break;
            case double d:
                cell.SetValue(d);
                break;
            case decimal dec:
                cell.SetValue(dec);
                break;
            case DateTime dateTime:
                cell.SetValue(dateTime);
                break;
            case IEnumerable enumerable and not string:
                cell.SetValue(Truncate(JoinEnumerable(enumerable)));
                break;
            default:
                cell.SetValue(Truncate(value.ToString() ?? string.Empty));
                break;
        }
    }

    private static string Truncate(string text)
    {
        const int maxLength = 32767;
        if (text.Length <= maxLength)
            return text;

        const string ellipsis = "…";
        var truncated = text[..Math.Max(0, maxLength - ellipsis.Length)] + ellipsis;
        return truncated;
    }

    private static string JoinEnumerable(IEnumerable enumerable)
    {
        var parts = new List<string>();
        foreach (var item in enumerable)
        {
            if (item is null)
                continue;

            parts.Add(item.ToString() ?? string.Empty);
        }

        return string.Join(", ", parts);
    }
}
