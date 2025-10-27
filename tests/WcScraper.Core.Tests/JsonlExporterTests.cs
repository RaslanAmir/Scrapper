using WcScraper.Core.Exporters;

namespace WcScraper.Core.Tests;

public class JsonlExporterTests
{
    [Fact]
    public void Write_FlushesAtSpecifiedCadence()
    {
        var rows = CreateRows(5);
        using var writer = new FlushTrackingTextWriter();

        JsonlExporter.Write(writer, rows, flushEvery: 2);

        Assert.Equal(2, writer.FlushCount);
    }

    [Fact]
    public void Write_DoesNotFlush_WhenCadenceNotProvided()
    {
        var rows = CreateRows(3);
        using var writer = new FlushTrackingTextWriter();

        JsonlExporter.Write(writer, rows);

        Assert.Equal(0, writer.FlushCount);
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
