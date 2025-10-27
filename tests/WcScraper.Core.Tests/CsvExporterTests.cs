using System.Text;
using WcScraper.Core.Exporters;
using Xunit;

namespace WcScraper.Core.Tests;

public sealed class CsvExporterTests
{
    [Fact]
    public void Write_ProducesUnionedHeadersAndEmptyCells()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".csv");

        try
        {
            var rows = new List<IDictionary<string, object?>>
            {
                new Dictionary<string, object?>
                {
                    ["alpha"] = 1,
                    ["beta"] = "foo"
                },
                new Dictionary<string, object?>
                {
                    ["beta"] = "bar",
                    ["gamma"] = true
                },
                new Dictionary<string, object?>
                {
                    ["gamma"] = 3.14m
                }
            };

            CsvExporter.Write(tempPath, rows);

            var lines = File.ReadAllLines(tempPath);

            Assert.Equal(4, lines.Length);
            Assert.Equal("alpha,beta,gamma", lines[0]);
            Assert.Equal("1,foo,", lines[1]);
            Assert.Equal(",bar,TRUE", lines[2]);
            Assert.Equal(",,3.14", lines[3]);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Fact]
    public void Write_FlushesAtConfiguredCadence()
    {
        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["value"] = 1 },
            new Dictionary<string, object?> { ["value"] = 2 },
            new Dictionary<string, object?> { ["value"] = 3 }
        };

        using var stream = new MemoryStream();
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var writer = new FlushCountingStreamWriter(stream, encoding);

        try
        {
            CsvExporter.Write(writer, rows, flushEvery: 2);

            Assert.Equal(2, writer.FlushCount);

            stream.Position = 0;
            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var content = reader.ReadToEnd();

            Assert.Equal($"value{Environment.NewLine}1{Environment.NewLine}2{Environment.NewLine}3{Environment.NewLine}", content);
        }
        finally
        {
            writer.Dispose();
        }
    }

    private sealed class FlushCountingStreamWriter : StreamWriter
    {
        public FlushCountingStreamWriter(Stream stream, Encoding encoding)
            : base(stream, encoding, bufferSize: 1024, leaveOpen: true)
        {
        }

        public int FlushCount { get; private set; }

        public override void Flush()
        {
            FlushCount++;
            base.Flush();
        }
    }
}
