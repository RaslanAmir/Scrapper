using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WcScraper.Core;

public sealed class PublicExtensionDetector : IDisposable
{
    private static readonly Regex ExtensionPathRegex = new(
        @"/wp-content/(?<type>plugins|themes|mu-plugins)/(?<slug>[A-Za-z0-9._-]+)(?=$|/|\\?|\.|#)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VersionTokenRegex = new(
        @"(?:^|[-_\.])(?:v(?:ersion)?[-_\.]?)?(?<version>\d+(?:\.\d+){1,})(?=$|[-_\.])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LinkTagRegex = new(
        @"<link\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LinkRelRegex = new(
        @"rel\s*=\s*(['""])(?<value>.*?)\1",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex LinkAsRegex = new(
        @"as\s*=\s*(['""])(?<value>.*?)\1",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex ScriptSrcRegex = new(
        @"<script\b[^>]*src\s*=\s*(['""])(?<url>.*?)\1",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnorePatternWhitespace);

    private static readonly Regex HrefRegex = new(
        @"href\s*=\s*(['""])(?<url>.*?)\1",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly HttpRetryPolicy _httpPolicy;

    private VersionEvidence _wordpressVersion = VersionEvidence.None;
    private VersionEvidence _wooCommerceVersion = VersionEvidence.None;

    public int? LastMaxPages { get; private set; }

    public long? LastMaxBytes { get; private set; }

    public bool PageLimitReached { get; private set; }

    public bool ByteLimitReached { get; private set; }

    public int ScheduledPageCount { get; private set; }

    public int ProcessedPageCount { get; private set; }

    public long TotalBytesDownloaded { get; private set; }

    public string? WordPressVersion => _wordpressVersion.Version;

    public string? WooCommerceVersion => _wooCommerceVersion.Version;

    public PublicExtensionDetector(HttpClient? httpClient = null, HttpRetryPolicy? httpRetryPolicy = null)
    {
        if (httpClient is null)
        {
            _httpClient = new HttpClient(new SocketsHttpHandler(), disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _ownsClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _ownsClient = false;
        }

        _httpPolicy = httpRetryPolicy ?? new HttpRetryPolicy();
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    public async Task<IReadOnlyList<PublicExtensionFootprint>> DetectAsync(
        string baseUrl,
        bool followLinkedAssets = true,
        IProgress<string>? log = null,
        IEnumerable<string>? extraEntryUrls = null,
        int? maxPages = null,
        long? maxBytes = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("Base URL cannot be empty", nameof(baseUrl));
        }

        baseUrl = WooScraper.CleanBaseUrl(baseUrl);
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new ArgumentException("Base URL must be an absolute URI", nameof(baseUrl));
        }

        var pageLimit = NormalizePageLimit(maxPages);
        var byteLimit = NormalizeByteLimit(maxBytes);

        LastMaxPages = pageLimit;
        LastMaxBytes = byteLimit;
        PageLimitReached = false;
        ByteLimitReached = false;
        ScheduledPageCount = 0;
        ProcessedPageCount = 0;
        TotalBytesDownloaded = 0;
        _pageLimitLogged = false;
        _byteLimitLogged = false;
        _wordpressVersion = VersionEvidence.None;
        _wooCommerceVersion = VersionEvidence.None;

        _log = log;

        _scheduledUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _urlsToProcess = new Queue<string>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entryUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            baseUri.ToString()
        };

        TryScheduleUrl(baseUri.ToString());

        if (extraEntryUrls is not null)
        {
            foreach (var entry in extraEntryUrls)
            {
                if (string.IsNullOrWhiteSpace(entry))
                {
                    continue;
                }

                if (!Uri.TryCreate(entry, UriKind.Absolute, out var entryUri) &&
                    !Uri.TryCreate(baseUri, entry, out entryUri))
                {
                    continue;
                }

                var entryUrl = entryUri.ToString();
                if (!entryUrls.Add(entryUrl))
                {
                    continue;
                }

                if (!TryScheduleUrl(entryUrl))
                {
                    entryUrls.Remove(entryUrl);
                }
            }
        }

        var findings = new Dictionary<(string Type, string Slug), PublicExtensionFootprint>();

        while (_urlsToProcess.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentUrl = _urlsToProcess.Dequeue();
            if (!seenUrls.Add(currentUrl))
            {
                continue;
            }

            ProcessedPageCount++;

            var download = await DownloadAsync(currentUrl, log, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(download.Content))
            {
                TotalBytesDownloaded += download.ByteCount;
                if (LastMaxBytes.HasValue && TotalBytesDownloaded >= LastMaxBytes.Value)
                {
                    ByteLimitReached = true;
                    ReportByteLimit(LastMaxBytes.Value);
                }
            }

            var content = download.Content;
            if (string.IsNullOrEmpty(content))
            {
                continue;
            }

            ScanForExtensions(currentUrl, content, findings);

            if (!followLinkedAssets || !entryUrls.Contains(currentUrl))
            {
                continue;
            }

            foreach (var asset in ExtractLinkedAssets(content, baseUri))
            {
                InspectAssetForPlatformVersions(asset.Url);

                if (!seenUrls.Contains(asset.Url))
                {
                    if (asset.IsPreload)
                    {
                        log?.Report($"Following {asset.PreloadDescription} asset {asset.Url}");
                    }

                    TryScheduleUrl(asset.Url);
                }
            }
        }

        return findings.Values.ToList();
    }

    private HashSet<string> _scheduledUrls = new(StringComparer.OrdinalIgnoreCase);
    private Queue<string> _urlsToProcess = new();
    private bool _pageLimitLogged;
    private bool _byteLimitLogged;
    private IProgress<string>? _log;

    private async Task<(string? Content, long ByteCount)> DownloadAsync(
        string url,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        try
        {
            log?.Report($"GET {url}");
            using var response = await _httpPolicy.SendAsync(
                () => _httpClient.GetAsync(url, cancellationToken),
                cancellationToken,
                attempt =>
                {
                    if (log is not null)
                    {
                        var delay = attempt.Delay.TotalSeconds >= 1
                            ? $"{attempt.Delay.TotalSeconds:F1}s"
                            : $"{attempt.Delay.TotalMilliseconds:F0}ms";
                        log.Report($"Retrying {url} in {delay} (attempt {attempt.AttemptNumber}): {attempt.Reason}");
                    }
                }).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode == 404)
                {
                    log?.Report($"Public extension detection received 404 for {url}");
                    return (null, 0);
                }

                response.EnsureSuccessStatusCode();
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var byteCount = response.Content.Headers.ContentLength
                ?? Encoding.UTF8.GetByteCount(content);
            return (content, byteCount);
        }
        catch (TaskCanceledException ex)
        {
            log?.Report($"Public extension detection request timed out: {ex.Message}");
        }
        catch (AuthenticationException ex)
        {
            log?.Report($"Public extension detection TLS handshake failed: {ex.Message}");
        }
        catch (IOException ex)
        {
            log?.Report($"Public extension detection I/O failure: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            log?.Report($"Public extension detection request failed: {ex.Message}");
        }

        return (null, 0);
    }

    private bool TryScheduleUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!_scheduledUrls.Add(url))
        {
            return false;
        }

        if (!CanScheduleAdditionalUrl())
        {
            _scheduledUrls.Remove(url);
            return false;
        }

        _urlsToProcess.Enqueue(url);
        ScheduledPageCount++;
        return true;
    }

