using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using WcScraper.Core;
using WcScraper.Core.Models;

namespace WcScraper.Wpf.Reporting;

internal sealed class ManualMigrationReportBuilder
{
    public string Build(ManualMigrationReportContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var builder = new StringBuilder();

        builder.AppendLine("# Manual Migration Report");
        builder.AppendLine();
        builder.AppendLine($"- **Generated:** {context.GeneratedAtUtc:O} (UTC)");
        if (!string.IsNullOrWhiteSpace(context.StoreUrl))
        {
            builder.AppendLine($"- **Store URL:** {context.StoreUrl}");
        }

        if (!string.IsNullOrWhiteSpace(context.StoreIdentifier))
        {
            builder.AppendLine($"- **Store Identifier:** `{context.StoreIdentifier}`");
        }

        if (!string.IsNullOrWhiteSpace(context.OutputFolder))
        {
            builder.AppendLine($"- **Output Folder:** `{context.OutputFolder}`");
        }

        builder.AppendLine($"- **HTTP retries:** {FormatRetrySummary(context)}");
        builder.AppendLine();
        AppendPlatformVersionSection(builder, context);
        builder.AppendLine();
        AppendExtensionFootprintSection(builder, context);
        builder.AppendLine();
        AppendDesignSection(builder, context);
        builder.AppendLine();
        AppendCredentialNotes(builder, context);
        builder.AppendLine();
        AppendLogHighlights(builder, context);

        return builder.ToString();
    }

    private static string FormatRetrySummary(ManualMigrationReportContext context)
    {
        if (!context.HttpRetriesEnabled || context.HttpRetryAttempts <= 0)
        {
            return "Disabled";
        }

        var baseDelay = FormatDuration(context.HttpRetryBaseDelay);
        var maxDelay = FormatDuration(context.HttpRetryMaxDelay);
        return $"Enabled ({context.HttpRetryAttempts} retries, base delay {baseDelay}, max delay {maxDelay})";
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalSeconds >= 1)
        {
            return $"{value.TotalSeconds:0.##}s";
        }

