using System.Collections.Generic;
using System.IO;
using WcScraper.Core.Exporters;
using Xunit;

namespace WcScraper.Core.Tests;

public sealed class CsvExporterUnionTests
{
    [Fact]
    public void Write_WithUnorderedDictionaries_ProducesUnionedHeadersAndAlignedRows()
    {
        var tempFile = Path.GetTempFileName();

        try
        {
            var rows = new List<IDictionary<string, object?>>
            {
                new Dictionary<string, object?>
                {
                    ["beta"] = "row1-beta",
                    ["alpha"] = 1
                },
                new Dictionary<string, object?>
                {
                    ["gamma"] = true,
                    ["beta"] = "row2-beta"
                },
                new Dictionary<string, object?>
                {
                    ["alpha"] = 2,
                    ["gamma"] = 3.5m
                }
            };

            CsvExporter.Write(tempFile, rows);

            var lines = File.ReadAllLines(tempFile);

            Assert.Collection(lines,
                header => Assert.Equal("beta,alpha,gamma", header),
                row1 => Assert.Equal("row1-beta,1,", row1),
                row2 => Assert.Equal("row2-beta,,TRUE", row2),
                row3 => Assert.Equal(",2,3.5", row3));
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
