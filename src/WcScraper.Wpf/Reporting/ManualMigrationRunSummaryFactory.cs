using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using WcScraper.Core;

namespace WcScraper.Wpf.Reporting;

public static class ManualMigrationRunSummaryFactory
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string CreateSnapshotJson(ManualMigrationReportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var snapshot = new ManualMigrationRunSnapshot(
            context.StoreIdentifier,
            Normalize(context.StoreUrl),
            context.IsWooCommerce,
            Normalize(context.WordPressVersion),
            Normalize(context.WooCommerceVersion),
            context.RequestedPublicExtensionFootprints,
            BuildPluginSummaries(context.Plugins),
            BuildThemeSummaries(context.Themes),
            BuildPublicExtensionSummaries(context.PublicExtensions),
            BuildDesignSummary(context),
            new RetrySummary(
                context.HttpRetriesEnabled,
                context.HttpRetryAttempts,
                Math.Round(context.HttpRetryBaseDelay.TotalSeconds, 3),
                Math.Round(context.HttpRetryMaxDelay.TotalSeconds, 3)),
            BuildLogHighlights(context.LogEntries),
            BuildMissingCredentialNotes(context.MissingCredentialExports));

        return JsonSerializer.Serialize(snapshot, s_jsonOptions);
    }

    private static IReadOnlyList<PluginSummary> BuildPluginSummaries(IReadOnlyList<InstalledPlugin> plugins)
    {
        if (plugins is null || plugins.Count == 0)
        {
            return Array.Empty<PluginSummary>();
        }

        return plugins
            .Select(plugin => new PluginSummary(
                ResolvePluginSlug(plugin),
                Normalize(plugin.Name),
                Normalize(plugin.Version),
                Normalize(plugin.Status)))
            .OrderBy(summary => summary.Slug, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<ThemeSummary> BuildThemeSummaries(IReadOnlyList<InstalledTheme> themes)
    {
        if (themes is null || themes.Count == 0)
        {
            return Array.Empty<ThemeSummary>();
        }

        return themes
            .Select(theme => new ThemeSummary(
                ResolveThemeSlug(theme),
                Normalize(theme.Name),
                Normalize(theme.Version),
                Normalize(theme.Status)))
            .OrderBy(summary => summary.Slug, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<PublicExtensionSummary> BuildPublicExtensionSummaries(IReadOnlyList<PublicExtensionFootprint> extensions)
    {
        if (extensions is null || extensions.Count == 0)
        {
            return Array.Empty<PublicExtensionSummary>();
        }

        return extensions
            .GroupBy(extension => (Slug: Normalize(extension.Slug) ?? string.Empty, Type: Normalize(extension.Type) ?? string.Empty), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var primary = group.First();
                return new PublicExtensionSummary(
                    group.Key.Slug,
                    group.Key.Type,
                    Normalize(primary.VersionHint),
                    Normalize(primary.DirectoryVersion),
                    Normalize(primary.DirectoryStatus),
                    Normalize(primary.DirectoryTitle));
            })
            .OrderBy(summary => summary.Slug, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static DesignSummary? BuildDesignSummary(ManualMigrationReportContext context)
    {
        var requestedSnapshot = context.RequestedDesignSnapshot;
        var requestedScreenshots = context.RequestedDesignScreenshots;
        if (!requestedSnapshot && !requestedScreenshots)
        {
            return null;
        }

        DesignSnapshotDetails? snapshotDetails = null;
        if (requestedSnapshot && !context.DesignSnapshotFailed && context.DesignSnapshot is { } snapshot && !string.IsNullOrWhiteSpace(snapshot.RawHtml))
        {
            var pages = snapshot.Pages
                .Where(page => !string.IsNullOrWhiteSpace(page.Url))
                .Select(page => new DesignPageSummary(
                    Normalize(page.Url) ?? string.Empty,
                    page.RawHtml?.Length ?? 0,
                    page.InlineCss?.Length ?? 0,
                    page.Stylesheets?.Count ?? 0,
                    page.FontFiles?.Count ?? 0,
                    page.ImageFiles?.Count ?? 0,
                    page.CssImageFiles?.Count ?? 0,
                    page.HtmlImageFiles?.Count ?? 0))
                .ToArray();

            snapshotDetails = new DesignSnapshotDetails(
                Normalize(snapshot.HomeUrl),
                snapshot.RawHtml.Length,
                snapshot.InlineCss?.Length ?? 0,
                snapshot.Stylesheets?.Count ?? 0,
                snapshot.FontFiles?.Count ?? 0,
                snapshot.IconFiles?.Count ?? 0,
                snapshot.ImageFiles?.Count ?? 0,
                snapshot.CssImageFiles?.Count ?? 0,
                snapshot.HtmlImageFiles?.Count ?? 0,
                snapshot.FontUrls?.Count ?? 0,
                snapshot.ColorSwatches?.Count ?? 0,
                pages);
        }

        IReadOnlyList<ScreenshotSummary> screenshots = Array.Empty<ScreenshotSummary>();
        if (requestedScreenshots && context.DesignScreenshots.Count > 0)
        {
            screenshots = context.DesignScreenshots
                .Select(screenshot => new ScreenshotSummary(
                    screenshot.Label,
                    screenshot.Width,
                    screenshot.Height,
                    Normalize(Path.GetFileName(screenshot.FilePath))))
                .ToArray();
        }

        return new DesignSummary(
            requestedSnapshot,
            context.DesignSnapshotFailed,
            snapshotDetails,
            requestedScreenshots,
            screenshots);
    }

    private static IReadOnlyList<string> BuildLogHighlights(IReadOnlyList<string> logEntries)
    {
        if (logEntries is null || logEntries.Count == 0)
        {
            return Array.Empty<string>();
        }

        var start = Math.Max(0, logEntries.Count - 20);
        return logEntries
            .Skip(start)
            .Select(entry => entry.Trim())
            .Where(entry => entry.Length > 0)
            .ToArray();
    }

    private static IReadOnlyList<string> BuildMissingCredentialNotes(IReadOnlyList<string> entries)
    {
        if (entries is null || entries.Count == 0)
        {
            return Array.Empty<string>();
        }

        return entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ResolvePluginSlug(InstalledPlugin plugin)
    {
        if (!string.IsNullOrWhiteSpace(plugin.Slug))
        {
            return plugin.Slug.Trim();
        }

        if (!string.IsNullOrWhiteSpace(plugin.PluginFile))
        {
            var pluginFile = plugin.PluginFile.Trim();
            var slashIndex = pluginFile.IndexOf('/');
            if (slashIndex > 0)
            {
                pluginFile = pluginFile[..slashIndex];
            }

            if (!string.IsNullOrWhiteSpace(pluginFile))
            {
                return pluginFile.Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(plugin.Name))
        {
            return plugin.Name.Trim();
        }

        return "unknown-plugin";
    }

    private static string ResolveThemeSlug(InstalledTheme theme)
    {
        if (!string.IsNullOrWhiteSpace(theme.Slug))
        {
            return theme.Slug.Trim();
        }

        if (!string.IsNullOrWhiteSpace(theme.Stylesheet))
        {
            return theme.Stylesheet.Trim();
        }

        if (!string.IsNullOrWhiteSpace(theme.Template))
        {
            return theme.Template.Trim();
        }

        if (!string.IsNullOrWhiteSpace(theme.Name))
        {
            return theme.Name.Trim();
        }

        return "unknown-theme";
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private sealed record ManualMigrationRunSnapshot(
        string StoreIdentifier,
        string? StoreUrl,
        bool IsWooCommerce,
        string? WordPressVersion,
        string? WooCommerceVersion,
        bool RequestedPublicExtensionFootprints,
        IReadOnlyList<PluginSummary> Plugins,
        IReadOnlyList<ThemeSummary> Themes,
        IReadOnlyList<PublicExtensionSummary> PublicExtensions,
        DesignSummary? Design,
        RetrySummary Retry,
        IReadOnlyList<string> LogHighlights,
        IReadOnlyList<string> MissingCredentialExports);

    private sealed record PluginSummary(
        string Slug,
        string? Name,
        string? Version,
        string? Status);

    private sealed record ThemeSummary(
        string Slug,
        string? Name,
        string? Version,
        string? Status);

    private sealed record PublicExtensionSummary(
        string Slug,
        string Type,
        string? VersionHint,
        string? DirectoryVersion,
        string? DirectoryStatus,
        string? DirectoryTitle);

    private sealed record DesignSummary(
        bool RequestedSnapshot,
        bool SnapshotFailed,
        DesignSnapshotDetails? Snapshot,
        bool RequestedScreenshots,
        IReadOnlyList<ScreenshotSummary> Screenshots);

    private sealed record DesignSnapshotDetails(
        string? HomeUrl,
        int HtmlLength,
        int InlineCssLength,
        int StylesheetCount,
        int FontFileCount,
        int IconCount,
        int ImageCount,
        int CssImageCount,
        int HtmlImageCount,
        int FontDeclarationCount,
        int ColorPaletteCount,
        IReadOnlyList<DesignPageSummary> Pages);

    private sealed record DesignPageSummary(
        string Url,
        int HtmlLength,
        int InlineCssLength,
        int StylesheetCount,
        int FontFileCount,
        int ImageCount,
        int CssImageCount,
        int HtmlImageCount);

    private sealed record ScreenshotSummary(
        string Label,
        int Width,
        int Height,
        string? FileName);

    private sealed record RetrySummary(
        bool Enabled,
        int Attempts,
        double BaseDelaySeconds,
        double MaxDelaySeconds);
}
