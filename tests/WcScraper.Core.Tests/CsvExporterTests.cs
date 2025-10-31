using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WcScraper.Core.Exporters;
using WcScraper.Core;
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
            CsvExporter.Write(writer, rows, new CsvWriteOptions { FlushEvery = 2 });

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

    [Fact]
    public void Write_WithLateHeaderExpansion_RewritesAndPadsPriorRows()
    {
        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["alpha"] = 1 },
            new Dictionary<string, object?> { ["beta"] = 2 },
            new Dictionary<string, object?> { ["alpha"] = 3, ["beta"] = 4 }
        };

        using var stream = new MemoryStream();
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        using var writer = new FlushCountingStreamWriter(stream, encoding);

        CsvExporter.Write(writer, rows, new CsvWriteOptions { FlushEvery = 1, RowBufferSize = 1 });

        writer.Flush();
        stream.Position = 0;

        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var lines = reader.ReadToEnd()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(new[] { "alpha,beta", "1,", ",2", "3,4" }, lines);
    }

    [Fact]
    public void WritePlugins_ExportsPluginMetadata()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".csv");

        try
        {
            var plugin = new InstalledPlugin
            {
                Name = "Sample Plugin",
                Slug = "sample-plugin",
                PluginFile = "sample/sample.php",
                Version = "1.2.3",
                Status = "active",
                UpdateChannel = "stable",
                AutoUpdate = true,
                Update = new PluginUpdateInfo
                {
                    NewVersion = "2.0.0",
                    Package = "https://example.test/sample.zip"
                },
                AssetManifest = JsonNode.Parse("{\"bundle\":\"main.js\"}")
            };

            plugin.OptionData["foo"] = JsonValue.Create("bar");
            plugin.OptionKeys.AddRange(new[] { "foo", "bar" });
            plugin.AssetPaths.AddRange(new[] { "js/main.js", "css/main.css" });

            CsvExporter.WritePlugins(tempPath, Yield(plugin));

            var lines = File.ReadAllLines(tempPath);

            Assert.Equal(2, lines.Length);
            Assert.Equal(
                string.Join(',',
                    "name",
                    "slug",
                    "plugin_file",
                    "version",
                    "status",
                    "update_channel",
                    "auto_update",
                    "update_available_version",
                    "update_package",
                    "option_data",
                    "option_keys",
                    "asset_manifest",
                    "asset_paths"),
                lines[0]);

            var pluginValues = lines[1].Split(',');
            Assert.Collection(pluginValues,
                v => Assert.Equal("Sample Plugin", v),
                v => Assert.Equal("sample-plugin", v),
                v => Assert.Equal("sample/sample.php", v),
                v => Assert.Equal("1.2.3", v),
                v => Assert.Equal("active", v),
                v => Assert.Equal("stable", v),
                v => Assert.Equal("TRUE", v),
                v => Assert.Equal("2.0.0", v),
                v => Assert.Equal("https://example.test/sample.zip", v),
                v => Assert.Equal("\"{\"\"foo\"\":\"\"bar\"\"}\"", v),
                v => Assert.Equal("foo;bar", v),
                v => Assert.Equal("\"{\"\"bundle\"\":\"\"main.js\"\"}\"", v),
                v => Assert.Equal("js/main.js;css/main.css", v));
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
    public void WriteThemes_ExportsThemeMetadata()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".csv");

        try
        {
            var theme = new InstalledTheme
            {
                Name = "Sample Theme",
                Slug = "sample-theme",
                Stylesheet = "sample/style.css",
                Template = "sample-template",
                Version = "4.5.6",
                Status = "active",
                UpdateChannel = "beta",
                AutoUpdate = false,
                Update = new ThemeUpdateInfo
                {
                    NewVersion = "4.6.0",
                    Package = "https://example.test/theme.zip"
                },
                AssetManifest = JsonNode.Parse("{\"style\":\"main.css\"}")
            };

            theme.OptionData["baz"] = JsonValue.Create("qux");
            theme.OptionKeys.AddRange(new[] { "baz" });
            theme.AssetPaths.Add("css/main.css");

            CsvExporter.WriteThemes(tempPath, Yield(theme));

            var lines = File.ReadAllLines(tempPath);

            Assert.Equal(2, lines.Length);
            Assert.Equal(
                string.Join(',',
                    "name",
                    "slug",
                    "stylesheet",
                    "template",
                    "version",
                    "status",
                    "update_channel",
                    "auto_update",
                    "update_available_version",
                    "update_package",
                    "option_data",
                    "option_keys",
                    "asset_manifest",
                    "asset_paths"),
                lines[0]);

            var themeValues = lines[1].Split(',');
            Assert.Collection(themeValues,
                v => Assert.Equal("Sample Theme", v),
                v => Assert.Equal("sample-theme", v),
                v => Assert.Equal("sample/style.css", v),
                v => Assert.Equal("sample-template", v),
                v => Assert.Equal("4.5.6", v),
                v => Assert.Equal("active", v),
                v => Assert.Equal("beta", v),
                v => Assert.Equal("FALSE", v),
                v => Assert.Equal("4.6.0", v),
                v => Assert.Equal("https://example.test/theme.zip", v),
                v => Assert.Equal("\"{\"\"baz\"\":\"\"qux\"\"}\"", v),
                v => Assert.Equal("baz", v),
                v => Assert.Equal("\"{\"\"style\"\":\"\"main.css\"\"}\"", v),
                v => Assert.Equal("css/main.css", v));
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static IEnumerable<T> Yield<T>(params T[] items)
    {
        foreach (var item in items)
        {
            yield return item;
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
