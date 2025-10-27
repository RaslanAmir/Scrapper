using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WcScraper.Core.Exporters;

public static class CsvExporter
{
    /// <summary>
    /// Writes the provided rows to a CSV file by buffering them in a temporary backing store, capturing
    /// the union of column headers in insertion order in a single pass, rewinding the buffer to emit the
    /// header row followed by each buffered record, and optionally flushing the output writer after every
    /// <paramref name="flushEvery"/> rows. The output is written with a BOM-aware UTF-8 encoding.
    /// </summary>
    public static void Write(string path, IEnumerable<IDictionary<string, object?>> rows, int? flushEvery = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Write(sw, rows, flushEvery);
    }

    public static void Write(StreamWriter writer, IEnumerable<IDictionary<string, object?>> rows, int? flushEvery = null)
    {
        if (writer is null)
        {
            throw new ArgumentNullException(nameof(writer));
        }

        string? tempPath = null;
        var flushBatch = flushEvery.HasValue && flushEvery.Value > 0 ? flushEvery.Value : (int?)null;

        try
        {
            tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            using var tempStream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.SequentialScan);
            using var tempWriter = new BinaryWriter(tempStream, Encoding.UTF8, leaveOpen: true);

            var headers = new List<string>();
            var headerSet = new HashSet<string>(StringComparer.Ordinal);
            var rowCount = 0;

            foreach (var row in rows)
            {
                if (row is null)
                {
                    continue;
                }

                var formattedRow = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kvp in row)
                {
                    if (headerSet.Add(kvp.Key))
                    {
                        headers.Add(kvp.Key);
                    }

                    formattedRow[kvp.Key] = Format(kvp.Value);
                }

                var buffer = JsonSerializer.SerializeToUtf8Bytes(formattedRow);
                tempWriter.Write(buffer.Length);
                tempWriter.Write(buffer);
                rowCount++;
            }

            tempWriter.Flush();
            tempStream.Position = 0;

            if (rowCount == 0)
            {
                writer.WriteLine("");
                return;
            }

            writer.WriteLine(string.Join(",", headers.Select(Quote)));

            using var tempReader = new BinaryReader(tempStream, Encoding.UTF8, leaveOpen: true);
            var flushCounter = 0;

            while (tempStream.Position < tempStream.Length)
            {
                var length = tempReader.ReadInt32();
                var buffer = tempReader.ReadBytes(length);
                var formattedRow = JsonSerializer.Deserialize<Dictionary<string, string>>(buffer) ?? new Dictionary<string, string>(StringComparer.Ordinal);

                var line = string.Join(",", headers.Select(header => Quote(formattedRow.TryGetValue(header, out var value) ? value ?? string.Empty : string.Empty)));

                writer.WriteLine(line);

                if (flushBatch.HasValue)
                {
                    flushCounter++;
                    if (flushCounter >= flushBatch.Value)
                    {
                        writer.Flush();
                        flushCounter = 0;
                    }
                }
            }

            if (flushBatch.HasValue && flushCounter > 0)
            {
                writer.Flush();
            }
        }
        finally
        {
            if (tempPath is not null && File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public static void WritePlugins(string path, IEnumerable<InstalledPlugin> plugins, int? flushEvery = null)
    {
        Write(path, plugins
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
                ["option_data"] = p.OptionData.Count > 0 ? JsonSerializer.Serialize(p.OptionData) : null,
                ["option_keys"] = p.OptionKeys.Count > 0 ? string.Join(";", p.OptionKeys) : null,
                ["asset_manifest"] = p.AssetManifest is not null ? p.AssetManifest.ToJsonString() : null,
                ["asset_paths"] = p.AssetPaths.Count > 0 ? string.Join(";", p.AssetPaths) : null
            }),
            flushEvery);
    }

    public static void WriteThemes(string path, IEnumerable<InstalledTheme> themes, int? flushEvery = null)
    {
        Write(path, themes
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
                ["option_data"] = t.OptionData.Count > 0 ? JsonSerializer.Serialize(t.OptionData) : null,
                ["option_keys"] = t.OptionKeys.Count > 0 ? string.Join(";", t.OptionKeys) : null,
                ["asset_manifest"] = t.AssetManifest is not null ? t.AssetManifest.ToJsonString() : null,
                ["asset_paths"] = t.AssetPaths.Count > 0 ? string.Join(";", t.AssetPaths) : null
            }),
            flushEvery);
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
