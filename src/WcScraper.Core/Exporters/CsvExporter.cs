using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace WcScraper.Core.Exporters;

public sealed class CsvWriteOptions
{
    public static CsvWriteOptions Default { get; } = new();

    public int RowBufferSize { get; init; } = 128;

    public int StreamWriterBufferSize { get; init; } = 4096;

    internal CsvWriteOptions Normalize()
    {
        var rowBuffer = RowBufferSize > 0 ? RowBufferSize : Default.RowBufferSize;
        var writerBuffer = StreamWriterBufferSize > 0 ? StreamWriterBufferSize : Default.StreamWriterBufferSize;

        return new CsvWriteOptions
        {
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
    /// <param name="path">The file path to write to.</param>
    /// <param name="rows">The rows to export. The rows are enumerated a single time.</param>
    /// <param name="options">Optional configuration for buffering rows while the header stabilizes.</param>
    /// <param name="bufferThreshold">
    /// Optional limit for how many formatted rows are buffered before they are emitted and the underlying writer is flushed.
    /// When omitted or non-positive, the exporter falls back to <see cref="CsvWriteOptions.RowBufferSize"/> to determine when
    /// buffered rows should be streamed.
    /// </param>
    public static void Write(string path, IEnumerable<IDictionary<string, object?>> rows, CsvWriteOptions? options = null, int? bufferThreshold = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var effectiveOptions = (options ?? CsvWriteOptions.Default).Normalize();
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), effectiveOptions.StreamWriterBufferSize);
        Write(sw, rows, effectiveOptions, bufferThreshold);
    }

    /// <summary>
    /// Streams rows to the provided <paramref name="writer"/> as CSV content.
    /// </summary>
    /// <param name="writer">The destination writer. Must be seekable when late header expansion occurs.</param>
    /// <param name="rows">The sequence of rows to stream.</param>
    /// <param name="options">Optional configuration for buffering rows while the header stabilizes.</param>
    /// <param name="bufferThreshold">
    /// Optional limit for how many formatted rows are buffered before they are emitted and the underlying writer is flushed.
    /// When omitted or non-positive, the exporter falls back to <see cref="CsvWriteOptions.RowBufferSize"/> to determine when
    /// buffered rows should be streamed.
    /// </param>
    public static void Write(StreamWriter writer, IEnumerable<IDictionary<string, object?>> rows, CsvWriteOptions? options = null, int? bufferThreshold = null)
    {
        if (writer is null)
        {
            throw new ArgumentNullException(nameof(writer));
        }

        var effectiveOptions = (options ?? CsvWriteOptions.Default).Normalize();
        var normalizedThreshold = bufferThreshold.HasValue && bufferThreshold.Value > 0 ? bufferThreshold.Value : (int?)null;
        var headers = new List<string>();
        var headerSet = new HashSet<string>(StringComparer.Ordinal);
        var bufferLimit = normalizedThreshold.HasValue
            ? Math.Max(effectiveOptions.RowBufferSize, normalizedThreshold.Value)
            : effectiveOptions.RowBufferSize;
        var pendingRows = new List<Dictionary<string, string>>(bufferLimit);
        var emittedRows = new List<string[]>();
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

        void EmitBufferedRowsIfThresholdReached()
        {
            if (!normalizedThreshold.HasValue)
            {
                return;
            }

            if (pendingRows.Count < normalizedThreshold.Value)
            {
                return;
            }

            WriteHeaderIfNeeded();
            FlushPendingRows();
            writer.Flush();
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

            EmitBufferedRowsIfThresholdReached();

            if (!headerWritten && pendingRows.Count >= bufferLimit)
            {
                WriteHeaderIfNeeded();
            }

            if (headerWritten && pendingRows.Count >= bufferLimit)
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
        if (normalizedThreshold.HasValue)
        {
            writer.Flush();
        }
    }

    public static void WritePlugins(string path, IEnumerable<InstalledPlugin> plugins, CsvWriteOptions? options = null, int? bufferThreshold = null)
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
            options,
            bufferThreshold);
    }

    public static void WriteThemes(string path, IEnumerable<InstalledTheme> themes, CsvWriteOptions? options = null, int? bufferThreshold = null)
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
            options,
            bufferThreshold);
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
