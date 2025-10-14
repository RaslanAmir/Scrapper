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
                var v = row.TryGetValue(headers[c], out var val) ? val : null;
                ws.Cell(r, c + 1).Value = v is null ? XLCellValue.Empty : XLCellValue.FromObject(v);
            }
            r++;
        }

        ws.Columns().AdjustToContents();
        wb.SaveAs(path);
    }
}
