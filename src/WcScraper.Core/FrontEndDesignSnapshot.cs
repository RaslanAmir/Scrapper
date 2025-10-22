using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
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

    private static readonly Regex FontFaceBlockRegex = new(
        "@font-face\\s*\\{[^}]*\\}",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LinkTagRegex = new(
        "<link\\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AttributeRegex = new(
        "(?<name>[a-zA-Z0-9:_-]+)\\s*=\\s*(\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^\\s\"'>]+))",
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
            return new FrontEndDesignSnapshotResult(
                homeUrl,
                string.Empty,
                string.Empty,
                Array.Empty<string>(),
                Array.Empty<StylesheetSnapshot>(),
                Array.Empty<FontAssetSnapshot>());
        }

        var inlineStyles = ExtractInlineStyles(html);
        var aggregatedCss = string.Join(Environment.NewLine + Environment.NewLine, inlineStyles);
        var baseUri = TryCreateUri(homeUrl);
        var fontCandidates = new Dictionary<string, FontAssetRequest>(StringComparer.OrdinalIgnoreCase);
        var stylesheetSnapshots = new List<StylesheetSnapshot>();
        var fontSnapshots = new List<FontAssetSnapshot>();

        foreach (var block in inlineStyles)
        {
            foreach (var candidate in ExtractFontUrlCandidates(block, baseUri))
            {
                AddFontCandidate(fontCandidates, candidate, homeUrl);
            }
        }

        var stylesheetHrefs = ExtractStylesheetHrefs(html);
        if (baseUri is not null && stylesheetHrefs.Count > 0)
        {
            var seenStylesheets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var href in stylesheetHrefs)
            {
                var resolved = ResolveAssetUrl(href, baseUri);
                if (string.IsNullOrWhiteSpace(resolved))
                {
                    continue;
                }

                if (!seenStylesheets.Add(resolved))
                {
                    continue;
                }

                try
                {
                    log?.Report($"GET {resolved}");
                    using var cssResponse = await httpClient.GetAsync(resolved, cancellationToken);
                    if (!cssResponse.IsSuccessStatusCode)
                    {
                        log?.Report($"Stylesheet request failed for {resolved}: {(int)cssResponse.StatusCode} {cssResponse.ReasonPhrase}");
                        continue;
                    }

                    var cssBytes = await cssResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                    var cssText = DecodeText(cssBytes, cssResponse.Content.Headers.ContentType?.CharSet);
                    var contentType = cssResponse.Content.Headers.ContentType?.MediaType;

                    var snapshot = new StylesheetSnapshot(href, resolved, cssBytes, cssText, contentType);
                    stylesheetSnapshots.Add(snapshot);

                    var stylesheetUri = TryCreateUri(resolved) ?? baseUri;
                    foreach (var candidate in ExtractFontUrlCandidates(cssText, stylesheetUri))
                    {
                        AddFontCandidate(fontCandidates, candidate, resolved);
                    }
                }
                catch (TaskCanceledException ex)
                {
                    log?.Report($"Stylesheet request canceled for {resolved}: {ex.Message}");
                }
                catch (HttpRequestException ex)
                {
                    log?.Report($"Stylesheet request failed for {resolved}: {ex.Message}");
                }
            }
        }

        foreach (var request in fontCandidates.Values)
        {
            try
            {
                log?.Report($"GET {request.ResolvedUrl}");
                using var fontResponse = await httpClient.GetAsync(request.ResolvedUrl, cancellationToken);
                if (!fontResponse.IsSuccessStatusCode)
                {
                    log?.Report($"Font request failed for {request.ResolvedUrl}: {(int)fontResponse.StatusCode} {fontResponse.ReasonPhrase}");
                    continue;
                }

                var fontBytes = await fontResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                var contentType = fontResponse.Content.Headers.ContentType?.MediaType;
                var fontSnapshot = new FontAssetSnapshot(request.RawUrl, request.ResolvedUrl, request.ReferencedFrom, fontBytes, contentType);
                fontSnapshots.Add(fontSnapshot);
            }
            catch (TaskCanceledException ex)
            {
                log?.Report($"Font request canceled for {request.ResolvedUrl}: {ex.Message}");
            }
            catch (HttpRequestException ex)
            {
                log?.Report($"Font request failed for {request.ResolvedUrl}: {ex.Message}");
            }
        }

        var fontUrls = fontCandidates.Keys.ToList();

        return new FrontEndDesignSnapshotResult(
            homeUrl,
            html,
            aggregatedCss,
            fontUrls,
            stylesheetSnapshots,
            fontSnapshots);
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

    private static IReadOnlyList<string> ExtractStylesheetHrefs(string html)
    {
        var hrefs = new List<string>();
        foreach (Match match in LinkTagRegex.Matches(html))
        {
            var tag = match.Value;
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            string? rel = null;
            string? href = null;

            foreach (Match attr in AttributeRegex.Matches(tag))
            {
                if (!attr.Success) continue;

                var name = attr.Groups["name"].Value;
                var value = attr.Groups["value"].Value;

                if (string.Equals(name, "rel", StringComparison.OrdinalIgnoreCase))
                {
                    rel = value;
                }
                else if (string.Equals(name, "href", StringComparison.OrdinalIgnoreCase))
                {
                    href = value;
                }
            }

            if (string.IsNullOrWhiteSpace(rel) || string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            var relTokens = rel
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(r => r.Trim())
                .Where(r => !string.IsNullOrEmpty(r));

            if (!relTokens.Any(r => string.Equals(r, "stylesheet", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            hrefs.Add(href);
        }

        return hrefs;
    }

    private static IEnumerable<FontUrlCandidate> ExtractFontUrlCandidates(string css, Uri? baseUri)
    {
        if (string.IsNullOrWhiteSpace(css)) yield break;

        foreach (Match block in FontFaceBlockRegex.Matches(css))
        {
            var blockText = block.Value;
            if (string.IsNullOrWhiteSpace(blockText))
            {
                continue;
            }

            foreach (Match match in FontUrlRegex.Matches(blockText))
            {
                if (match.Groups.Count < 3) continue;

                var raw = match.Groups[2].Value?.Trim();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

                var resolved = ResolveAssetUrl(raw, baseUri);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    yield return new FontUrlCandidate(raw, resolved);
                }
            }
        }
    }

    private static string? ResolveAssetUrl(string candidate, Uri? baseUri)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        if (candidate.StartsWith("//", StringComparison.Ordinal))
        {
            if (baseUri is null)
            {
                return "https:" + candidate;
            }

            return baseUri.Scheme + ":" + candidate;
        }

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        if (baseUri is not null && Uri.TryCreate(baseUri, candidate, out var combined))
        {
            return combined.ToString();
        }

        return null;
    }

    private static Uri? TryCreateUri(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    private static void AddFontCandidate(
        IDictionary<string, FontAssetRequest> accumulator,
        FontUrlCandidate candidate,
        string referencedFrom)
    {
        if (!accumulator.ContainsKey(candidate.ResolvedUrl))
        {
            accumulator[candidate.ResolvedUrl] = new FontAssetRequest(candidate.RawUrl, candidate.ResolvedUrl, referencedFrom);
        }
    }

    private static string DecodeText(byte[] contentBytes, string? charset)
    {
        if (contentBytes.Length == 0)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                var encoding = Encoding.GetEncoding(charset);
                return encoding.GetString(contentBytes);
            }
            catch (ArgumentException)
            {
            }
        }

        return Encoding.UTF8.GetString(contentBytes);
    }

    private readonly record struct FontUrlCandidate(string RawUrl, string ResolvedUrl);

    private sealed record FontAssetRequest(string RawUrl, string ResolvedUrl, string ReferencedFrom);
}

