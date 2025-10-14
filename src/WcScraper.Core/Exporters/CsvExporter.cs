using System.Globalization;
using System.Text;

namespace WcScraper.Core.Exporters;

public static class CsvExporter
{
    public static void Write(string path, IEnumerable<IDictionary<string, object?>> rows)
    {
        var list = rows.ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        if (!list.Any())
        {
            sw.WriteLine("");
            return;
        }

        // Headers
        var headers = list.First().Keys.ToList();
        sw.WriteLine(string.Join(",", headers.Select(Quote)));

        foreach (var row in list)
        {
            var line = string.Join(",", headers.Select(h => Quote(Format(row.TryGetValue(h, out var v) ? v : null))));
            sw.WriteLine(line);
        }
    }

    private static string Format(object? v)
    {
        if (v is null) return "";
        return v switch
        {
            DateTime dt => dt.ToString("s", CultureInfo.InvariantCulture),
            double d => d.ToString("0.######", CultureInfo.InvariantCulture),
            float f => f.ToString("0.######", CultureInfo.InvariantCulture),
            decimal m => m.ToString("0.######", CultureInfo.InvariantCulture),
            bool b => b ? "TRUE" : "FALSE",
            _ => v.ToString() ?? ""
        };
    }

    private static string Quote(string s)
    {
        var needs = s.Contains(',') || s.Contains('"') || s.Contains('\') || s.Contains('
') || s.Contains('');
        if (!needs) return s;
        return $""{s.Replace(""", """")}"";
    }
}