    private bool CanScheduleAdditionalUrl()
    {
        if (LastMaxPages.HasValue && ScheduledPageCount >= LastMaxPages.Value)
        {
            PageLimitReached = true;
            ReportPageLimit(LastMaxPages.Value);
            return false;
        }

        if (LastMaxBytes.HasValue && TotalBytesDownloaded >= LastMaxBytes.Value)
        {
            ByteLimitReached = true;
            ReportByteLimit(LastMaxBytes.Value);
            return false;
        }

        return true;
    }

    private void ReportPageLimit(int limit)
    {
        if (_pageLimitLogged)
        {
            return;
        }

        _log?.Report($"Public extension detection page limit reached ({limit:N0} page(s)); skipping additional URLs.");
        _pageLimitLogged = true;
    }

    private void ReportByteLimit(long limit)
    {
        if (_byteLimitLogged)
        {
            return;
        }

        _log?.Report($"Public extension detection byte limit reached ({FormatByteSize(limit)}); skipping additional URLs.");
        _byteLimitLogged = true;
    }

    private static int? NormalizePageLimit(int? maxPages)
        => maxPages.HasValue && maxPages.Value > 0 ? maxPages : null;

    private static long? NormalizeByteLimit(long? maxBytes)
        => maxBytes.HasValue && maxBytes.Value > 0 ? maxBytes : null;

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

