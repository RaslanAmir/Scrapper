using System.Text.Json;

namespace WcScraper.Core.Exporters;

public static class JsonlExporter
{
    public static void Write(string path, IEnumerable<IDictionary<string, object?>> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var sw = new StreamWriter(fs, System.Text.Encoding.UTF8);
        var opts = new JsonSerializerOptions { WriteIndented = false };

        foreach (var row in rows)
        {
            sw.WriteLine(JsonSerializer.Serialize(row, opts));
        }
    }
}