public sealed class FrontEndDesignSnapshotResult
{
    public FrontEndDesignSnapshotResult(
        string homeUrl,
        string rawHtml,
        string inlineCss,
        IReadOnlyList<string> fontUrls,
        IReadOnlyList<StylesheetSnapshot> stylesheets,
        IReadOnlyList<FontAssetSnapshot> fontFiles)
    {
        HomeUrl = homeUrl;
        RawHtml = rawHtml;
        InlineCss = inlineCss;
        FontUrls = fontUrls ?? Array.Empty<string>();
        Stylesheets = stylesheets ?? Array.Empty<StylesheetSnapshot>();
        FontFiles = fontFiles ?? Array.Empty<FontAssetSnapshot>();
    }

    public string HomeUrl { get; }

    public string RawHtml { get; }

    public string InlineCss { get; }

    public IReadOnlyList<string> FontUrls { get; }

    public IReadOnlyList<StylesheetSnapshot> Stylesheets { get; }

    public IReadOnlyList<FontAssetSnapshot> FontFiles { get; }
}

public sealed class StylesheetSnapshot
{
    public StylesheetSnapshot(
        string sourceUrl,
        string resolvedUrl,
        byte[] content,
        string textContent,
        string? contentType)
    {
        SourceUrl = sourceUrl ?? string.Empty;
        ResolvedUrl = resolvedUrl ?? string.Empty;
        Content = content ?? Array.Empty<byte>();
        TextContent = textContent ?? string.Empty;
        ContentType = contentType;
    }

    public string SourceUrl { get; }

    public string ResolvedUrl { get; }

    public byte[] Content { get; }

    public string TextContent { get; }

    public string? ContentType { get; }
}

public sealed class FontAssetSnapshot
{
    public FontAssetSnapshot(
        string sourceUrl,
        string resolvedUrl,
        string referencedFrom,
        byte[] content,
        string? contentType)
    {
        SourceUrl = sourceUrl ?? string.Empty;
        ResolvedUrl = resolvedUrl ?? string.Empty;
        ReferencedFrom = referencedFrom ?? string.Empty;
        Content = content ?? Array.Empty<byte>();
        ContentType = contentType;
    }

    public string SourceUrl { get; }

    public string ResolvedUrl { get; }

    public string ReferencedFrom { get; }

    public byte[] Content { get; }

    public string? ContentType { get; }
}