        return $"{value.TotalMilliseconds:0}ms";
    }

    private static string FormatByteSize(long bytes)
    {
        const double OneKb = 1024d;
        const double OneMb = OneKb * 1024d;
        const double OneGb = OneMb * 1024d;

        if (bytes >= OneGb)
        {
            return $"{bytes / OneGb:0.##} GB";
        }

        if (bytes >= OneMb)
        {
            return $"{bytes / OneMb:0.##} MB";
        }

        if (bytes >= OneKb)
        {
            return $"{bytes / OneKb:0.##} KB";
        }

        return $"{bytes:N0} B";
    }

    private static void AppendPlatformVersionSection(StringBuilder builder, ManualMigrationReportContext context)
    {
        builder.AppendLine("## Platform versions");

        if (!context.IsWooCommerce)
        {
            builder.AppendLine("Not applicable for this platform.");
            return;
        }

        if (!context.RequestedPublicExtensionFootprints)
        {
            builder.AppendLine("Public crawl not requested; no version cues captured.");
            return;
        }

        var wordpress = context.WordPressVersion;
        var woo = context.WooCommerceVersion;

        if (string.IsNullOrWhiteSpace(wordpress) && string.IsNullOrWhiteSpace(woo))
        {
            builder.AppendLine("No version cues detected in public assets.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(wordpress))
        {
            builder.AppendLine($"- WordPress core: {MarkdownEscape(wordpress)}");
        }

        if (!string.IsNullOrWhiteSpace(woo))
        {
            builder.AppendLine($"- WooCommerce: {MarkdownEscape(woo)}");
        }
    }

    private static void AppendExtensionFootprintSection(StringBuilder builder, ManualMigrationReportContext context)
    {
        builder.AppendLine("## Extension Footprint");

        AppendInstalledPlugins(builder, context);
        builder.AppendLine();
        AppendInstalledThemes(builder, context);
        builder.AppendLine();
        AppendPublicExtensionFootprints(builder, context);
        builder.AppendLine();
        AppendExtensionArtifacts(builder, "Plugin bundles", context.PluginBundles);
        builder.AppendLine();
        AppendExtensionArtifacts(builder, "Theme bundles", context.ThemeBundles);
    }

    private static void AppendInstalledPlugins(StringBuilder builder, ManualMigrationReportContext context)
    {
        builder.AppendLine("### Installed plugins (authenticated)");
        if (!context.IsWooCommerce || !context.RequestedPluginInventory)
        {
            builder.AppendLine("Not requested for this run.");
            return;
        }

        if (context.Plugins.Count == 0)
        {
            var message = context.AttemptedPluginFetch
                ? "No plugins were returned by the authenticated endpoint."
                : "Plugin inventory requires WordPress administrator credentials.";
            builder.AppendLine(message);
            return;
        }

        builder.AppendLine("| Slug | Name | Version | Status | Update Channel |");
        builder.AppendLine("| --- | --- | --- | --- | --- |");

        foreach (var plugin in context.Plugins
            .Select(p => new
            {
                Slug = ResolvePluginSlug(p),
                p.Name,
                p.Version,
                p.Status,
                Channel = DetermineUpdateChannel(p)
            })
            .OrderBy(p => p.Slug, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| {MarkdownEscape(plugin.Slug)} | {MarkdownEscape(plugin.Name)} | {MarkdownEscape(plugin.Version)} | {MarkdownEscape(plugin.Status)} | {MarkdownEscape(plugin.Channel)} |");
        }
    }

    private static void AppendInstalledThemes(StringBuilder builder, ManualMigrationReportContext context)
    {
        builder.AppendLine("### Installed themes (authenticated)");
        if (!context.IsWooCommerce || !context.RequestedThemeInventory)
        {
            builder.AppendLine("Not requested for this run.");
            return;
        }

        if (context.Themes.Count == 0)
        {
            var message = context.AttemptedThemeFetch
                ? "No themes were returned by the authenticated endpoint."
                : "Theme inventory requires WordPress administrator credentials.";
            builder.AppendLine(message);
            return;
        }

        builder.AppendLine("| Slug | Name | Version | Status | Update Channel |");
        builder.AppendLine("| --- | --- | --- | --- | --- |");

        foreach (var theme in context.Themes
            .Select(t => new
            {
                Slug = ResolveThemeSlug(t),
                t.Name,
                t.Version,
                t.Status,
                Channel = DetermineUpdateChannel(t)
            })
            .OrderBy(t => t.Slug, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| {MarkdownEscape(theme.Slug)} | {MarkdownEscape(theme.Name)} | {MarkdownEscape(theme.Version)} | {MarkdownEscape(theme.Status)} | {MarkdownEscape(theme.Channel)} |");
        }
    }

    private static void AppendPublicExtensionFootprints(StringBuilder builder, ManualMigrationReportContext context)
    {
        builder.AppendLine("### Public plugin/theme slugs");
        var detection = context.PublicExtensionDetection;
        if (detection is not null && (detection.PageLimitReached || detection.ByteLimitReached))
        {
            var limitDescription = DescribeDetectionLimit(detection);
            builder.AppendLine($"> Crawl stopped after {detection.ProcessedPageCount:N0} page(s) / {FormatByteSize(detection.TotalBytesDownloaded)} because the {limitDescription} was reached.");
            builder.AppendLine();
        }
        if (!context.IsWooCommerce || !context.RequestedPublicExtensionFootprints)
        {
            builder.AppendLine("Not requested for this run.");
            return;
        }

        if (context.PublicExtensions.Count == 0)
        {
            var message = context.AttemptedPublicExtensionFootprintFetch
                ? "No public plugin/theme slugs were detected."
                : "Slug detection runs when authenticated exports are unavailable.";
            builder.AppendLine(message);
            return;
        }

        var orderedFootprints = context.PublicExtensions
            .OrderBy(f => f.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Slug, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var anySourceUrls = orderedFootprints.Any(f => f.SourceUrls is { Count: > 0 });
        if (!anySourceUrls)
        {
            builder.AppendLine("> No source URLs were captured for the detected slugs. Directory metadata is shown when available.");
            builder.AppendLine();
        }
        else
        {
            builder.AppendLine("> Primary source URLs reflect the first capture; complete lists appear in the detailed breakdown below.");
            builder.AppendLine();
        }

        builder.AppendLine("| Type | Slug | Directory Title | Version | Homepage | Status | Primary Source URL | Asset URL | Version Hint | Source URLs |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");

        foreach (var footprint in orderedFootprints)
        {
            var primarySource = footprint.SourceUrls?.FirstOrDefault() ?? string.Empty;
            var inlineSources = FormatInlineSourceUrls(footprint.SourceUrls);

            builder.AppendLine(
                $"| {MarkdownEscape(footprint.Type)} " +
                $"| {MarkdownEscape(footprint.Slug)} " +
                $"| {MarkdownEscape(footprint.DirectoryTitle)} " +
                $"| {MarkdownEscape(footprint.DirectoryVersion)} " +
                $"| {MarkdownEscape(footprint.DirectoryHomepage)} " +
                $"| {MarkdownEscape(footprint.DirectoryStatus)} " +
                $"| {MarkdownEscape(primarySource)} " +
                $"| {MarkdownEscape(footprint.AssetUrl)} " +
                $"| {MarkdownEscape(footprint.VersionHint)} " +
                $"| {MarkdownEscape(inlineSources)} |");
        }

        builder.AppendLine();
        builder.AppendLine("#### Public slug details");

        foreach (var footprint in orderedFootprints)
        {
            builder.AppendLine($"- **{MarkdownEscape(footprint.Slug)}** ({MarkdownEscape(footprint.Type)})");

            AppendDetailLine(builder, "Directory title", footprint.DirectoryTitle);
            AppendDetailLine(builder, "Directory version", footprint.DirectoryVersion);
            AppendDetailLine(builder, "Directory homepage", footprint.DirectoryHomepage);
            AppendDetailLine(builder, "Directory status", footprint.DirectoryStatus);

            if (footprint.SourceUrls is { Count: > 0 })
            {
                builder.AppendLine("  - Source URLs:");
                foreach (var url in footprint.SourceUrls)
                {
                    builder.AppendLine("    - " + MarkdownEscape(url));
                }
            }
            else
            {
                builder.AppendLine("  - Source URLs: None captured");
            }

            AppendDetailLine(builder, "Asset URL", footprint.AssetUrl);
            AppendDetailLine(builder, "Version hint", footprint.VersionHint);

            builder.AppendLine();
        }
    }

    private static void AppendDetailLine(StringBuilder builder, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.AppendLine($"  - {label}: {MarkdownEscape(value)}");
    }

    private static string FormatInlineSourceUrls(IReadOnlyList<string>? sourceUrls)
    {
        if (sourceUrls is not { Count: > 0 })
        {
            return "None captured";
        }

        return string.Join(";", sourceUrls);
    }

    private static void AppendExtensionArtifacts(StringBuilder builder, string heading, IReadOnlyList<ExtensionArtifact> artifacts)
    {
        builder.AppendLine($"### {heading}");
        if (artifacts.Count == 0)
        {
            builder.AppendLine("No archives were captured.");
            return;
        }

        foreach (var artifact in artifacts.OrderBy(a => a.Slug, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- {MarkdownEscape(artifact.Slug)} → `{artifact.DirectoryPath}`");
        }
    }

    private static void AppendDesignSection(StringBuilder builder, ManualMigrationReportContext context)
    {
        builder.AppendLine("## Design Snapshot");

        if (!context.IsWooCommerce)
        {
            builder.AppendLine("Design snapshot exports are only available for WooCommerce targets.");
            return;
        }

        AppendDesignSnapshotSummary(builder, context);
        builder.AppendLine();
        AppendTypographySummary(builder, context);
        builder.AppendLine();
        AppendColorPalette(builder, context);
        builder.AppendLine();
        AppendDesignScreenshots(builder, context);
    }

    private static void AppendDesignSnapshotSummary(StringBuilder builder, ManualMigrationReportContext context)
    {
        builder.AppendLine("### Snapshot summary");

        if (!context.RequestedDesignSnapshot)
        {
            builder.AppendLine("Not requested for this run.");
            return;
        }

        if (context.DesignSnapshotFailed)
        {
            builder.AppendLine("Design snapshot capture failed. Review logs for details.");
            return;
        }

        if (context.DesignSnapshot is null || string.IsNullOrWhiteSpace(context.DesignSnapshot.RawHtml))
        {
            builder.AppendLine("No design HTML was captured.");
            return;
        }

        var snapshot = context.DesignSnapshot;
        builder.AppendLine("- **HTML length:** " + snapshot.RawHtml.Length.ToString("N0", CultureInfo.InvariantCulture) + " characters");
        builder.AppendLine("- **Inline CSS length:** " + snapshot.InlineCss.Length.ToString("N0", CultureInfo.InvariantCulture) + " characters");
        builder.AppendLine("- **External stylesheets:** " + snapshot.Stylesheets.Count);
        builder.AppendLine("- **Font files:** " + snapshot.FontFiles.Count);
        builder.AppendLine("- **Background images:** " + snapshot.ImageFiles.Count);
        builder.AppendLine("- **Font declarations:** " + snapshot.FontUrls.Count);
        if (snapshot.Pages.Count > 0)
        {
            builder.AppendLine("- **Captured pages:**");
            foreach (var page in snapshot.Pages)
            {
                var htmlLength = page.RawHtml.Length.ToString("N0", CultureInfo.InvariantCulture);
                var inlineLength = page.InlineCss.Length.ToString("N0", CultureInfo.InvariantCulture);
                builder.AppendLine(
                    $"  - {MarkdownEscape(page.Url)} (HTML {htmlLength} chars, inline CSS {inlineLength} chars, stylesheets {page.Stylesheets.Count}, fonts {page.FontFiles.Count}, images {page.ImageFiles.Count})");
            }
        }
        else if (!string.IsNullOrWhiteSpace(snapshot.HomeUrl))
        {
            builder.AppendLine("- **Captured pages:**");
            builder.AppendLine($"  - {MarkdownEscape(snapshot.HomeUrl)}");
        }
        builder.AppendLine("- **Design folder:** `" + Path.Combine(context.OutputFolder, "design") + "`");
        builder.AppendLine(
            "- **Asset manifest:** `" + Path.Combine(context.OutputFolder, "design", "assets-manifest.json") +
            "` (includes `file_size_bytes` and `sha256` for every stylesheet, font, and background image).");
    }

    private static void AppendTypographySummary(StringBuilder builder, ManualMigrationReportContext context)
    {
        builder.AppendLine("### Typography");

        if (!context.RequestedDesignSnapshot)
        {
            builder.AppendLine("Not requested for this run.");
            return;
        }

        if (context.DesignSnapshot is null)
        {
            builder.AppendLine("No design snapshot data available.");
            return;
        }

        if (context.DesignSnapshot.FontFiles.Count == 0)
        {
            builder.AppendLine("No font files were captured.");
            return;
        }

        var fontsWithFamilies = new List<(string Family, FontAssetSnapshot Font)>();
        var fontsWithoutFamilies = new List<FontAssetSnapshot>();

        foreach (var font in context.DesignSnapshot.FontFiles)
        {
            var family = ExtractPrimaryFontFamily(font.FontFamily);
            if (string.IsNullOrWhiteSpace(family))
            {
                fontsWithoutFamilies.Add(font);
                continue;
            }

            fontsWithFamilies.Add((family!, font));
        }

        if (fontsWithFamilies.Count == 0)
        {
            builder.AppendLine("No font family metadata detected.");
            if (fontsWithoutFamilies.Count > 0)
            {
                builder.AppendLine($"Fonts without family metadata: {fontsWithoutFamilies.Count}");
            }
            return;
        }

        foreach (var group in fontsWithFamilies
            .GroupBy(f => f.Family, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var variants = group
                .Select(g => FormatFontVariant(g.Font))
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .Select(MarkdownEscape)
                .ToList();

            var line = $"- {MarkdownEscape(group.Key)} × {group.Count()}";
            if (variants.Count > 0)
            {
                line += " (" + string.Join(", ", variants) + ")";
            }

            builder.AppendLine(line);
        }

        if (fontsWithoutFamilies.Count > 0)
        {
            builder.AppendLine($"- Fonts without family metadata: {fontsWithoutFamilies.Count}");
        }
    }

    private static void AppendColorPalette(StringBuilder builder, ManualMigrationReportContext context)
    {
        builder.AppendLine("### Color palette");

        if (!context.RequestedDesignSnapshot)
        {
            builder.AppendLine("Color analysis not requested.");
            return;
        }

        if (context.DesignSnapshot is null || context.DesignSnapshot.ColorSwatches.Count == 0)
        {
            builder.AppendLine("No CSS color swatches detected.");
            return;
        }

        foreach (var swatch in context.DesignSnapshot.ColorSwatches
            .OrderByDescending(s => s.Count)
            .ThenBy(s => s.Value, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- {MarkdownEscape(swatch.Value)} × {swatch.Count}");
        }
    }

    private static void AppendDesignScreenshots(StringBuilder builder, ManualMigrationReportContext context)
    {
        builder.AppendLine("### Design screenshots");
        if (!context.RequestedDesignScreenshots)
        {
            builder.AppendLine("Not requested for this run.");
            return;
        }

        if (context.DesignScreenshots.Count == 0)
        {
            builder.AppendLine("No screenshots were captured.");
            return;
        }

        foreach (var screenshot in context.DesignScreenshots)
        {
            builder.AppendLine($"- {MarkdownEscape(screenshot.Label)} ({screenshot.Width}×{screenshot.Height}): `{screenshot.FilePath}`");
        }
    }

    private static string? ExtractPrimaryFontFamily(string? fontFamilyValue)
    {
        if (string.IsNullOrWhiteSpace(fontFamilyValue))
        {
            return null;
        }

        foreach (var candidate in fontFamilyValue.Split(','))
        {
            var trimmed = candidate.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if ((trimmed.StartsWith("\"") && trimmed.EndsWith("\"")) || (trimmed.StartsWith("'") && trimmed.EndsWith("'")))
            {
                trimmed = trimmed[1..^1];
            }

            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                return trimmed;
            }
        }

        return null;
    }

    private static string? FormatFontVariant(FontAssetSnapshot font)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(font.FontStyle))
        {
            parts.Add(font.FontStyle!);
        }

        if (!string.IsNullOrWhiteSpace(font.FontWeight))
        {
            parts.Add(font.FontWeight!);
        }

        if (parts.Count == 0)
        {
            return null;
        }

        return string.Join(" ", parts);
    }

    private static void AppendCredentialNotes(StringBuilder builder, ManualMigrationReportContext context)
    {
        builder.AppendLine("## Credential-gated exports");

        if (!context.IsWooCommerce)
        {
            builder.AppendLine("Shopify mode does not require WordPress credentials.");
            return;
        }

        var notes = context.MissingCredentialExports
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (notes.Count == 0)
        {
            builder.AppendLine("All credential-gated exports were captured during this run.");
            return;
        }

        foreach (var note in notes)
        {
            builder.AppendLine("- " + note);
        }
    }

    private static void AppendLogHighlights(StringBuilder builder, ManualMigrationReportContext context)
    {
        builder.AppendLine("## Log highlights");

        if (context.LogEntries.Count == 0)
        {
            builder.AppendLine("No log entries were recorded.");
            return;
        }

        var highlights = context.LogEntries
            .TakeLast(20)
            .ToList();

        foreach (var entry in highlights)
        {
            builder.AppendLine("- " + MarkdownEscape(entry));
        }
    }

    private static string DescribeDetectionLimit(PublicExtensionDetectionSummary detection)
    {
        var parts = new List<string>();
        if (detection.PageLimitReached)
        {
            parts.Add(detection.MaxPages.HasValue
                ? $"page cap of {detection.MaxPages.Value:N0} page(s)"
                : "configured page cap");
        }

        if (detection.ByteLimitReached)
        {
            parts.Add(detection.MaxBytes.HasValue
                ? $"byte cap of {FormatByteSize(detection.MaxBytes.Value)}"
                : "configured byte cap");
        }

        return string.Join(" and ", parts);
    }

    private static string MarkdownEscape(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }

    private static string ResolvePluginSlug(InstalledPlugin plugin)
    {
        if (!string.IsNullOrWhiteSpace(plugin.Slug))
        {
            return plugin.Slug!;
        }

        if (!string.IsNullOrWhiteSpace(plugin.PluginFile))
        {
            var pluginFile = plugin.PluginFile!;
            var slash = pluginFile.IndexOf('/');
            if (slash > 0)
            {
                return pluginFile[..slash];
            }

            if (pluginFile.EndsWith(".php", StringComparison.OrdinalIgnoreCase))
            {
                return pluginFile[..^4];
            }

            return pluginFile;
        }

        return plugin.Name ?? "(unknown)";
    }

    private static string ResolveThemeSlug(InstalledTheme theme)
    {
        if (!string.IsNullOrWhiteSpace(theme.Slug))
        {
            return theme.Slug!;
        }

        if (!string.IsNullOrWhiteSpace(theme.Stylesheet))
        {
            return theme.Stylesheet!;
        }

        if (!string.IsNullOrWhiteSpace(theme.Template))
        {
            return theme.Template!;
        }

        return theme.Name ?? "(unknown)";
    }

    private static string DetermineUpdateChannel(InstalledPlugin plugin)
    {
        if (!string.IsNullOrWhiteSpace(plugin.UpdateChannel))
        {
            return plugin.UpdateChannel!;
        }

        if (plugin.AutoUpdate.HasValue)
        {
            return plugin.AutoUpdate.Value ? "auto" : "manual";
        }

        if (!string.IsNullOrWhiteSpace(plugin.Update?.Channel))
        {
            return plugin.Update!.Channel!;
        }

        return string.Empty;
    }

    private static string DetermineUpdateChannel(InstalledTheme theme)
    {
        if (!string.IsNullOrWhiteSpace(theme.UpdateChannel))
        {
            return theme.UpdateChannel!;
        }

        if (theme.AutoUpdate.HasValue)
        {
            return theme.AutoUpdate.Value ? "auto" : "manual";
        }

        if (!string.IsNullOrWhiteSpace(theme.Update?.Channel))
        {
            return theme.Update!.Channel!;
        }

        return string.Empty;
    }
}

internal sealed record ManualMigrationReportContext(
    string StoreUrl,
    string StoreIdentifier,
    string OutputFolder,
    bool IsWooCommerce,
    IReadOnlyList<InstalledPlugin> Plugins,
    IReadOnlyList<InstalledTheme> Themes,
    IReadOnlyList<PublicExtensionFootprint> PublicExtensions,
    PublicExtensionDetectionSummary? PublicExtensionDetection,
    string? WordPressVersion,
    string? WooCommerceVersion,
    IReadOnlyList<ExtensionArtifact> PluginBundles,
    IReadOnlyList<ExtensionArtifact> ThemeBundles,
    bool RequestedPluginInventory,
    bool RequestedThemeInventory,
    bool RequestedPublicExtensionFootprints,
    bool AttemptedPluginFetch,
    bool AttemptedThemeFetch,
    bool AttemptedPublicExtensionFootprintFetch,
    FrontEndDesignSnapshotResult? DesignSnapshot,
    IReadOnlyList<DesignScreenshot> DesignScreenshots,
    bool RequestedDesignSnapshot,
    bool RequestedDesignScreenshots,
    bool DesignSnapshotFailed,
    IReadOnlyList<string> MissingCredentialExports,
    IReadOnlyList<string> LogEntries,
    DateTime GeneratedAtUtc,
    bool HttpRetriesEnabled,
    int HttpRetryAttempts,
    TimeSpan HttpRetryBaseDelay,
    TimeSpan HttpRetryMaxDelay);
