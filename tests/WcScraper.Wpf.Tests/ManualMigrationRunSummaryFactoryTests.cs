using System;
using System.Collections.Generic;
using System.Text.Json;
using WcScraper.Core;
using WcScraper.Wpf.Reporting;
using Xunit;

namespace WcScraper.Wpf.Tests;

public class ManualMigrationRunSummaryFactoryTests
{
    [Fact]
    public void CreateSnapshotJson_CapturesKeySections()
    {
        var plugins = new List<InstalledPlugin>
        {
            new() { Slug = "sample-plugin", Name = "Sample Plugin", Version = "1.2.3", Status = "active" }
        };

        var themes = new List<InstalledTheme>
        {
            new() { Slug = "sample-theme", Name = "Sample Theme", Version = "2.0", Status = "active" }
        };

        var publicExtensions = new List<PublicExtensionFootprint>
        {
            new() { Slug = "public-plugin", Type = "plugin", VersionHint = "3.0", DirectoryVersion = "2.9", DirectoryStatus = "active", DirectoryTitle = "Public Plugin" }
        };

        var designSnapshot = new FrontEndDesignSnapshotResult(
            "https://example.com",
            "<html><body>Home</body></html>",
            "body{color:#000;}",
            new List<string> { "https://fonts.example.com/font.woff" },
            new List<StylesheetSnapshot>(),
            new List<FontAssetSnapshot>(),
            new List<DesignIconSnapshot>(),
            new List<DesignImageSnapshot>(),
            new List<DesignImageSnapshot>(),
            new List<DesignImageSnapshot>(),
            new List<ColorSwatch> { new("#000000", 1) }
        );

        var designScreenshots = new List<DesignScreenshot>
        {
            new("Desktop", 1440, 900, "desktop.png", Array.Empty<byte>())
        };

        var logEntries = new List<string> { "Log A", "Log B" };

        var context = new ManualMigrationReportContext(
            "https://example.com",
            "example-store",
            "output",
            true,
            plugins,
            themes,
            publicExtensions,
            publicExtensionDetection: null,
            wordPressVersion: "6.4",
            wooCommerceVersion: "8.4",
            pluginBundles: Array.Empty<ExtensionArtifact>(),
            themeBundles: Array.Empty<ExtensionArtifact>(),
            requestedPluginInventory: true,
            requestedThemeInventory: true,
            requestedPublicExtensionFootprints: true,
            attemptedPluginFetch: true,
            attemptedThemeFetch: true,
            attemptedPublicExtensionFootprintFetch: true,
            designSnapshot,
            designScreenshots,
            requestedDesignSnapshot: true,
            requestedDesignScreenshots: true,
            designSnapshotFailed: false,
            missingCredentialExports: new List<string> { "Missing credentials" },
            logEntries,
            DateTime.UtcNow,
            httpRetriesEnabled: true,
            httpRetryAttempts: 3,
            httpRetryBaseDelay: TimeSpan.FromSeconds(1),
            httpRetryMaxDelay: TimeSpan.FromSeconds(10));

        var json = ManualMigrationRunSummaryFactory.CreateSnapshotJson(context);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("example-store", root.GetProperty("storeIdentifier").GetString());
        Assert.True(root.GetProperty("isWooCommerce").GetBoolean());

        var pluginElement = root.GetProperty("plugins")[0];
        Assert.Equal("sample-plugin", pluginElement.GetProperty("slug").GetString());
        Assert.Equal("1.2.3", pluginElement.GetProperty("version").GetString());

        var designElement = root.GetProperty("design");
        Assert.True(designElement.GetProperty("requestedSnapshot").GetBoolean());
        Assert.Equal(designSnapshot.RawHtml.Length, designElement.GetProperty("snapshot").GetProperty("htmlLength").GetInt32());
        Assert.Equal("Desktop", designElement.GetProperty("screenshots")[0].GetProperty("label").GetString());

        var retryElement = root.GetProperty("retry");
        Assert.True(retryElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(3, retryElement.GetProperty("attempts").GetInt32());

        Assert.Equal(2, root.GetProperty("logHighlights").GetArrayLength());
    }
}
