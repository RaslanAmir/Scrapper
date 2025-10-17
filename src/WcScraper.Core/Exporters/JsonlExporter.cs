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
                ["option_keys"] = p.OptionKeys,
                ["asset_paths"] = p.AssetPaths
            });

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
                ["option_keys"] = t.OptionKeys,
                ["asset_paths"] = t.AssetPaths
            });

        Write(path, rows);
    }
}
