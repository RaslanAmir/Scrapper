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

    public static void WritePlugins(string path, IEnumerable<InstalledPlugin> plugins)
    {
        var rows = plugins
            .Select(p => new Dictionary<string, object?>
            {
                ["name"] = p.Name,
                ["slug"] = p.Slug,
                ["plugin_file"] = p.PluginFile,
                ["version"] = p.Version,
                ["status"] = p.Status,
                ["update_channel"] = p.UpdateChannel,
                ["auto_update"] = p.AutoUpdate,
                ["update_available_version"] = p.Update?.NewVersion,
                ["update_package"] = p.Update?.Package,
                ["option_keys"] = p.OptionKeys.Count > 0 ? string.Join(";", p.OptionKeys) : null,
                ["asset_paths"] = p.AssetPaths.Count > 0 ? string.Join(";", p.AssetPaths) : null
            })
            .ToList();

        Write(path, rows);
    }

    public static void WriteThemes(string path, IEnumerable<InstalledTheme> themes)
    {
        var rows = themes
            .Select(t => new Dictionary<string, object?>
            {
                ["name"] = t.Name,
                ["slug"] = t.Slug,
                ["stylesheet"] = t.Stylesheet,
                ["template"] = t.Template,
                ["version"] = t.Version,
                ["status"] = t.Status,
                ["update_channel"] = t.UpdateChannel,
                ["auto_update"] = t.AutoUpdate,
                ["update_available_version"] = t.Update?.NewVersion,
                ["update_package"] = t.Update?.Package,
                ["option_keys"] = t.OptionKeys.Count > 0 ? string.Join(";", t.OptionKeys) : null,
                ["asset_paths"] = t.AssetPaths.Count > 0 ? string.Join(";", t.AssetPaths) : null
            })
            .ToList();

        Write(path, rows);
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
        var needs = s.Contains(',')
                    || s.Contains('"')
                    || s.Contains('\\')
                    || s.Contains('\n')
                    || s.Contains('\r');

        if (!needs) return s;

        return $"\"{s.Replace("\"", "\"\"")}\"";
    }
}
