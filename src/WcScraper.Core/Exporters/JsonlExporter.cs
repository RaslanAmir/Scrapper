using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WcScraper.Core.Exporters;

public static class JsonlExporter
{
    /// <summary>
    /// Writes the provided rows to disk as newline-delimited JSON without materializing the source sequence.
    /// When <paramref name="bufferThreshold" /> is specified, the exporter flushes the underlying writer whenever the buffered
    /// row count meets the threshold so downstream consumers can observe progress as rows are streamed. Any remaining buffered
    /// rows are flushed once enumeration completes.
    /// </summary>
    /// <param name="path">The file path to write to.</param>
    /// <param name="rows">The sequence of rows to write. The sequence is streamed and enumerated only once.</param>
    /// <param name="bufferThreshold">Optional row count that triggers a writer flush while streaming rows.</param>
    public static void Write(string path, IEnumerable<IDictionary<string, object?>> rows, int? bufferThreshold = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var sw = new StreamWriter(fs, System.Text.Encoding.UTF8);

        Write(sw, rows, bufferThreshold);
    }

    public static void WritePlugins(string path, IEnumerable<InstalledPlugin> plugins, int? bufferThreshold = null)
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

        Write(path, rows, bufferThreshold);
    }

    public static void WriteThemes(string path, IEnumerable<InstalledTheme> themes, int? bufferThreshold = null)
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

        Write(path, rows, bufferThreshold);
    }

    internal static void Write(TextWriter writer, IEnumerable<IDictionary<string, object?>> rows, int? bufferThreshold = null)
    {
        var opts = new JsonSerializerOptions { WriteIndented = false };
        var shouldFlush = bufferThreshold.HasValue && bufferThreshold.Value > 0;
        var flushCadence = bufferThreshold.GetValueOrDefault();
        var buffered = 0;

        foreach (var row in rows)
        {
            writer.WriteLine(JsonSerializer.Serialize(row, opts));

            if (shouldFlush)
            {
                buffered++;
                if (buffered >= flushCadence)
                {
                    writer.Flush();
                    buffered = 0;
                }
            }
        }

        if (shouldFlush && buffered > 0)
        {
            writer.Flush();
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