    private void ScanForExtensions(
        string sourceUrl,
        string content,
        IDictionary<(string Type, string Slug), PublicExtensionFootprint> findings)
    {
        foreach (Match match in ExtensionPathRegex.Matches(content))
        {
            if (!match.Success)
            {
                continue;
            }

            var type = NormalizeExtensionType(match.Groups["type"].Value);
            var slug = NormalizeSlug(match.Groups["slug"].Value);
            if (string.IsNullOrEmpty(slug))
            {
                continue;
            }

            var assetUrl = ExtractAssetUrl(content, match);
            var versionHint = ExtractVersionHint(assetUrl, match);

            InspectAssetForPlatformVersions(assetUrl);

            var key = (type, slug);
            if (!findings.TryGetValue(key, out var footprint))
            {
                var sourceUrls = string.IsNullOrWhiteSpace(sourceUrl)
                    ? new List<string>()
                    : new List<string> { sourceUrl };

                footprint = new PublicExtensionFootprint
                {
                    Slug = slug,
                    Type = type,
                    SourceUrl = sourceUrl,
                    SourceUrls = sourceUrls,
                    AssetUrl = assetUrl,
                    VersionHint = versionHint
                };

                findings[key] = footprint;
                continue;
            }

            if (string.IsNullOrWhiteSpace(footprint.SourceUrl))
            {
                footprint.SourceUrl = sourceUrl;
            }

            MergeSourceUrls(footprint, sourceUrl);

            if (string.IsNullOrWhiteSpace(footprint.AssetUrl) && !string.IsNullOrWhiteSpace(assetUrl))
            {
                footprint.AssetUrl = assetUrl;
            }

            if (string.IsNullOrWhiteSpace(footprint.VersionHint) && !string.IsNullOrWhiteSpace(versionHint))
            {
                footprint.VersionHint = versionHint;
            }
        }
    }

    private static void MergeSourceUrls(PublicExtensionFootprint footprint, string? additionalUrl)
    {
        var combined = AggregateSourceUrls(footprint, additionalUrl);
        if (combined.Count > 0)
        {
            var primary = combined[0];
            if (!string.Equals(primary, footprint.SourceUrl, StringComparison.Ordinal))
            {
                footprint.SourceUrl = primary;
            }
        }
        else if (!string.IsNullOrWhiteSpace(footprint.SourceUrl))
        {
            footprint.SourceUrl = string.Empty;
        }

        footprint.SourceUrls = combined;
    }

    private static List<string> AggregateSourceUrls(PublicExtensionFootprint footprint, string? additionalUrl)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void AddIfMissing(List<string> target, HashSet<string> seenSet, string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            if (seenSet.Add(url))
            {
                target.Add(url);
            }
        }

        AddIfMissing(result, seen, footprint.SourceUrl);

        if (footprint.SourceUrls is { Count: > 0 })
        {
            foreach (var url in footprint.SourceUrls)
            {
                AddIfMissing(result, seen, url);
            }
        }

        AddIfMissing(result, seen, additionalUrl);

