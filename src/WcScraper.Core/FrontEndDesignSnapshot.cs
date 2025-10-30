using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WcScraper.Core.Telemetry;

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

    private static readonly Regex HtmlImageTagRegex = new(
        "<(?<tag>img|source|picture)\\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

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
        CancellationToken cancellationToken = default,
        HttpRetryPolicy? retryPolicy = null,
        ILoggerFactory? loggerFactory = null)
    {
        if (httpClient is null) throw new ArgumentNullException(nameof(httpClient));
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentException("Base URL is required.", nameof(baseUrl));

        baseUrl = WooScraper.CleanBaseUrl(baseUrl);
        var homeUrl = baseUrl + "/";

        loggerFactory ??= NullLoggerFactory.Instance;
        retryPolicy ??= new HttpRetryPolicy(logger: loggerFactory.CreateLogger<HttpRetryPolicy>());

        var pageUrls = BuildPageUrlList(homeUrl, additionalPageUrls);
        var processedUrls = new List<string>(pageUrls.Count);
        var pageBuilders = new List<DesignPageSnapshotBuilder>(pageUrls.Count);
        var aggregatedHtml = new StringBuilder();
        var aggregatedCssSources = new List<string>();

        var stylesheetLookup = new Dictionary<string, StylesheetSnapshot>(StringComparer.OrdinalIgnoreCase);
        var aggregatedStylesheets = new List<StylesheetSnapshot>();

        var fontCandidates = new Dictionary<string, FontAssetRequest>(StringComparer.OrdinalIgnoreCase);
        var fontCandidateOrder = new List<string>();
        var iconCandidates = new Dictionary<string, IconAssetRequest>(StringComparer.OrdinalIgnoreCase);
        var iconCandidateOrder = new List<string>();
        var imageCandidates = new Dictionary<string, ImageAssetRequest>(StringComparer.OrdinalIgnoreCase);
        var imageCandidateOrder = new List<string>();

        foreach (var pageUrl in pageUrls)
        {
            var builder = await CapturePageAsync(
                httpClient,
                retryPolicy,
                pageUrl,
                aggregatedStylesheets,
                stylesheetLookup,
                fontCandidates,
                fontCandidateOrder,
                iconCandidates,
                iconCandidateOrder,
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
                var fontContext = new ScraperOperationContext(
                    "FrontEndDesignSnapshot.FetchFont",
                    request.ResolvedUrl,
                    entityType: "font");
                var fontRetryReporter = CreateRetryReporter(log, request.ResolvedUrl);
                using var fontResponse = await retryPolicy.SendAsync(
                    () => httpClient.GetAsync(request.ResolvedUrl, cancellationToken),
                    fontContext,
                    NullScraperInstrumentation.Instance,
                    cancellationToken,
                    onRetry: fontRetryReporter);
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

        var iconSnapshotsMap = new Dictionary<string, DesignIconSnapshot>(StringComparer.OrdinalIgnoreCase);
        var iconSnapshots = new List<DesignIconSnapshot>();

        foreach (var request in iconCandidates.Values)
        {
            try
            {
                log?.Report($"GET {request.ResolvedUrl}");
                var iconContext = new ScraperOperationContext(
                    "FrontEndDesignSnapshot.FetchIcon",
                    request.ResolvedUrl,
                    entityType: "icon");
                var iconRetryReporter = CreateRetryReporter(log, request.ResolvedUrl);
                using var iconResponse = await retryPolicy.SendAsync(
                    () => httpClient.GetAsync(request.ResolvedUrl, cancellationToken),
                    iconContext,
                    NullScraperInstrumentation.Instance,
                    cancellationToken,
                    onRetry: iconRetryReporter);
                if (!iconResponse.IsSuccessStatusCode)
                {
                    log?.Report($"Icon request failed for {request.ResolvedUrl}: {(int)iconResponse.StatusCode} {iconResponse.ReasonPhrase}");
                    continue;
                }

                var iconBytes = await iconResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                var contentType = iconResponse.Content.Headers.ContentType?.MediaType;
                var iconSnapshot = new DesignIconSnapshot(
                    request.RawUrl,
                    request.ResolvedUrl,
                    request.References,
                    iconBytes,
                    contentType,
                    request.Rel,
                    request.LinkType,
                    request.Sizes,
                    request.Color,
                    request.Media);
                iconSnapshotsMap[request.ResolvedUrl] = iconSnapshot;
                iconSnapshots.Add(iconSnapshot);
            }
            catch (TaskCanceledException ex)
            {
                log?.Report($"Icon request canceled for {request.ResolvedUrl}: {ex.Message}");
            }
            catch (HttpRequestException ex)
            {
                log?.Report($"Icon request failed for {request.ResolvedUrl}: {ex.Message}");
            }
        }

        var imageSnapshotsMap = new Dictionary<string, DesignImageSnapshot>(StringComparer.OrdinalIgnoreCase);
        var imageSnapshots = new List<DesignImageSnapshot>();

        foreach (var request in imageCandidates.Values)
        {
            try
            {
                log?.Report($"GET {request.ResolvedUrl}");
                var imageContext = new ScraperOperationContext(
                    "FrontEndDesignSnapshot.FetchImage",
                    request.ResolvedUrl,
                    entityType: "image");
                var imageRetryReporter = CreateRetryReporter(log, request.ResolvedUrl);
                using var imageResponse = await retryPolicy.SendAsync(
                    () => httpClient.GetAsync(request.ResolvedUrl, cancellationToken),
                    imageContext,
                    NullScraperInstrumentation.Instance,
                    cancellationToken,
                    onRetry: imageRetryReporter);
                if (!imageResponse.IsSuccessStatusCode)
                {
                    log?.Report($"Image request failed for {request.ResolvedUrl}: {(int)imageResponse.StatusCode} {imageResponse.ReasonPhrase}");
                    continue;
                }

                var imageBytes = await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                var contentType = imageResponse.Content.Headers.ContentType?.MediaType;
                var imageSnapshot = new DesignImageSnapshot(
                    request.RawUrl,
                    request.ResolvedUrl,
                    request.References,
                    imageBytes,
                    contentType,
                    request.Origins);
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

        var cssImageSnapshots = imageSnapshots
            .Where(snapshot => snapshot.Origins.Contains(DesignImageOrigin.Css))
            .ToList();

        var htmlImageSnapshots = imageSnapshots
            .Where(snapshot => snapshot.Origins.Contains(DesignImageOrigin.Html))
            .ToList();

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
            .Select(builder => builder.Build(fontSnapshotsMap, iconSnapshotsMap, imageSnapshotsMap))
            .ToList();

        return new FrontEndDesignSnapshotResult(
            homeUrl,
            aggregatedHtml.ToString(),
            aggregatedInlineCss,
            fontCandidateOrder,
            aggregatedStylesheets,
            fontSnapshots,
            iconSnapshots,
            imageSnapshots,
            cssImageSnapshots,
            htmlImageSnapshots,
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
        HttpRetryPolicy retryPolicy,
        string pageUrl,
        List<StylesheetSnapshot> aggregatedStylesheets,
        Dictionary<string, StylesheetSnapshot> stylesheetLookup,
        Dictionary<string, FontAssetRequest> fontCandidates,
        List<string> fontCandidateOrder,
        Dictionary<string, IconAssetRequest> iconCandidates,
        List<string> iconCandidateOrder,
        Dictionary<string, ImageAssetRequest> imageCandidates,
        List<string> imageCandidateOrder,
        List<string> aggregatedCssSources,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var builder = new DesignPageSnapshotBuilder(pageUrl);

        log?.Report($"GET {pageUrl}");
        var pageContext = new ScraperOperationContext(
            "FrontEndDesignSnapshot.FetchPage",
            pageUrl,
            entityType: "page");
        var pageRetryReporter = CreateRetryReporter(log, pageUrl);
        using var response = await retryPolicy.SendAsync(
            () => httpClient.GetAsync(pageUrl, cancellationToken),
            pageContext,
            NullScraperInstrumentation.Instance,
            cancellationToken,
            onRetry: pageRetryReporter);
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

                builder.AddImageUrl(candidate.ResolvedUrl, DesignImageOrigin.Css);
            }
        }

        CollectHtmlImageReferences(
            html,
            pageUri,
            pageUrl,
            builder,
            imageCandidates,
            imageCandidateOrder);

        var linkAssets = ExtractLinkAssetReferences(html);

        foreach (var iconReference in linkAssets.IconReferences)
        {
            if (ShouldSkipIconCandidate(iconReference.Href))
            {
                continue;
            }

            var resolved = ResolveAssetUrl(iconReference.Href, pageUri);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                continue;
            }

            var candidate = new IconUrlCandidate(
                iconReference.Href,
                resolved,
                iconReference.Rel,
                iconReference.LinkType,
                iconReference.Sizes,
                iconReference.Color,
                iconReference.Media);

            if (AddIconCandidate(iconCandidates, candidate, pageUrl))
            {
                iconCandidateOrder.Add(candidate.ResolvedUrl);
            }

            builder.AddIconUrl(candidate.ResolvedUrl);
        }

        var stylesheetHrefs = linkAssets.StylesheetHrefs;
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
                var stylesheetContext = new ScraperOperationContext(
                    "FrontEndDesignSnapshot.FetchStylesheet",
                    request.ResolvedUrl,
                    entityType: "stylesheet");
                var stylesheetRetryReporter = CreateRetryReporter(log, request.ResolvedUrl);
                using var cssResponse = await retryPolicy.SendAsync(
                    () => httpClient.GetAsync(request.ResolvedUrl, cancellationToken),
                    stylesheetContext,
                    NullScraperInstrumentation.Instance,
                    cancellationToken,
                    onRetry: stylesheetRetryReporter);
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

            builder.AddImageUrl(candidate.ResolvedUrl, DesignImageOrigin.Css);
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

    private static LinkAssetExtractionResult ExtractLinkAssetReferences(string html)
    {
        var stylesheetHrefs = new List<string>();
        var iconReferences = new List<LinkIconReference>();

        foreach (Match match in LinkTagRegex.Matches(html))
        {
            var tag = match.Value;
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            string? relRaw = null;
            string? href = null;
            string? linkType = null;
            string? sizes = null;
            string? color = null;
            string? media = null;

            foreach (Match attr in AttributeRegex.Matches(tag))
            {
                if (!attr.Success)
                {
                    continue;
                }

                var name = attr.Groups["name"].Value;
                var value = attr.Groups["value"].Value;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var decoded = DecodeHtmlAttributeValue(value);
                if (string.Equals(name, "rel", StringComparison.OrdinalIgnoreCase))
                {
                    relRaw = decoded;
                }
                else if (string.Equals(name, "href", StringComparison.OrdinalIgnoreCase))
                {
                    href = decoded;
                }
                else if (string.Equals(name, "type", StringComparison.OrdinalIgnoreCase))
                {
                    linkType = decoded;
                }
                else if (string.Equals(name, "sizes", StringComparison.OrdinalIgnoreCase))
                {
                    sizes = decoded;
                }
                else if (string.Equals(name, "color", StringComparison.OrdinalIgnoreCase))
                {
                    color = decoded;
                }
                else if (string.Equals(name, "media", StringComparison.OrdinalIgnoreCase))
                {
                    media = decoded;
                }
            }

            if (string.IsNullOrWhiteSpace(relRaw) || string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            var relTokens = relRaw
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(r => r.Trim())
                .Where(r => !string.IsNullOrEmpty(r))
                .ToArray();

            if (relTokens.Any(r => string.Equals(r, "stylesheet", StringComparison.OrdinalIgnoreCase)))
            {
                stylesheetHrefs.Add(href);
            }

            if (relTokens.Any(IsIconRelToken))
            {
                iconReferences.Add(new LinkIconReference(href, relRaw, linkType, sizes, color, media));
            }
        }

        return new LinkAssetExtractionResult(stylesheetHrefs, iconReferences);
    }

    private static bool IsIconRelToken(string relToken)
    {
        if (string.IsNullOrWhiteSpace(relToken))
        {
            return false;
        }

        var normalized = relToken.Trim();

        if (string.Equals(normalized, "icon", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(normalized, "favicon", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(normalized, "shortcut-icon", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalized.StartsWith("apple-touch-icon", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalized.StartsWith("mask-icon", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalized.StartsWith("fluid-icon", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static void CollectHtmlImageReferences(
        string html,
        Uri? pageUri,
        string pageUrl,
        DesignPageSnapshotBuilder builder,
        IDictionary<string, ImageAssetRequest> imageCandidates,
        IList<string> imageCandidateOrder)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return;
        }

        foreach (Match tagMatch in HtmlImageTagRegex.Matches(html))
        {
            if (!tagMatch.Success)
            {
                continue;
            }

            var tagName = tagMatch.Groups["tag"].Value;
            if (string.IsNullOrWhiteSpace(tagName))
            {
                continue;
            }

            foreach (Match attribute in AttributeRegex.Matches(tagMatch.Value))
            {
                if (!attribute.Success)
                {
                    continue;
                }

                var attributeName = attribute.Groups["name"].Value;
                var attributeValue = attribute.Groups["value"].Value;
                if (string.IsNullOrWhiteSpace(attributeName) || string.IsNullOrWhiteSpace(attributeValue))
                {
                    continue;
                }

                var normalizedName = attributeName.Trim();
                var decodedValue = DecodeHtmlAttributeValue(attributeValue);
                if (string.IsNullOrWhiteSpace(decodedValue))
                {
                    continue;
                }

                var isSrcAttribute = IsHtmlSrcAttribute(normalizedName);
                var isSrcSetAttribute = IsHtmlSrcSetAttribute(normalizedName);
                if (!isSrcAttribute && !isSrcSetAttribute)
                {
                    continue;
                }

                var referencedFrom = BuildHtmlImageReference(pageUrl, tagName, normalizedName);

                if (isSrcAttribute)
                {
                    ProcessHtmlImageCandidate(
                        decodedValue,
                        pageUri,
                        tagName,
                        referencedFrom,
                        imageCandidates,
                        imageCandidateOrder,
                        builder);
                }
                else
                {
                    foreach (var candidateValue in SplitSrcSetValues(decodedValue))
                    {
                        ProcessHtmlImageCandidate(
                            candidateValue,
                            pageUri,
                            tagName,
                            referencedFrom,
                            imageCandidates,
                            imageCandidateOrder,
                            builder);
                    }
                }
            }
        }
    }

    private static string? DecodeHtmlAttributeValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var decoded = WebUtility.HtmlDecode(value)?.Trim();
        return string.IsNullOrWhiteSpace(decoded) ? null : decoded;
    }

    private static bool IsHtmlSrcAttribute(string attributeName)
    {
        if (string.IsNullOrWhiteSpace(attributeName))
        {
            return false;
        }

        if (string.Equals(attributeName, "src", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (attributeName.StartsWith("data-", StringComparison.OrdinalIgnoreCase)
            && attributeName.IndexOf("src", StringComparison.OrdinalIgnoreCase) >= 0
            && attributeName.IndexOf("srcset", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return true;
        }

        return false;
    }

    private static bool IsHtmlSrcSetAttribute(string attributeName)
    {
        if (string.IsNullOrWhiteSpace(attributeName))
        {
            return false;
        }

        if (string.Equals(attributeName, "srcset", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return attributeName.StartsWith("data-", StringComparison.OrdinalIgnoreCase)
            && attributeName.IndexOf("srcset", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static IEnumerable<string> SplitSrcSetValues(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;

        foreach (var part in value.Split(','))
        {
            var trimmed = part?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            var separatorIndex = trimmed.IndexOf(' ');
            var candidate = separatorIndex >= 0
                ? trimmed[..separatorIndex].Trim()
                : trimmed;

            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            yield return candidate;
        }
    }

    private static void ProcessHtmlImageCandidate(
        string rawValue,
        Uri? pageUri,
        string tagName,
        string referencedFrom,
        IDictionary<string, ImageAssetRequest> imageCandidates,
        IList<string> imageCandidateOrder,
        DesignPageSnapshotBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return;
        }

        var candidateValue = rawValue.Trim();
        if (candidateValue.Length >= 2)
        {
            candidateValue = candidateValue.Trim('"', '\'');
        }

        if (candidateValue.StartsWith("url(", StringComparison.OrdinalIgnoreCase)
            && candidateValue.EndsWith(")", StringComparison.Ordinal))
        {
            candidateValue = candidateValue.Length > 5
                ? candidateValue.Substring(4, candidateValue.Length - 5)
                : string.Empty;
            candidateValue = candidateValue.Trim('"', '\'', ' ');
        }

        if (ShouldSkipImageCandidate(candidateValue))
        {
            return;
        }

        var resolved = ResolveAssetUrl(candidateValue, pageUri);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            return;
        }

        var candidate = new ImageUrlCandidate(
            candidateValue,
            resolved,
            DesignImageOrigin.Html);

        if (AddImageCandidate(imageCandidates, candidate, referencedFrom))
        {
            imageCandidateOrder.Add(candidate.ResolvedUrl);
        }

        builder.AddImageUrl(candidate.ResolvedUrl, DesignImageOrigin.Html);
    }

    private static string BuildHtmlImageReference(string pageUrl, string tagName, string attributeName)
    {
        var normalizedTag = string.IsNullOrWhiteSpace(tagName) ? "element" : tagName.Trim();
        var normalizedAttribute = string.IsNullOrWhiteSpace(attributeName) ? "src" : attributeName.Trim();
        var fragment = $"{normalizedTag.ToLowerInvariant()}[{normalizedAttribute.ToLowerInvariant()}]";
        return string.IsNullOrWhiteSpace(pageUrl) ? fragment : pageUrl + "#" + fragment;
    }

    private static bool ShouldSkipImageCandidate(string candidateValue)
    {
        if (string.IsNullOrWhiteSpace(candidateValue))
        {
            return true;
        }

        var trimmed = candidateValue.Trim();
        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.StartsWith("#", StringComparison.Ordinal)) return true;
        if (trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static bool ShouldSkipIconCandidate(string candidateValue)
    {
        if (string.IsNullOrWhiteSpace(candidateValue))
        {
            return true;
        }

        var trimmed = candidateValue.Trim();
        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.StartsWith("#", StringComparison.Ordinal)) return true;
        if (trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
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

    private static Action<ScraperOperationContext>? CreateRetryReporter(IProgress<string>? log, string target)
        => log is null
            ? null
            : context => ReportRetry(log, target, context);

    private static void ReportRetry(IProgress<string>? log, string url, ScraperOperationContext retryContext)
    {
        if (log is null || retryContext.RetryAttempt is not { } attempt)
        {
            return;
        }

        var delayValue = retryContext.RetryDelay ?? TimeSpan.Zero;
        string delay;
        if (delayValue.TotalSeconds >= 1)
        {
            delay = $"{delayValue.TotalSeconds:F1}s";
        }
        else
        {
            delay = $"{Math.Max(1, delayValue.TotalMilliseconds):F0}ms";
        }

        var reason = string.IsNullOrWhiteSpace(retryContext.RetryReason)
            ? "Retry scheduled."
            : retryContext.RetryReason;

        log.Report($"Retrying {url} in {delay} (attempt {attempt}): {reason}");
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

    private static bool AddIconCandidate(
        IDictionary<string, IconAssetRequest> accumulator,
        IconUrlCandidate candidate,
        string referencedFrom)
    {
        if (accumulator.TryGetValue(candidate.ResolvedUrl, out var existing))
        {
            existing.AddReference(referencedFrom);
            existing.MergeMetadata(candidate);
            return false;
        }

        accumulator[candidate.ResolvedUrl] = new IconAssetRequest(
            candidate.RawUrl,
            candidate.ResolvedUrl,
            referencedFrom,
            candidate.Rel,
            candidate.LinkType,
            candidate.Sizes,
            candidate.Color,
            candidate.Media);
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

            yield return new ImageUrlCandidate(raw, resolved, DesignImageOrigin.Css);
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
        if (accumulator.TryGetValue(candidate.ResolvedUrl, out var existing))
        {
            existing.AddOrigin(candidate.Origin);
            existing.AddReference(referencedFrom);
            return false;
        }

        accumulator[candidate.ResolvedUrl] = new ImageAssetRequest(
            candidate.RawUrl,
            candidate.ResolvedUrl,
            referencedFrom,
            candidate.Origin);
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
        private readonly HashSet<string> _iconUrls = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _imageUrls = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _cssImageUrls = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _htmlImageUrls = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _cssSources = new();
        private readonly List<StylesheetSnapshot> _stylesheets = new();
        private readonly List<string> _fontUrlList = new();
        private readonly List<string> _iconUrlList = new();
        private readonly List<string> _imageUrlList = new();
        private readonly List<string> _cssImageUrlList = new();
        private readonly List<string> _htmlImageUrlList = new();

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

        public void AddIconUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            var normalized = url.Trim();
            if (_iconUrls.Add(normalized))
            {
                _iconUrlList.Add(normalized);
            }
        }

        public void AddImageUrl(string? url, DesignImageOrigin origin = DesignImageOrigin.Css)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            var normalized = url.Trim();
            if (_imageUrls.Add(normalized))
            {
                _imageUrlList.Add(normalized);
            }

            switch (origin)
            {
                case DesignImageOrigin.Html:
                    if (_htmlImageUrls.Add(normalized))
                    {
                        _htmlImageUrlList.Add(normalized);
                    }

                    break;
                default:
                    if (_cssImageUrls.Add(normalized))
                    {
                        _cssImageUrlList.Add(normalized);
                    }

                    break;
            }
        }

        public DesignPageSnapshot Build(
            IReadOnlyDictionary<string, FontAssetSnapshot> fontSnapshots,
            IReadOnlyDictionary<string, DesignIconSnapshot> iconSnapshots,
            IReadOnlyDictionary<string, DesignImageSnapshot> imageSnapshots)
        {
            var fontFiles = _fontUrlList
                .Select(url => fontSnapshots.TryGetValue(url, out var snapshot) ? snapshot : null)
                .Where(snapshot => snapshot is not null)
                .Cast<FontAssetSnapshot>()
                .ToList();

            var iconFiles = _iconUrlList
                .Select(url => iconSnapshots.TryGetValue(url, out var snapshot) ? snapshot : null)
                .Where(snapshot => snapshot is not null)
                .Cast<DesignIconSnapshot>()
                .ToList();

            var imageFiles = _imageUrlList
                .Select(url => imageSnapshots.TryGetValue(url, out var snapshot) ? snapshot : null)
                .Where(snapshot => snapshot is not null)
                .Cast<DesignImageSnapshot>()
                .ToList();

            var cssImageFiles = _cssImageUrlList
                .Select(url => imageSnapshots.TryGetValue(url, out var snapshot) ? snapshot : null)
                .Where(snapshot => snapshot is not null)
                .Cast<DesignImageSnapshot>()
                .ToList();

            var htmlImageFiles = _htmlImageUrlList
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
                iconFiles,
                imageFiles,
                cssImageFiles,
                htmlImageFiles,
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

    private readonly record struct IconUrlCandidate(
        string RawUrl,
        string ResolvedUrl,
        string? Rel,
        string? LinkType,
        string? Sizes,
        string? Color,
        string? Media);

    private sealed record LinkAssetExtractionResult(
        IReadOnlyList<string> StylesheetHrefs,
        IReadOnlyList<LinkIconReference> IconReferences);

    private readonly record struct LinkIconReference(
        string Href,
        string? Rel,
        string? LinkType,
        string? Sizes,
        string? Color,
        string? Media);

    private readonly record struct ImageUrlCandidate(string RawUrl, string ResolvedUrl, DesignImageOrigin Origin);

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

    private sealed class IconAssetRequest
    {
        private readonly HashSet<string> _referenceSet = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _references = new();

        public IconAssetRequest(
            string rawUrl,
            string resolvedUrl,
            string referencedFrom,
            string? rel,
            string? linkType,
            string? sizes,
            string? color,
            string? media)
        {
            RawUrl = rawUrl ?? string.Empty;
            ResolvedUrl = resolvedUrl ?? string.Empty;
            Rel = rel;
            LinkType = linkType;
            Sizes = sizes;
            Color = color;
            Media = media;
            AddReference(referencedFrom);
        }

        public string RawUrl { get; }

        public string ResolvedUrl { get; }

        public string? Rel { get; private set; }

        public string? LinkType { get; private set; }

        public string? Sizes { get; private set; }

        public string? Color { get; private set; }

        public string? Media { get; private set; }

        public string ReferencedFrom => _references.Count > 0 ? _references[0] : string.Empty;

        public IReadOnlyList<string> References => _references;

        public void AddReference(string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                return;
            }

            if (_referenceSet.Add(reference))
            {
                _references.Add(reference);
            }
        }

        public void MergeMetadata(IconUrlCandidate candidate)
        {
            if (string.IsNullOrWhiteSpace(Rel) && !string.IsNullOrWhiteSpace(candidate.Rel))
            {
                Rel = candidate.Rel;
            }

            if (string.IsNullOrWhiteSpace(LinkType) && !string.IsNullOrWhiteSpace(candidate.LinkType))
            {
                LinkType = candidate.LinkType;
            }

            if (string.IsNullOrWhiteSpace(Sizes) && !string.IsNullOrWhiteSpace(candidate.Sizes))
            {
                Sizes = candidate.Sizes;
            }

            if (string.IsNullOrWhiteSpace(Color) && !string.IsNullOrWhiteSpace(candidate.Color))
            {
                Color = candidate.Color;
            }

            if (string.IsNullOrWhiteSpace(Media) && !string.IsNullOrWhiteSpace(candidate.Media))
            {
                Media = candidate.Media;
            }
        }
    }

    private sealed class ImageAssetRequest
    {
        private readonly HashSet<DesignImageOrigin> _originSet = new();
        private readonly List<DesignImageOrigin> _originList = new();
        private readonly HashSet<string> _referenceSet = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _references = new();

        public ImageAssetRequest(
            string rawUrl,
            string resolvedUrl,
            string referencedFrom,
            DesignImageOrigin origin)
        {
            RawUrl = rawUrl ?? string.Empty;
            ResolvedUrl = resolvedUrl ?? string.Empty;
            AddOrigin(origin);
            AddReference(referencedFrom);
        }

        public string RawUrl { get; }

        public string ResolvedUrl { get; }

        public string ReferencedFrom => _references.Count > 0 ? _references[0] : string.Empty;

        public IReadOnlyList<string> References => _references;

        public IReadOnlyList<DesignImageOrigin> Origins => _originList;

        public void AddOrigin(DesignImageOrigin origin)
        {
            if (_originSet.Add(origin))
            {
                _originList.Add(origin);
            }
        }

        public void AddReference(string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                return;
            }

            if (_referenceSet.Add(reference))
            {
                _references.Add(reference);
            }
        }
    }

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
        IReadOnlyList<DesignIconSnapshot> iconFiles,
        IReadOnlyList<DesignImageSnapshot> imageFiles,
        IReadOnlyList<DesignImageSnapshot> cssImageFiles,
        IReadOnlyList<DesignImageSnapshot> htmlImageFiles,
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
        IconFiles = iconFiles ?? Array.Empty<DesignIconSnapshot>();
        ImageFiles = imageFiles ?? Array.Empty<DesignImageSnapshot>();
        CssImageFiles = cssImageFiles ?? Array.Empty<DesignImageSnapshot>();
        HtmlImageFiles = htmlImageFiles ?? Array.Empty<DesignImageSnapshot>();
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

    public IReadOnlyList<DesignIconSnapshot> IconFiles { get; }

    public IReadOnlyList<DesignImageSnapshot> ImageFiles { get; }

    public IReadOnlyList<DesignImageSnapshot> CssImageFiles { get; }

    public IReadOnlyList<DesignImageSnapshot> HtmlImageFiles { get; }

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
        IReadOnlyList<DesignIconSnapshot> iconFiles,
        IReadOnlyList<DesignImageSnapshot> imageFiles,
        IReadOnlyList<DesignImageSnapshot> cssImageFiles,
        IReadOnlyList<DesignImageSnapshot> htmlImageFiles,
        IReadOnlyList<ColorSwatch> colorSwatches)
    {
        Url = url ?? string.Empty;
        RawHtml = rawHtml ?? string.Empty;
        InlineCss = inlineCss ?? string.Empty;
        FontUrls = fontUrls ?? Array.Empty<string>();
        Stylesheets = stylesheets ?? Array.Empty<StylesheetSnapshot>();
        FontFiles = fontFiles ?? Array.Empty<FontAssetSnapshot>();
        IconFiles = iconFiles ?? Array.Empty<DesignIconSnapshot>();
        ImageFiles = imageFiles ?? Array.Empty<DesignImageSnapshot>();
        CssImageFiles = cssImageFiles ?? Array.Empty<DesignImageSnapshot>();
        HtmlImageFiles = htmlImageFiles ?? Array.Empty<DesignImageSnapshot>();
        ColorSwatches = colorSwatches ?? Array.Empty<ColorSwatch>();
    }

    public string Url { get; }

    public string RawHtml { get; }

    public string InlineCss { get; }

    public IReadOnlyList<string> FontUrls { get; }

    public IReadOnlyList<StylesheetSnapshot> Stylesheets { get; }

    public IReadOnlyList<FontAssetSnapshot> FontFiles { get; }

    public IReadOnlyList<DesignIconSnapshot> IconFiles { get; }

    public IReadOnlyList<DesignImageSnapshot> ImageFiles { get; }

    public IReadOnlyList<DesignImageSnapshot> CssImageFiles { get; }

    public IReadOnlyList<DesignImageSnapshot> HtmlImageFiles { get; }

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

public sealed class DesignIconSnapshot
{
    public DesignIconSnapshot(
        string sourceUrl,
        string resolvedUrl,
        IReadOnlyList<string> references,
        byte[] content,
        string? contentType,
        string? rel,
        string? linkType,
        string? sizes,
        string? color,
        string? media)
    {
        SourceUrl = sourceUrl ?? string.Empty;
        ResolvedUrl = resolvedUrl ?? string.Empty;
        References = references ?? Array.Empty<string>();
        ReferencedFrom = References.Count > 0 ? References[0] : string.Empty;
        Content = content ?? Array.Empty<byte>();
        ContentType = contentType;
        Rel = rel;
        LinkType = linkType;
        Sizes = sizes;
        Color = color;
        Media = media;
    }

    public string SourceUrl { get; }

    public string ResolvedUrl { get; }

    public string ReferencedFrom { get; }

    public IReadOnlyList<string> References { get; }

    public byte[] Content { get; }

    public string? ContentType { get; }

    public string? Rel { get; }

    public string? LinkType { get; }

    public string? Sizes { get; }

    public string? Color { get; }

    public string? Media { get; }
}

public sealed class DesignImageSnapshot
{
    public DesignImageSnapshot(
        string sourceUrl,
        string resolvedUrl,
        IReadOnlyList<string> references,
        byte[] content,
        string? contentType,
        IReadOnlyList<DesignImageOrigin> origins)
    {
        SourceUrl = sourceUrl ?? string.Empty;
        ResolvedUrl = resolvedUrl ?? string.Empty;
        References = references ?? Array.Empty<string>();
        ReferencedFrom = References.Count > 0 ? References[0] : string.Empty;
        Content = content ?? Array.Empty<byte>();
        ContentType = contentType;
        Origins = origins ?? Array.Empty<DesignImageOrigin>();
    }

    public string SourceUrl { get; }

    public string ResolvedUrl { get; }

    public string ReferencedFrom { get; }

    public IReadOnlyList<string> References { get; }

    public byte[] Content { get; }

    public string? ContentType { get; }

    public IReadOnlyList<DesignImageOrigin> Origins { get; }
}

public enum DesignImageOrigin
{
    Css,
    Html
}

public sealed record ColorSwatch(string Value, int Count);
