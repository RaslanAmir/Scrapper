using System.Text.Json;
using System.Text.Json.Nodes;

namespace WcScraper.Core.Exporters;

public static class JsonlExporter
{
    /// <summary>
    /// Writes the provided rows to disk as newline-delimited JSON without materializing the source sequence.
    /// When <paramref name="flushEvery" /> is specified, the underlying writer is flushed after each multiple of the provided
    /// value to ensure downstream consumers can observe progress as rows are streamed.
    /// </summary>
    /// <param name="path">The file path to write to.</param>
    /// <param name="rows">The sequence of rows to write. The sequence is streamed and enumerated only once.</param>
    /// <param name="flushEvery">Optional cadence at which to flush the writer while streaming rows.</param>
    public static void Write(string path, IEnumerable<IDictionary<string, object?>> rows, int? flushEvery = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var sw = new StreamWriter(fs, System.Text.Encoding.UTF8);

        Write(sw, rows, flushEvery);
    }

    public static void WritePlugins(string path, IEnumerable<InstalledPlugin> plugins, int? flushEvery = null)
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
                ["option_data"] = CloneOptionData(p.OptionData),
                ["option_keys"] = p.OptionKeys,
                ["asset_manifest"] = CloneNode(p.AssetManifest),
                ["asset_paths"] = p.AssetPaths
            });

        Write(path, rows, flushEvery);
    }

    public static void WriteThemes(string path, IEnumerable<InstalledTheme> themes, int? flushEvery = null)
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
                ["option_data"] = CloneOptionData(t.OptionData),
                ["option_keys"] = t.OptionKeys,
                ["asset_manifest"] = CloneNode(t.AssetManifest),
                ["asset_paths"] = t.AssetPaths
            });

        Write(path, rows, flushEvery);
    }

    internal static void Write(TextWriter writer, IEnumerable<IDictionary<string, object?>> rows, int? flushEvery = null)
    {
        var opts = new JsonSerializerOptions { WriteIndented = false };
        var shouldFlush = flushEvery.HasValue && flushEvery.Value > 0;
        var flushCadence = flushEvery.GetValueOrDefault();
        var written = 0;

        foreach (var row in rows)
        {
            writer.WriteLine(JsonSerializer.Serialize(row, opts));

            if (shouldFlush)
            {
                written++;
                if (written % flushCadence == 0)
                {
                    writer.Flush();
                }
            }
        }
    }

    private static JsonNode? CloneNode(JsonNode? node)
        => node?.DeepClone();

    private static JsonObject? CloneOptionData(Dictionary<string, JsonNode?> data)
    {
        if (data is null || data.Count == 0)
        {
            return null;
        }

        var obj = new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true });
        foreach (var kvp in data)
        {
            obj[kvp.Key] = kvp.Value?.DeepClone();
        }

        return obj;
    }
}