        return result;
    }

    private static string? ExtractAssetUrl(string content, Match match)
    {
        var start = DetermineAssetStartIndex(content, match.Index);
        if (start < 0)
        {
            return null;
        }

        var (end, closingDelimiter) = DetermineAssetEndIndex(content, match.Index);
        if (end < 0)
        {
            end = content.Length;
        }

        if (end <= start)
        {
            return null;
        }

        return content[start..end].Trim();
    }

    private static int DetermineAssetStartIndex(string content, int matchIndex)
    {
        for (var i = matchIndex - 1; i >= 0; i--)
        {
            var c = content[i];
            if (c is '"' or '\'' or '(')
            {
                return i + 1;
            }

            if (char.IsWhiteSpace(c) || c == '>')
            {
                return matchIndex;
            }
        }

        return matchIndex;
    }

    private static (int EndIndex, char ClosingDelimiter) DetermineAssetEndIndex(string content, int matchIndex)
    {
        char closingDelimiter = '\0';
        for (var i = matchIndex - 1; i >= 0; i--)
        {
            var c = content[i];
            if (c is '"' or '\'' or '(')
            {
                closingDelimiter = c switch
                {
                    '(' => ')',
                    var quote => quote
                };
                break;
            }
        }

        if (closingDelimiter is '"' or '\'' or ')')
        {
            var end = content.IndexOf(closingDelimiter, matchIndex);
            if (end >= 0)
            {
                return (end, closingDelimiter);
            }
        }

        for (var i = matchIndex; i < content.Length; i++)
        {
            var c = content[i];
            if (char.IsWhiteSpace(c) || c is '>' or '"' or '\'' || (c == ')' && closingDelimiter == ')'))
            {
                return (i, closingDelimiter);
            }
        }

        return (-1, closingDelimiter);
    }

    private static string? ExtractVersionHint(string? assetUrl, Match match)
    {
        if (!string.IsNullOrWhiteSpace(assetUrl))
        {
            var fromQuery = TryGetQueryVersion(assetUrl);
            if (!string.IsNullOrWhiteSpace(fromQuery))
            {
                return fromQuery;
            }

            var normalized = assetUrl;
            var fragmentIndex = normalized.IndexOf('#');
            if (fragmentIndex >= 0)
            {
                normalized = normalized[..fragmentIndex];
            }

            var queryIndex = normalized.IndexOf('?');
            if (queryIndex >= 0)
            {
                normalized = normalized[..queryIndex];
            }

            var fileName = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                var matchVersion = VersionTokenRegex.Match(fileName);
                if (matchVersion.Success)
                {
                    return matchVersion.Groups["version"].Value;
                }
            }
        }

        var fallback = VersionTokenRegex.Match(match.Value);
        return fallback.Success ? fallback.Groups["version"].Value : null;
    }

    private static string NormalizeExtensionType(string rawType)
    {
        if (string.IsNullOrWhiteSpace(rawType))
        {
            return "plugin";
        }

        return rawType.Trim().ToLowerInvariant() switch
        {
            "themes" or "theme" => "theme",
            "mu-plugins" or "mu-plugin" => "mu-plugin",
            _ => "plugin"
        };
    }

    private static string? TryGetQueryVersion(string assetUrl)
    {
        var queryStart = assetUrl.IndexOf('?');
        if (queryStart < 0)
        {
            return null;
        }

        var queryEnd = assetUrl.IndexOf('#', queryStart);
        var query = queryEnd >= 0
            ? assetUrl[(queryStart + 1)..queryEnd]
            : assetUrl[(queryStart + 1)..];

        foreach (var segment in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kvp = segment.Split('=', 2);
            var key = Uri.UnescapeDataString(kvp[0]);
            if (!IsVersionQueryKey(key))
            {
                continue;
            }

            if (kvp.Length == 2)
            {
                var value = Uri.UnescapeDataString(kvp[1]);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private void InspectAssetForPlatformVersions(string? assetUrl)
    {
        if (string.IsNullOrWhiteSpace(assetUrl))
        {
            return;
        }

        var wordpressEvidence = ExtractWordPressVersionEvidence(assetUrl);
        if (wordpressEvidence.HasValue && wordpressEvidence.IsBetterThan(_wordpressVersion))
        {
            _wordpressVersion = wordpressEvidence;
        }

        var wooEvidence = ExtractWooCommerceVersionEvidence(assetUrl);
        if (wooEvidence.HasValue && wooEvidence.IsBetterThan(_wooCommerceVersion))
        {
            _wooCommerceVersion = wooEvidence;
        }
    }

    private static VersionEvidence ExtractWordPressVersionEvidence(string assetUrl)
    {
        if (assetUrl.IndexOf("wp-includes", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return VersionEvidence.None;
        }

        return ExtractVersionEvidence(assetUrl);
    }

    private static VersionEvidence ExtractWooCommerceVersionEvidence(string assetUrl)
    {
        if (assetUrl.IndexOf("woocommerce", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return VersionEvidence.None;
        }

        var matchesPluginFolder = assetUrl.IndexOf("wp-content/plugins/woocommerce", StringComparison.OrdinalIgnoreCase) >= 0;
        var matchesAssetsFolder = assetUrl.IndexOf("/woocommerce/", StringComparison.OrdinalIgnoreCase) >= 0
            && assetUrl.IndexOf("/assets/", StringComparison.OrdinalIgnoreCase) >= 0;

        if (!matchesPluginFolder && !matchesAssetsFolder)
        {
            return VersionEvidence.None;
        }

        return ExtractVersionEvidence(assetUrl);
    }

    private static VersionEvidence ExtractVersionEvidence(string assetUrl)
    {
        if (TryCreateEvidence(TryGetQueryVersion(assetUrl), VersionEvidence.QueryConfidence, out var evidence))
        {
            return evidence;
        }

        if (TryCreateEvidence(TryExtractPathVersion(assetUrl), VersionEvidence.PathConfidence, out evidence))
        {
            return evidence;
        }

        return VersionEvidence.None;
    }

    private static string? TryExtractPathVersion(string assetUrl)
    {
        var normalized = assetUrl;
        var fragmentIndex = normalized.IndexOf('#');
        if (fragmentIndex >= 0)
        {
            normalized = normalized[..fragmentIndex];
        }

        var queryIndex = normalized.IndexOf('?');
        if (queryIndex >= 0)
        {
            normalized = normalized[..queryIndex];
        }

        var fileName = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var match = VersionTokenRegex.Match(fileName);
        return match.Success ? match.Groups["version"].Value : null;
    }

    private static bool TryCreateEvidence(string? version, int confidence, out VersionEvidence evidence)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            evidence = VersionEvidence.None;
            return false;
        }

        var trimmed = version.Trim();
        var segments = ExtractVersionSegments(trimmed);
        if (segments.Length == 0)
        {
            evidence = VersionEvidence.None;
            return false;
        }

        evidence = new VersionEvidence(trimmed, confidence, segments);
        return true;
    }

    private static int[] ExtractVersionSegments(string version)
    {
        var tokens = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var segments = new List<int>();

        foreach (var token in tokens)
        {
            var span = token.AsSpan();
            var length = 0;
            while (length < span.Length && char.IsDigit(span[length]))
            {
                length++;
            }

            if (length == 0)
            {
                break;
            }

            if (!int.TryParse(span[..length], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                break;
            }

            segments.Add(value);

            if (length < span.Length)
            {
                break;
            }
        }

        return segments.ToArray();
    }

    private static bool IsVersionQueryKey(string key) =>
        key.Equals("ver", StringComparison.OrdinalIgnoreCase)
        || key.Equals("version", StringComparison.OrdinalIgnoreCase)
        || key.Equals("v", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<LinkedAsset> ExtractLinkedAssets(string html, Uri baseUri)
    {
        var assets = new Dictionary<string, LinkedAsset>(StringComparer.OrdinalIgnoreCase);

        foreach (Match linkMatch in LinkTagRegex.Matches(html))
        {
            var tag = linkMatch.Value;

            var hrefMatch = HrefRegex.Match(tag);
            if (!hrefMatch.Success)
            {
                continue;
            }

            var relTokens = ExtractRelTokens(LinkRelRegex.Match(tag));
            var asValue = ExtractAttributeValue(LinkAsRegex.Match(tag));
            var assetKind = DetermineLinkAssetKind(relTokens, asValue, tag);

            if (assetKind == LinkedAssetKind.None)
            {
                continue;
            }

            var resolved = ResolveUrl(baseUri, hrefMatch.Groups["url"].Value);
            if (resolved is not null && !assets.ContainsKey(resolved))
            {
                var isPreload = IsPreloadRel(relTokens);
                var preloadDescription = DeterminePreloadDescription(relTokens, assetKind);
                assets[resolved] = new LinkedAsset(resolved, assetKind, isPreload, preloadDescription);
            }
        }

        foreach (Match scriptMatch in ScriptSrcRegex.Matches(html))
        {
            if (!scriptMatch.Success)
            {
                continue;
            }

            var resolved = ResolveUrl(baseUri, scriptMatch.Groups["url"].Value);
            if (resolved is not null && !assets.ContainsKey(resolved))
            {
                assets[resolved] = new LinkedAsset(resolved, LinkedAssetKind.Script, false, null);
            }
        }

        return assets.Values;
    }

    private static LinkedAssetKind DetermineLinkAssetKind(HashSet<string> relTokens, string? asValue, string tag)
    {
        if (relTokens.Contains("stylesheet"))
        {
            return LinkedAssetKind.Stylesheet;
        }

        if (relTokens.Count == 0 && tag.IndexOf("stylesheet", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return LinkedAssetKind.Stylesheet;
        }

        var normalizedAs = string.IsNullOrWhiteSpace(asValue)
            ? null
            : asValue.Trim();

        if (relTokens.Contains("modulepreload"))
        {
            if (normalizedAs is not null && normalizedAs.Equals("style", StringComparison.OrdinalIgnoreCase))
            {
                return LinkedAssetKind.Stylesheet;
            }

            return LinkedAssetKind.Script;
        }

        if (relTokens.Contains("preload") || relTokens.Contains("prefetch"))
        {
            if (normalizedAs is not null)
            {
                if (normalizedAs.Equals("style", StringComparison.OrdinalIgnoreCase))
                {
                    return LinkedAssetKind.Stylesheet;
                }

                if (normalizedAs.Equals("script", StringComparison.OrdinalIgnoreCase))
                {
                    return LinkedAssetKind.Script;
                }
            }
        }

        return LinkedAssetKind.None;
    }

    private static bool IsPreloadRel(HashSet<string> relTokens) =>
        relTokens.Contains("preload") || relTokens.Contains("modulepreload") || relTokens.Contains("prefetch");

    private static string? DeterminePreloadDescription(HashSet<string> relTokens, LinkedAssetKind kind)
    {
        if (relTokens.Contains("modulepreload"))
        {
            return "modulepreload";
        }

        if (relTokens.Contains("preload"))
        {
            return kind switch
            {
                LinkedAssetKind.Stylesheet => "preload stylesheet",
                LinkedAssetKind.Script => "preload script",
                _ => "preload"
            };
        }

        if (relTokens.Contains("prefetch"))
        {
            return "prefetch";
        }

        return null;
    }

    private static HashSet<string> ExtractRelTokens(Match match)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!match.Success)
        {
            return tokens;
        }

        foreach (var token in match.Groups["value"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = token.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                tokens.Add(trimmed);
            }
        }

        return tokens;
    }

    private static string? ExtractAttributeValue(Match match) =>
        match.Success ? match.Groups["value"].Value : null;

    private sealed record LinkedAsset(string Url, LinkedAssetKind Kind, bool IsPreload, string? PreloadDescription);

    private enum LinkedAssetKind
    {
        None = 0,
        Stylesheet,
        Script
    }

    private readonly struct VersionEvidence
    {
        public const int QueryConfidence = 3;
        public const int PathConfidence = 2;

        public static readonly VersionEvidence None = new(string.Empty, 0, Array.Empty<int>());

        public VersionEvidence(string version, int confidence, int[] segments)
        {
            Version = version;
            Confidence = confidence;
            Segments = segments ?? Array.Empty<int>();
        }

        public string Version { get; }

        public int Confidence { get; }

        public int[] Segments { get; }

        public bool HasValue => Confidence > 0 && !string.IsNullOrEmpty(Version);

        public bool IsBetterThan(VersionEvidence other)
        {
            if (!HasValue)
            {
                return false;
            }

            if (!other.HasValue)
            {
                return true;
            }

            if (Confidence != other.Confidence)
            {
                return Confidence > other.Confidence;
            }

            var maxSegments = Math.Max(Segments.Length, other.Segments.Length);
            for (var i = 0; i < maxSegments; i++)
            {
                var current = i < Segments.Length ? Segments[i] : 0;
                var previous = i < other.Segments.Length ? other.Segments[i] : 0;

                if (current == previous)
                {
                    continue;
                }

                return current > previous;
            }

            return string.Compare(Version, other.Version, StringComparison.OrdinalIgnoreCase) > 0;
        }
    }

    private static string? ResolveUrl(Uri baseUri, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        candidate = candidate.Trim();

        if (candidate.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute))
        {
            if (absolute.Scheme is "http" or "https")
            {
                return absolute.ToString();
            }

            return null;
        }

        if (Uri.TryCreate(baseUri, candidate, out var relative))
        {
            if (relative.Scheme is "http" or "https")
            {
                return relative.ToString();
            }
        }

        return null;
    }

    private static string NormalizeSlug(string slug)
    {
        slug = slug.Trim();
        if (slug.Length == 0)
        {
            return string.Empty;
        }

        slug = slug.Trim('/');
        slug = Uri.UnescapeDataString(slug);
        slug = slug.ToLowerInvariant();
        return slug;
    }
}

public sealed record PublicExtensionDetectionSummary(
    int? MaxPages,
    long? MaxBytes,
    int ScheduledPageCount,
    int ProcessedPageCount,
    long TotalBytesDownloaded,
    bool PageLimitReached,
    bool ByteLimitReached,
    string? WordPressVersion,
    string? WooCommerceVersion);
