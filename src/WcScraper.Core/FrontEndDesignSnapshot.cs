using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WcScraper.Core;

public static class FrontEndDesignSnapshot
{
    private static readonly Regex StyleBlockRegex = new(
        "<style\\b[^>]*>(.*?)</style>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FontUrlRegex = new(
        "url\\((['\"\\]?)([^'\"\\)]+)\\1\\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<FrontEndDesignSnapshotResult> CaptureAsync(
        HttpClient httpClient,
        string baseUrl,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (httpClient is null) throw new ArgumentNullException(nameof(httpClient));
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentException("Base URL is required.", nameof(baseUrl));

        baseUrl = WooScraper.CleanBaseUrl(baseUrl);
        var homeUrl = baseUrl + "/";
        log?.Report($"GET {homeUrl}");

        using var response = await httpClient.GetAsync(homeUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
        {
            return new FrontEndDesignSnapshotResult(homeUrl, string.Empty, string.Empty, Array.Empty<string>());
        }

        var inlineStyles = ExtractInlineStyles(html);
        var aggregatedCss = string.Join(Environment.NewLine + Environment.NewLine, inlineStyles);
        var baseUri = TryCreateUri(homeUrl);
        var fontUrls = baseUri is null
            ? Array.Empty<string>()
            : ExtractFontUrls(inlineStyles, baseUri);

        return new FrontEndDesignSnapshotResult(homeUrl, html, aggregatedCss, fontUrls);
    }

    private static IReadOnlyList<string> ExtractInlineStyles(string html)
    {
        var styles = new List<string>();
        foreach (Match match in StyleBlockRegex.Matches(html))
        {
            var content = match.Groups.Count > 1 ? match.Groups[1].Value : string.Empty;
            if (!string.IsNullOrWhiteSpace(content))
            {
                styles.Add(content.Trim());
            }
        }

        return styles;
    }

    private static IReadOnlyList<string> ExtractFontUrls(IEnumerable<string> inlineStyles, Uri baseUri)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var block in inlineStyles)
        {
            foreach (Match match in FontUrlRegex.Matches(block))
            {
                if (match.Groups.Count < 3) continue;
                var raw = match.Groups[2].Value?.Trim();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

                var resolved = ResolveAssetUrl(raw, baseUri);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    urls.Add(resolved);
                }
            }
        }

        return urls.ToList();
    }

    private static string? ResolveAssetUrl(string candidate, Uri baseUri)
    {
        if (candidate.StartsWith("//"))
        {
            return baseUri.Scheme + ":" + candidate;
        }

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        if (Uri.TryCreate(baseUri, candidate, out var combined))
        {
            return combined.ToString();
        }

        return null;
    }

    private static Uri? TryCreateUri(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
}

public sealed class FrontEndDesignSnapshotResult
{
    public FrontEndDesignSnapshotResult(string homeUrl, string rawHtml, string inlineCss, IReadOnlyList<string> fontUrls)
    {
        HomeUrl = homeUrl;
        RawHtml = rawHtml;
        InlineCss = inlineCss;
        FontUrls = fontUrls ?? Array.Empty<string>();
    }

    public string HomeUrl { get; }

    public string RawHtml { get; }

    public string InlineCss { get; }

    public IReadOnlyList<string> FontUrls { get; }
}
