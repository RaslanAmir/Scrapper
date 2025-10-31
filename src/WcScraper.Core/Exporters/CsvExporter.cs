using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WcScraper.Core.Exporters;

public sealed class CsvWriteOptions
{
    public static CsvWriteOptions Default { get; } = new();

    public int? FlushEvery { get; init; }

    public int RowBufferSize { get; init; } = 128;

    public int StreamWriterBufferSize { get; init; } = 4096;

    internal CsvWriteOptions Normalize()
    {
        var flush = FlushEvery.HasValue && FlushEvery.Value > 0 ? FlushEvery : null;
        var rowBuffer = RowBufferSize > 0 ? RowBufferSize : Default.RowBufferSize;
        var writerBuffer = StreamWriterBufferSize > 0 ? StreamWriterBufferSize : Default.StreamWriterBufferSize;

        return new CsvWriteOptions
        {
            FlushEvery = flush,
            RowBufferSize = rowBuffer,
            StreamWriterBufferSize = writerBuffer
        };
    }
}

public static class CsvExporter
{
    /// <summary>
    /// Writes the provided rows to a CSV file while capturing the union of column headers in insertion order
    /// during a single pass. The rows are buffered in-memory until the header set stabilizes, after which the
    /// header and buffered rows are streamed to the writer using the provided <see cref="CsvWriteOptions"/>.
    /// The output is written with a BOM-aware UTF-8 encoding.
    /// </summary>
    public static void Write(string path, IEnumerable<IDictionary<string, object?>> rows, CsvWriteOptions? options = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var effectiveOptions = (options ?? CsvWriteOptions.Default).Normalize();
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), effectiveOptions.StreamWriterBufferSize);
        Write(sw, rows, effectiveOptions);
    }

    public static void Write(StreamWriter writer, IEnumerable<IDictionary<string, object?>> rows, CsvWriteOptions? options = null)
    {
        if (writer is null)
        {
            throw new ArgumentNullException(nameof(writer));
        }

        var effectiveOptions = (options ?? CsvWriteOptions.Default).Normalize();
        var headers = new List<string>();
        var headerSet = new HashSet<string>(StringComparer.Ordinal);
        var pendingRows = new List<Dictionary<string, string>>(effectiveOptions.RowBufferSize);
        var emittedRows = new List<string[]>();
        var flushBatch = effectiveOptions.FlushEvery;
        var flushCounter = 0;
        var headerWritten = false;
        var anyRows = false;

        void WriteHeaderIfNeeded()
        {
            if (headerWritten)
            {
                return;
            }

            if (headers.Count == 0 && !anyRows)
            {
                return;
            }

            writer.WriteLine(string.Join(",", headers.Select(Quote)));
            headerWritten = true;
        }

        void WriteRowValues(string[] values)
        {
            var line = string.Join(",", values.Select(Quote));
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

        void FlushPendingRows()
        {
            if (pendingRows.Count == 0)
            {
                return;
            }

            foreach (var pending in pendingRows)
            {
                var values = new string[headers.Count];
                for (var i = 0; i < headers.Count; i++)
                {
                    var header = headers[i];
                    values[i] = pending.TryGetValue(header, out var value) ? value ?? string.Empty : string.Empty;
                }

                emittedRows.Add(values);
                WriteRowValues(values);
            }

            pendingRows.Clear();
        }

        void RewriteOutput()
        {
            if (!writer.BaseStream.CanSeek)
            {
                throw new InvalidOperationException("Cannot expand CSV headers on a non-seekable stream.");
            }

            writer.Flush();
            writer.BaseStream.Seek(0, SeekOrigin.Begin);
            writer.BaseStream.SetLength(0);
            headerWritten = false;
            flushCounter = 0;

            if (headers.Count == 0)
            {
                return;
            }

            WriteHeaderIfNeeded();

            for (var i = 0; i < emittedRows.Count; i++)
            {
                var row = emittedRows[i];
                if (row.Length != headers.Count)
                {
                    var previousLength = row.Length;
                    Array.Resize(ref row, headers.Count);
                    for (var j = previousLength; j < headers.Count; j++)
                    {
                        row[j] = string.Empty;
                    }
                    emittedRows[i] = row;
                }

                WriteRowValues(row);
            }
        }

        void ExpandEmittedRowsForNewHeader()
        {
            for (var i = 0; i < emittedRows.Count; i++)
            {
                var row = emittedRows[i];
                var originalLength = row.Length;
                if (originalLength == headers.Count)
                {
                    continue;
                }

                Array.Resize(ref row, headers.Count);
                for (var j = originalLength; j < headers.Count; j++)
                {
                    row[j] = string.Empty;
                }

                emittedRows[i] = row;
            }
        }

        foreach (var row in rows)
        {
            if (row is null)
            {
                continue;
            }

            anyRows = true;
            var formattedRow = new Dictionary<string, string>(StringComparer.Ordinal);
            var headerExpanded = false;

            foreach (var kvp in row)
            {
                if (headerSet.Add(kvp.Key))
                {
                    headers.Add(kvp.Key);
                    headerExpanded = true;
                }

                formattedRow[kvp.Key] = Format(kvp.Value);
            }

            if (headerExpanded)
            {
                ExpandEmittedRowsForNewHeader();

                if (headerWritten)
                {
                    RewriteOutput();
                }
            }

            pendingRows.Add(formattedRow);

            if (!headerWritten && pendingRows.Count >= effectiveOptions.RowBufferSize)
            {
                WriteHeaderIfNeeded();
            }

            if (headerWritten && pendingRows.Count >= effectiveOptions.RowBufferSize)
            {
                FlushPendingRows();
            }
        }

        if (!anyRows)
        {
            writer.WriteLine(string.Empty);
            return;
        }

        WriteHeaderIfNeeded();
        FlushPendingRows();

        if (flushBatch.HasValue && flushCounter > 0)
        {
            writer.Flush();
        }
    }

    public static void WritePlugins(string path, IEnumerable<InstalledPlugin> plugins, CsvWriteOptions? options = null)
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
            options);
    }

    public static void WriteThemes(string path, IEnumerable<InstalledTheme> themes, CsvWriteOptions? options = null)
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
            options);
    }

    private static string Format(object? v)
    {
        if (v is null) return string.Empty;
        return v switch
        {
            DateTime dt => dt.ToString("s", CultureInfo.InvariantCulture),
            double d => d.ToString("0.######", CultureInfo.InvariantCulture),
            float f => f.ToString("0.######", CultureInfo.InvariantCulture),
            decimal m => m.ToString("0.######", CultureInfo.InvariantCulture),
            bool b => b ? "TRUE" : "FALSE",
            _ => v.ToString() ?? string.Empty
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
