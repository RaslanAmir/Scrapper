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

    private static readonly Regex CssUrlTokenRegex = new(
        "url\\((['\"\\]?)([^'\"\\)]+)\\1\\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CssImportRegex = new(
        "@import\\s+(?:url\\(\\s*(?:['\"]?)(?<url>[^'\"\\)\\s]+)['\"]?\\s*\\)|['\"](?<url>[^'\"]+)['\"])(?:[^;]*);",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex LinkTagRegex = new(
        "<link\\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AttributeRegex = new(
        "(?<name>[a-zA-Z0-9:_-]+)\\s*=\\s*(\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^\\s\"'>]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HexColorRegex = new(
        "#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})\\b",
        RegexOptions.Compiled);

    private static readonly Regex RgbColorRegex = new(
        "(?<func>rgba?)\\s*\\((?<args>[^)]*)\\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CssCustomPropertyRegex = new(
        "--[a-zA-Z0-9_-]+",
        RegexOptions.Compiled);

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
                Array.Empty<FontAssetSnapshot>(),
                Array.Empty<DesignImageSnapshot>(),
                Array.Empty<ColorSwatch>());
        }

        var inlineStyles = ExtractInlineStyles(html);
        var inlineCss = string.Join(Environment.NewLine + Environment.NewLine, inlineStyles);
        var cssSources = new List<string>();
        cssSources.AddRange(inlineStyles);
        var baseUri = TryCreateUri(homeUrl);
        var fontCandidates = new Dictionary<string, FontAssetRequest>(StringComparer.OrdinalIgnoreCase);
        var stylesheetSnapshots = new List<StylesheetSnapshot>();
        var fontSnapshots = new List<FontAssetSnapshot>();
        var imageCandidates = new Dictionary<string, ImageAssetRequest>(StringComparer.OrdinalIgnoreCase);
        var imageSnapshots = new List<DesignImageSnapshot>();

        foreach (var block in inlineStyles)
        {
            foreach (var candidate in ExtractFontUrlCandidates(block, baseUri))
            {
                AddFontCandidate(fontCandidates, candidate, homeUrl);
            }

            foreach (var candidate in ExtractImageUrlCandidates(block, baseUri, fontCandidates))
            {
                AddImageCandidate(imageCandidates, candidate, homeUrl);
            }
        }

        var stylesheetHrefs = ExtractStylesheetHrefs(html);
        if (baseUri is not null && stylesheetHrefs.Count > 0)
        {
            var seenStylesheets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stylesheetQueue = new Queue<StylesheetRequest>();

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

                stylesheetQueue.Enqueue(new StylesheetRequest(href, resolved, homeUrl));
            }

            while (stylesheetQueue.Count > 0)
            {
                var request = stylesheetQueue.Dequeue();

                try
                {
                    log?.Report($"GET {request.ResolvedUrl}");
                    using var cssResponse = await httpClient.GetAsync(request.ResolvedUrl, cancellationToken);
                    if (!cssResponse.IsSuccessStatusCode)
                    {
                        log?.Report($"Stylesheet request failed for {request.ResolvedUrl}: {(int)cssResponse.StatusCode} {cssResponse.ReasonPhrase}");
                        continue;
                    }

                    var cssBytes = await cssResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                    var cssText = DecodeText(cssBytes, cssResponse.Content.Headers.ContentType?.CharSet);
                    var contentType = cssResponse.Content.Headers.ContentType?.MediaType;

                    var snapshot = new StylesheetSnapshot(request.SourceUrl, request.ResolvedUrl, request.ReferencedFrom, cssBytes, cssText, contentType);
                    stylesheetSnapshots.Add(snapshot);

                    if (!string.IsNullOrWhiteSpace(cssText))
                    {
                        cssSources.Add(cssText);
                    }

                    var stylesheetUri = TryCreateUri(request.ResolvedUrl) ?? baseUri;
                    foreach (var importCandidate in ExtractCssImportUrls(cssText))
                    {
                        var resolvedImport = ResolveAssetUrl(importCandidate, stylesheetUri);
                        if (string.IsNullOrWhiteSpace(resolvedImport))
                        {
                            continue;
                        }

                        if (!seenStylesheets.Add(resolvedImport))
                        {
                            continue;
                        }

                        stylesheetQueue.Enqueue(new StylesheetRequest(importCandidate, resolvedImport, request.ResolvedUrl));
                    }

                    foreach (var candidate in ExtractFontUrlCandidates(cssText, stylesheetUri))
                    {
                        AddFontCandidate(fontCandidates, candidate, request.ResolvedUrl);
                    }

                    foreach (var candidate in ExtractImageUrlCandidates(cssText, stylesheetUri, fontCandidates))
                    {
                        AddImageCandidate(imageCandidates, candidate, request.ResolvedUrl);
                    }
                }
                catch (TaskCanceledException ex)
                {
                    log?.Report($"Stylesheet request canceled for {request.ResolvedUrl}: {ex.Message}");
                }
                catch (HttpRequestException ex)
                {
                    log?.Report($"Stylesheet request failed for {request.ResolvedUrl}: {ex.Message}");
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

        foreach (var request in imageCandidates.Values)
        {
            try
            {
                log?.Report($"GET {request.ResolvedUrl}");
                using var imageResponse = await httpClient.GetAsync(request.ResolvedUrl, cancellationToken);
                if (!imageResponse.IsSuccessStatusCode)
                {
                    log?.Report($"Image request failed for {request.ResolvedUrl}: {(int)imageResponse.StatusCode} {imageResponse.ReasonPhrase}");
                    continue;
                }

                var imageBytes = await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                var contentType = imageResponse.Content.Headers.ContentType?.MediaType;
                var imageSnapshot = new DesignImageSnapshot(request.RawUrl, request.ResolvedUrl, request.ReferencedFrom, imageBytes, contentType);
                imageSnapshots.Add(imageSnapshot);
            }
            catch (TaskCanceledException ex)
            {
                log?.Report($"Image request canceled for {request.ResolvedUrl}: {ex.Message}");
            }
            catch (HttpRequestException ex)
            {
                log?.Report($"Image request failed for {request.ResolvedUrl}: {ex.Message}");
            }
        }

        var fontUrls = fontCandidates.Keys.ToList();

        var aggregatedCss = string.Join(
            Environment.NewLine + Environment.NewLine,
            cssSources.Where(s => !string.IsNullOrWhiteSpace(s)));

        var colorSwatches = ExtractColorSwatches(aggregatedCss);

        return new FrontEndDesignSnapshotResult(
            homeUrl,
            html,
            inlineCss,
            fontUrls,
            stylesheetSnapshots,
            fontSnapshots,
            imageSnapshots,
            colorSwatches);
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

    private static IEnumerable<ImageUrlCandidate> ExtractImageUrlCandidates(
        string css,
        Uri? baseUri,
        IReadOnlyDictionary<string, FontAssetRequest> knownFontRequests)
    {
        if (string.IsNullOrWhiteSpace(css)) yield break;

        var sanitized = FontFaceBlockRegex.Replace(css, string.Empty);
        if (string.IsNullOrWhiteSpace(sanitized)) yield break;

        foreach (Match match in CssUrlTokenRegex.Matches(sanitized))
        {
            if (match.Groups.Count < 3) continue;

            var raw = match.Groups[2].Value?.Trim();
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            if (raw.StartsWith("#", StringComparison.Ordinal)) continue;
            if (raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
            if (raw.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) continue;
            if (raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;

            var resolved = ResolveAssetUrl(raw, baseUri);
            if (string.IsNullOrWhiteSpace(resolved)) continue;
            if (knownFontRequests.ContainsKey(resolved)) continue;

            yield return new ImageUrlCandidate(raw, resolved);
        }
    }

    private static IEnumerable<string> ExtractCssImportUrls(string css)
    {
        if (string.IsNullOrWhiteSpace(css)) yield break;

        foreach (Match match in CssImportRegex.Matches(css))
        {
            var value = match.Groups["url"].Value?.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return value;
        }
    }

    private static void AddImageCandidate(
        IDictionary<string, ImageAssetRequest> accumulator,
        ImageUrlCandidate candidate,
        string referencedFrom)
    {
        if (!accumulator.ContainsKey(candidate.ResolvedUrl))
        {
            accumulator[candidate.ResolvedUrl] = new ImageAssetRequest(candidate.RawUrl, candidate.ResolvedUrl, referencedFrom);
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

    private static IReadOnlyList<ColorSwatch> ExtractColorSwatches(string aggregatedCss)
    {
        if (string.IsNullOrWhiteSpace(aggregatedCss))
        {
            return Array.Empty<ColorSwatch>();
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void Increment(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            if (counts.ContainsKey(key))
            {
                counts[key]++;
            }
            else
            {
                counts[key] = 1;
            }
        }

        foreach (Match match in HexColorRegex.Matches(aggregatedCss))
        {
            if (!match.Success) continue;
            var normalized = NormalizeHexColor(match.Value);
            if (!string.IsNullOrEmpty(normalized))
            {
                Increment(normalized);
            }
        }

        foreach (Match match in RgbColorRegex.Matches(aggregatedCss))
        {
            if (!match.Success) continue;
            var normalized = NormalizeRgbColor(match);
            if (!string.IsNullOrEmpty(normalized))
            {
                Increment(normalized);
            }
        }

        foreach (Match match in CssCustomPropertyRegex.Matches(aggregatedCss))
        {
            if (!match.Success) continue;
            var normalized = NormalizeCssCustomProperty(match.Value);
            if (!string.IsNullOrEmpty(normalized))
            {
                Increment(normalized);
            }
        }

        if (counts.Count == 0)
        {
            return Array.Empty<ColorSwatch>();
        }

        return counts
            .Select(kvp => new ColorSwatch(kvp.Key, kvp.Value))
            .OrderByDescending(s => s.Count)
            .ThenBy(s => s.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? NormalizeHexColor(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var hex = value.Trim();
        if (!hex.StartsWith('#'))
        {
            return null;
        }

        hex = hex[1..];
        if (hex.Length == 3)
        {
            var builder = new StringBuilder(6);
            foreach (var c in hex)
            {
                var upper = char.ToUpperInvariant(c);
                builder.Append(upper);
                builder.Append(upper);
            }

            return "#" + builder.ToString();
        }

        if (hex.Length == 6 || hex.Length == 8)
        {
            return "#" + hex.ToUpperInvariant();
        }

        return null;
    }

    private static string? NormalizeRgbColor(Match match)
    {
        var func = match.Groups["func"].Value;
        var args = match.Groups["args"].Value;
        if (string.IsNullOrWhiteSpace(func) || string.IsNullOrWhiteSpace(args))
        {
            return null;
        }

        var normalizedArgs = args.Trim();
        normalizedArgs = Regex.Replace(normalizedArgs, "\\s*,\\s*", ", ");
        normalizedArgs = Regex.Replace(normalizedArgs, "\\s*/\\s*", " / ");
        normalizedArgs = Regex.Replace(normalizedArgs, "\\s+", " ");
        normalizedArgs = normalizedArgs.Replace("( ", "(").Replace(" )", ")");

        var normalizedFunc = func.ToLowerInvariant();
        if (normalizedFunc != "rgb" && normalizedFunc != "rgba")
        {
            return null;
        }

        return $"{normalizedFunc}({normalizedArgs})";
    }

    private static string? NormalizeCssCustomProperty(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.StartsWith("--", StringComparison.Ordinal) ? trimmed : null;
    }

    private readonly record struct FontUrlCandidate(string RawUrl, string ResolvedUrl);

    private readonly record struct ImageUrlCandidate(string RawUrl, string ResolvedUrl);

    private sealed record FontAssetRequest(string RawUrl, string ResolvedUrl, string ReferencedFrom);

    private sealed record ImageAssetRequest(string RawUrl, string ResolvedUrl, string ReferencedFrom);

    private sealed record StylesheetRequest(string SourceUrl, string ResolvedUrl, string ReferencedFrom);
}

public sealed class FrontEndDesignSnapshotResult
{
    public FrontEndDesignSnapshotResult(
        string homeUrl,
        string rawHtml,
        string inlineCss,
        IReadOnlyList<string> fontUrls,
        IReadOnlyList<StylesheetSnapshot> stylesheets,
        IReadOnlyList<FontAssetSnapshot> fontFiles,
        IReadOnlyList<DesignImageSnapshot> imageFiles,
        IReadOnlyList<ColorSwatch> colorSwatches)
    {
        HomeUrl = homeUrl;
        RawHtml = rawHtml;
        InlineCss = inlineCss;
        FontUrls = fontUrls ?? Array.Empty<string>();
        Stylesheets = stylesheets ?? Array.Empty<StylesheetSnapshot>();
        FontFiles = fontFiles ?? Array.Empty<FontAssetSnapshot>();
        ImageFiles = imageFiles ?? Array.Empty<DesignImageSnapshot>();
        ColorSwatches = colorSwatches ?? Array.Empty<ColorSwatch>();
    }

    public string HomeUrl { get; }

    public string RawHtml { get; }

    public string InlineCss { get; }

    public IReadOnlyList<string> FontUrls { get; }

    public IReadOnlyList<StylesheetSnapshot> Stylesheets { get; }

    public IReadOnlyList<FontAssetSnapshot> FontFiles { get; }

    public IReadOnlyList<DesignImageSnapshot> ImageFiles { get; }

    public IReadOnlyList<ColorSwatch> ColorSwatches { get; }
}

public sealed class StylesheetSnapshot
{
    public StylesheetSnapshot(
        string sourceUrl,
        string resolvedUrl,
        string referencedFrom,
        byte[] content,
        string textContent,
        string? contentType)
    {
        SourceUrl = sourceUrl ?? string.Empty;
        ResolvedUrl = resolvedUrl ?? string.Empty;
        ReferencedFrom = referencedFrom ?? string.Empty;
        Content = content ?? Array.Empty<byte>();
        TextContent = textContent ?? string.Empty;
        ContentType = contentType;
    }

    public string SourceUrl { get; }

    public string ResolvedUrl { get; }

    public string ReferencedFrom { get; }

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

public sealed class DesignImageSnapshot
{
    public DesignImageSnapshot(
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

public sealed record ColorSwatch(string Value, int Count);
