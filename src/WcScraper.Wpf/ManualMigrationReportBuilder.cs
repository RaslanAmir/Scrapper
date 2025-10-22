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

        builder.AppendLine("| Type | Slug | Directory Title | Version | Homepage | Status |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- |");

        foreach (var footprint in context.PublicExtensions
            .OrderBy(f => f.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Slug, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| {MarkdownEscape(footprint.Type)} | {MarkdownEscape(footprint.Slug)} | {MarkdownEscape(footprint.DirectoryTitle)} | {MarkdownEscape(footprint.DirectoryVersion)} | {MarkdownEscape(footprint.DirectoryHomepage)} | {MarkdownEscape(footprint.DirectoryStatus)} |");
        }
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
    DateTime GeneratedAtUtc);
