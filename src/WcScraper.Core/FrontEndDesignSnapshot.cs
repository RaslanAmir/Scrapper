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

    private static readonly Regex FontFacePropertyRegex = new(
        @"(?<name>font-family|font-style|font-weight)\\s*:\\s*(?<value>[^;{}]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
        IEnumerable<string>? additionalPageUrls = null,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (httpClient is null) throw new ArgumentNullException(nameof(httpClient));
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentException("Base URL is required.", nameof(baseUrl));

        baseUrl = WooScraper.CleanBaseUrl(baseUrl);
        var homeUrl = baseUrl + "/";

        var pageUrls = BuildPageUrlList(homeUrl, additionalPageUrls);
        var processedUrls = new List<string>(pageUrls.Count);
        var pageBuilders = new List<DesignPageSnapshotBuilder>(pageUrls.Count);
        var aggregatedHtml = new StringBuilder();
        var aggregatedCssSources = new List<string>();

        var stylesheetLookup = new Dictionary<string, StylesheetSnapshot>(StringComparer.OrdinalIgnoreCase);
        var aggregatedStylesheets = new List<StylesheetSnapshot>();

        var fontCandidates = new Dictionary<string, FontAssetRequest>(StringComparer.OrdinalIgnoreCase);
        var fontCandidateOrder = new List<string>();
        var imageCandidates = new Dictionary<string, ImageAssetRequest>(StringComparer.OrdinalIgnoreCase);
        var imageCandidateOrder = new List<string>();

        foreach (var pageUrl in pageUrls)
        {
            var builder = await CapturePageAsync(
                httpClient,
                pageUrl,
                aggregatedStylesheets,
                stylesheetLookup,
                fontCandidates,
                fontCandidateOrder,
                imageCandidates,
                imageCandidateOrder,
                aggregatedCssSources,
                log,
                cancellationToken);

            pageBuilders.Add(builder);
            processedUrls.Add(pageUrl);

            if (aggregatedHtml.Length > 0)
            {
                aggregatedHtml.AppendLine().AppendLine();
            }

            aggregatedHtml.Append("<!-- Snapshot: ")
                .Append(pageUrl)
                .AppendLine(" -->");
            aggregatedHtml.Append(builder.RawHtml);
        }

        var fontSnapshotsMap = new Dictionary<string, FontAssetSnapshot>(StringComparer.OrdinalIgnoreCase);
        var fontSnapshots = new List<FontAssetSnapshot>();

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
                var fontSnapshot = new FontAssetSnapshot(
                    request.RawUrl,
                    request.ResolvedUrl,
                    request.ReferencedFrom,
                    fontBytes,
                    contentType,
                    request.FontFamily,
                    request.FontStyle,
                    request.FontWeight);
                fontSnapshotsMap[request.ResolvedUrl] = fontSnapshot;
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

        var imageSnapshotsMap = new Dictionary<string, DesignImageSnapshot>(StringComparer.OrdinalIgnoreCase);
        var imageSnapshots = new List<DesignImageSnapshot>();

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
                imageSnapshotsMap[request.ResolvedUrl] = imageSnapshot;
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

        var aggregatedInlineCss = string.Join(
            Environment.NewLine + Environment.NewLine,
            pageBuilders
                .Select(p => p.InlineCss)
                .Where(s => !string.IsNullOrWhiteSpace(s)));

        var aggregatedCss = string.Join(
            Environment.NewLine + Environment.NewLine,
            aggregatedCssSources.Where(s => !string.IsNullOrWhiteSpace(s)));

        var colorSwatches = ExtractColorSwatches(aggregatedCss);

        var pages = pageBuilders
            .Select(builder => builder.Build(fontSnapshotsMap, imageSnapshotsMap))
            .ToList();

        return new FrontEndDesignSnapshotResult(
            homeUrl,
            aggregatedHtml.ToString(),
            aggregatedInlineCss,
            fontCandidateOrder,
            aggregatedStylesheets,
            fontSnapshots,
            imageSnapshots,
            colorSwatches,
            pages,
            processedUrls);
    }

    private static List<string> BuildPageUrlList(string homeUrl, IEnumerable<string>? additionalPageUrls)
    {
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        urls.Add(homeUrl);
        seen.Add(homeUrl);

        if (additionalPageUrls is null)
        {
            return urls;
        }

        var baseUri = TryCreateUri(homeUrl);

        foreach (var candidate in additionalPageUrls)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var resolved = ResolveAssetUrl(candidate.Trim(), baseUri);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                continue;
            }

            if (!seen.Add(resolved))
            {
                continue;
            }

            urls.Add(resolved);
        }

        return urls;
    }

    private static async Task<DesignPageSnapshotBuilder> CapturePageAsync(
        HttpClient httpClient,
        string pageUrl,
        List<StylesheetSnapshot> aggregatedStylesheets,
        Dictionary<string, StylesheetSnapshot> stylesheetLookup,
        Dictionary<string, FontAssetRequest> fontCandidates,
        List<string> fontCandidateOrder,
        Dictionary<string, ImageAssetRequest> imageCandidates,
        List<string> imageCandidateOrder,
        List<string> aggregatedCssSources,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var builder = new DesignPageSnapshotBuilder(pageUrl);

        log?.Report($"GET {pageUrl}");
        using var response = await httpClient.GetAsync(pageUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(cancellationToken) ?? string.Empty;
        builder.RawHtml = html;

        if (string.IsNullOrWhiteSpace(html))
        {
            return builder;
        }

        var pageUri = TryCreateUri(pageUrl);
        var inlineStyles = ExtractInlineStyles(html);
        builder.InlineCss = string.Join(Environment.NewLine + Environment.NewLine, inlineStyles);

        foreach (var block in inlineStyles)
        {
            builder.AddCssSource(block);
            if (!string.IsNullOrWhiteSpace(block))
            {
                aggregatedCssSources.Add(block);
            }

            foreach (var candidate in ExtractFontUrlCandidates(block, pageUri))
            {
                if (AddFontCandidate(fontCandidates, candidate, pageUrl))
                {
                    fontCandidateOrder.Add(candidate.ResolvedUrl);
                }

                builder.AddFontUrl(candidate.ResolvedUrl);
            }

            foreach (var candidate in ExtractImageUrlCandidates(block, pageUri, fontCandidates))
            {
                if (AddImageCandidate(imageCandidates, candidate, pageUrl))
                {
                    imageCandidateOrder.Add(candidate.ResolvedUrl);
                }

                builder.AddImageUrl(candidate.ResolvedUrl);
            }
        }

        var stylesheetHrefs = ExtractStylesheetHrefs(html);
        if (stylesheetHrefs.Count == 0)
        {
            return builder;
        }

        var seenForQueue = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stylesheetQueue = new Queue<StylesheetRequest>();

        foreach (var href in stylesheetHrefs)
        {
            var resolved = ResolveAssetUrl(href, pageUri);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                continue;
            }

            if (!seenForQueue.Add(resolved))
            {
                continue;
            }

            stylesheetQueue.Enqueue(new StylesheetRequest(href, resolved, pageUrl));
        }

        while (stylesheetQueue.Count > 0)
        {
            var request = stylesheetQueue.Dequeue();

            if (stylesheetLookup.TryGetValue(request.ResolvedUrl, out var knownSnapshot))
            {
                builder.AddStylesheet(knownSnapshot);
                var knownUri = TryCreateUri(knownSnapshot.ResolvedUrl) ?? pageUri;
                ProcessStylesheetContent(
                    knownSnapshot.TextContent,
                    knownUri,
                    request.ResolvedUrl,
                    builder,
                    fontCandidates,
                    fontCandidateOrder,
                    imageCandidates,
                    imageCandidateOrder);
                EnqueueStylesheetImports(
                    knownSnapshot.TextContent,
                    knownUri,
                    request.ResolvedUrl,
                    stylesheetQueue,
                    seenForQueue);
                continue;
            }

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
                aggregatedStylesheets.Add(snapshot);
                stylesheetLookup[request.ResolvedUrl] = snapshot;
                builder.AddStylesheet(snapshot);

                if (!string.IsNullOrWhiteSpace(cssText))
                {
                    aggregatedCssSources.Add(cssText);
                }

                var stylesheetUri = TryCreateUri(request.ResolvedUrl) ?? pageUri;
                ProcessStylesheetContent(
                    cssText,
                    stylesheetUri,
                    request.ResolvedUrl,
                    builder,
                    fontCandidates,
                    fontCandidateOrder,
                    imageCandidates,
                    imageCandidateOrder);
                EnqueueStylesheetImports(
                    cssText,
                    stylesheetUri,
                    request.ResolvedUrl,
                    stylesheetQueue,
                    seenForQueue);
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

        return builder;
    }

    private static void ProcessStylesheetContent(
        string cssText,
        Uri? stylesheetUri,
        string referencedFrom,
        DesignPageSnapshotBuilder builder,
        Dictionary<string, FontAssetRequest> fontCandidates,
        List<string> fontCandidateOrder,
        Dictionary<string, ImageAssetRequest> imageCandidates,
        List<string> imageCandidateOrder)
    {
        if (string.IsNullOrWhiteSpace(cssText))
        {
            return;
        }

        foreach (var candidate in ExtractFontUrlCandidates(cssText, stylesheetUri))
        {
            if (AddFontCandidate(fontCandidates, candidate, referencedFrom))
            {
                fontCandidateOrder.Add(candidate.ResolvedUrl);
            }

            builder.AddFontUrl(candidate.ResolvedUrl);
        }

        foreach (var candidate in ExtractImageUrlCandidates(cssText, stylesheetUri, fontCandidates))
        {
            if (AddImageCandidate(imageCandidates, candidate, referencedFrom))
            {
                imageCandidateOrder.Add(candidate.ResolvedUrl);
            }

            builder.AddImageUrl(candidate.ResolvedUrl);
        }

        builder.AddCssSource(cssText);
    }

    private static void EnqueueStylesheetImports(
        string cssText,
        Uri? stylesheetUri,
        string referencedFrom,
        Queue<StylesheetRequest> queue,
        HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(cssText))
        {
            return;
        }

        foreach (var importCandidate in ExtractCssImportUrls(cssText))
        {
            var resolvedImport = ResolveAssetUrl(importCandidate, stylesheetUri);
            if (string.IsNullOrWhiteSpace(resolvedImport))
            {
                continue;
            }

            if (!seen.Add(resolvedImport))
            {
                continue;
            }

            queue.Enqueue(new StylesheetRequest(importCandidate, resolvedImport, referencedFrom));
        }
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

            var metadata = ExtractFontFaceMetadata(blockText);

            foreach (Match match in FontUrlRegex.Matches(blockText))
            {
                if (match.Groups.Count < 3) continue;

                var raw = match.Groups[2].Value?.Trim();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

                var resolved = ResolveAssetUrl(raw, baseUri);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    yield return new FontUrlCandidate(raw, resolved, metadata.FontFamily, metadata.FontStyle, metadata.FontWeight);
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

    private static bool AddFontCandidate(
        IDictionary<string, FontAssetRequest> accumulator,
        FontUrlCandidate candidate,
        string referencedFrom)
    {
        if (accumulator.TryGetValue(candidate.ResolvedUrl, out var existing))
        {
            existing.MergeMetadata(candidate);
            return false;
        }

        accumulator[candidate.ResolvedUrl] = new FontAssetRequest(
            candidate.RawUrl,
            candidate.ResolvedUrl,
            referencedFrom,
            candidate.FontFamily,
            candidate.FontStyle,
            candidate.FontWeight);
        return true;
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

    private static bool AddImageCandidate(
        IDictionary<string, ImageAssetRequest> accumulator,
        ImageUrlCandidate candidate,
        string referencedFrom)
    {
        if (accumulator.ContainsKey(candidate.ResolvedUrl))
        {
            return false;
        }

        accumulator[candidate.ResolvedUrl] = new ImageAssetRequest(candidate.RawUrl, candidate.ResolvedUrl, referencedFrom);
        return true;
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

    private sealed class DesignPageSnapshotBuilder
    {
        private readonly HashSet<string> _stylesheetUrls = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _fontUrls = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _imageUrls = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _cssSources = new();
        private readonly List<StylesheetSnapshot> _stylesheets = new();
        private readonly List<string> _fontUrlList = new();
        private readonly List<string> _imageUrlList = new();

        public DesignPageSnapshotBuilder(string url)
        {
            Url = url ?? string.Empty;
        }

        public string Url { get; }

        public string RawHtml { get; set; } = string.Empty;

        public string InlineCss { get; set; } = string.Empty;

        public void AddCssSource(string? css)
        {
            if (string.IsNullOrWhiteSpace(css))
            {
                return;
            }

            _cssSources.Add(css);
        }

        public void AddStylesheet(StylesheetSnapshot snapshot)
        {
            if (snapshot is null)
            {
                return;
            }

            if (_stylesheetUrls.Add(snapshot.ResolvedUrl))
            {
                _stylesheets.Add(snapshot);
            }

            AddCssSource(snapshot.TextContent);
        }

        public void AddFontUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            if (_fontUrls.Add(url))
            {
                _fontUrlList.Add(url);
            }
        }

        public void AddImageUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            if (_imageUrls.Add(url))
            {
                _imageUrlList.Add(url);
            }
        }

        public DesignPageSnapshot Build(
            IReadOnlyDictionary<string, FontAssetSnapshot> fontSnapshots,
            IReadOnlyDictionary<string, DesignImageSnapshot> imageSnapshots)
        {
            var fontFiles = _fontUrlList
                .Select(url => fontSnapshots.TryGetValue(url, out var snapshot) ? snapshot : null)
                .Where(snapshot => snapshot is not null)
                .Cast<FontAssetSnapshot>()
                .ToList();

            var imageFiles = _imageUrlList
                .Select(url => imageSnapshots.TryGetValue(url, out var snapshot) ? snapshot : null)
                .Where(snapshot => snapshot is not null)
                .Cast<DesignImageSnapshot>()
                .ToList();

            var cssAggregate = string.Join(
                Environment.NewLine + Environment.NewLine,
                _cssSources.Where(s => !string.IsNullOrWhiteSpace(s)));

            var colorSwatches = ExtractColorSwatches(cssAggregate);

            return new DesignPageSnapshot(
                Url,
                RawHtml,
                InlineCss,
                _fontUrlList,
                _stylesheets,
                fontFiles,
                imageFiles,
                colorSwatches);
        }
    }

    private static FontFaceMetadata ExtractFontFaceMetadata(string blockText)
    {
        if (string.IsNullOrWhiteSpace(blockText))
        {
            return default;
        }

        string? fontFamily = null;
        string? fontStyle = null;
        string? fontWeight = null;

        foreach (Match property in FontFacePropertyRegex.Matches(blockText))
        {
            if (!property.Success)
            {
                continue;
            }

            var name = property.Groups["name"].Value;
            var value = NormalizeFontFaceValue(property.Groups["value"].Value);

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (fontFamily is null && string.Equals(name, "font-family", StringComparison.OrdinalIgnoreCase))
            {
                fontFamily = value;
            }
            else if (fontStyle is null && string.Equals(name, "font-style", StringComparison.OrdinalIgnoreCase))
            {
                fontStyle = value;
            }
            else if (fontWeight is null && string.Equals(name, "font-weight", StringComparison.OrdinalIgnoreCase))
            {
                fontWeight = value;
            }
        }

        return new FontFaceMetadata(fontFamily, fontStyle, fontWeight);
    }

    private static string? NormalizeFontFaceValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        if (trimmed.EndsWith(";", StringComparison.Ordinal))
        {
            trimmed = trimmed.TrimEnd(';');
        }

        var importantIndex = trimmed.IndexOf("!important", StringComparison.OrdinalIgnoreCase);
        if (importantIndex >= 0)
        {
            trimmed = trimmed[..importantIndex];
        }

        trimmed = trimmed.Trim();

        if (trimmed.Length >= 2)
        {
            var startsWithQuote = trimmed[0] == '\"' || trimmed[0] == '\'';
            var endsWithQuote = trimmed[^1] == '\"' || trimmed[^1] == '\'';
            if (startsWithQuote && endsWithQuote)
            {
                trimmed = trimmed[1..^1];
            }
        }

        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private readonly record struct FontFaceMetadata(string? FontFamily, string? FontStyle, string? FontWeight);

    private readonly record struct FontUrlCandidate(string RawUrl, string ResolvedUrl, string? FontFamily, string? FontStyle, string? FontWeight);

    private readonly record struct ImageUrlCandidate(string RawUrl, string ResolvedUrl);

    private sealed class FontAssetRequest
    {
        public FontAssetRequest(
            string rawUrl,
            string resolvedUrl,
            string referencedFrom,
            string? fontFamily,
            string? fontStyle,
            string? fontWeight)
        {
            RawUrl = rawUrl ?? string.Empty;
            ResolvedUrl = resolvedUrl ?? string.Empty;
            ReferencedFrom = referencedFrom ?? string.Empty;
            FontFamily = fontFamily;
            FontStyle = fontStyle;
            FontWeight = fontWeight;
        }

        public string RawUrl { get; }

        public string ResolvedUrl { get; }

        public string ReferencedFrom { get; }

        public string? FontFamily { get; private set; }

        public string? FontStyle { get; private set; }

        public string? FontWeight { get; private set; }

        public void MergeMetadata(FontUrlCandidate candidate)
        {
            if (string.IsNullOrWhiteSpace(FontFamily) && !string.IsNullOrWhiteSpace(candidate.FontFamily))
            {
                FontFamily = candidate.FontFamily;
            }

            if (string.IsNullOrWhiteSpace(FontStyle) && !string.IsNullOrWhiteSpace(candidate.FontStyle))
            {
                FontStyle = candidate.FontStyle;
            }

            if (string.IsNullOrWhiteSpace(FontWeight) && !string.IsNullOrWhiteSpace(candidate.FontWeight))
            {
                FontWeight = candidate.FontWeight;
            }
        }
    }

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
        IReadOnlyList<ColorSwatch> colorSwatches,
        IReadOnlyList<DesignPageSnapshot>? pages = null,
        IReadOnlyList<string>? processedUrls = null)
    {
        HomeUrl = homeUrl;
        RawHtml = rawHtml;
        InlineCss = inlineCss;
        FontUrls = fontUrls ?? Array.Empty<string>();
        Stylesheets = stylesheets ?? Array.Empty<StylesheetSnapshot>();
        FontFiles = fontFiles ?? Array.Empty<FontAssetSnapshot>();
        ImageFiles = imageFiles ?? Array.Empty<DesignImageSnapshot>();
        ColorSwatches = colorSwatches ?? Array.Empty<ColorSwatch>();
        Pages = pages ?? Array.Empty<DesignPageSnapshot>();
        ProcessedUrls = processedUrls ?? Array.Empty<string>();
    }

    public string HomeUrl { get; }

    public string RawHtml { get; }

    public string InlineCss { get; }

    public IReadOnlyList<string> FontUrls { get; }

    public IReadOnlyList<StylesheetSnapshot> Stylesheets { get; }

    public IReadOnlyList<FontAssetSnapshot> FontFiles { get; }

    public IReadOnlyList<DesignImageSnapshot> ImageFiles { get; }

    public IReadOnlyList<ColorSwatch> ColorSwatches { get; }

    public IReadOnlyList<DesignPageSnapshot> Pages { get; }

    public IReadOnlyList<string> ProcessedUrls { get; }
}

