using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using WcScraper.Core.Exporters;

namespace WcScraper.Core.Tests;

public class JsonlExporterTests
{
    [Fact]
    public void Write_FlushesWhenThresholdReached()
    {
        var rows = CreateRows(5);
        using var writer = new FlushTrackingTextWriter();

        JsonlExporter.Write(writer, rows, bufferThreshold: 2);

        Assert.Equal(3, writer.FlushCount);
    }

    [Fact]
    public void Write_DoesNotFlushWhenThresholdOmitted()
    {
        var rows = CreateRows(3);
        using var writer = new FlushTrackingTextWriter();

        JsonlExporter.Write(writer, rows);

        Assert.Equal(0, writer.FlushCount);
    }

    [Fact]
    public void Write_PreservesRowCount_WhenDictionariesHaveDifferentKeyOrders()
    {
        var tempFile = Path.GetTempFileName();

        try
        {
            var rows = new List<IDictionary<string, object?>>
            {
                new Dictionary<string, object?>
                {
                    ["beta"] = 10,
                    ["alpha"] = "row1"
                },
                new Dictionary<string, object?>
                {
                    ["gamma"] = true,
                    ["beta"] = "row2"
                },
                new Dictionary<string, object?>
                {
                    ["alpha"] = "row3",
                    ["delta"] = 42
                }
            };

            JsonlExporter.Write(tempFile, rows);

            var lines = File.ReadAllLines(tempFile);

            Assert.Equal(rows.Count, lines.Length);
            Assert.Collection(lines,
                line =>
                {
                    using var doc = JsonDocument.Parse(line);
                    Assert.Equal("row1", doc.RootElement.GetProperty("alpha").GetString());
                },
                line =>
                {
                    using var doc = JsonDocument.Parse(line);
                    Assert.Equal("row2", doc.RootElement.GetProperty("beta").GetString());
                },
                line =>
                {
                    using var doc = JsonDocument.Parse(line);
                    Assert.Equal("row3", doc.RootElement.GetProperty("alpha").GetString());
                });
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void Write_StreamedRows_FlushesAtThresholdAndPreservesLineCount()
    {
        var yielded = 0;

        static IDictionary<string, object?> CreateRow(params (string Key, object? Value)[] pairs)
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, value) in pairs)
            {
                dict[key] = value;
            }

            return dict;
        }

        IEnumerable<IDictionary<string, object?>> StreamRows()
        {
            yielded++;
            yield return CreateRow(("beta", "b1"), ("alpha", "a1"));

            yielded++;
            yield return CreateRow(("gamma", "g2"), ("beta", "b2"));

            yielded++;
            yield return CreateRow(("alpha", "a3"), ("delta", "d3"));
        }

        using var writer = new FlushTrackingTextWriter();

        JsonlExporter.Write(writer, StreamRows(), bufferThreshold: 2);

        Assert.Equal(2, writer.FlushCount);

        var lines = writer.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        Assert.All(lines, line => Assert.False(string.IsNullOrWhiteSpace(line)));
        Assert.Equal(3, yielded);
    }

    private static IEnumerable<IDictionary<string, object?>> CreateRows(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return new Dictionary<string, object?> { ["index"] = i };
        }
    }

    private sealed class FlushTrackingTextWriter : StringWriter
    {
        public int FlushCount { get; private set; }

        public override void Flush()
        {
            FlushCount++;
            base.Flush();
        }
    }
}