public sealed class DesignPageSnapshot
{
    public DesignPageSnapshot(
        string url,
        string rawHtml,
        string inlineCss,
        IReadOnlyList<string> fontUrls,
        IReadOnlyList<StylesheetSnapshot> stylesheets,
        IReadOnlyList<FontAssetSnapshot> fontFiles,
        IReadOnlyList<DesignImageSnapshot> imageFiles,
        IReadOnlyList<ColorSwatch> colorSwatches)
    {
        Url = url ?? string.Empty;
        RawHtml = rawHtml ?? string.Empty;
        InlineCss = inlineCss ?? string.Empty;
        FontUrls = fontUrls ?? Array.Empty<string>();
        Stylesheets = stylesheets ?? Array.Empty<StylesheetSnapshot>();
        FontFiles = fontFiles ?? Array.Empty<FontAssetSnapshot>();
        ImageFiles = imageFiles ?? Array.Empty<DesignImageSnapshot>();
        ColorSwatches = colorSwatches ?? Array.Empty<ColorSwatch>();
    }

    public string Url { get; }

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
        string? contentType,
        string? fontFamily,
        string? fontStyle,
        string? fontWeight)
    {
        SourceUrl = sourceUrl ?? string.Empty;
        ResolvedUrl = resolvedUrl ?? string.Empty;
        ReferencedFrom = referencedFrom ?? string.Empty;
        Content = content ?? Array.Empty<byte>();
        ContentType = contentType;
        FontFamily = fontFamily;
        FontStyle = fontStyle;
        FontWeight = fontWeight;
    }

    public string SourceUrl { get; }

    public string ResolvedUrl { get; }

    public string ReferencedFrom { get; }

    public byte[] Content { get; }

    public string? ContentType { get; }

    public string? FontFamily { get; }

    public string? FontStyle { get; }

    public string? FontWeight { get; }
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
