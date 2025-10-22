using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WcScraper.Core;

public sealed class PublicExtensionDetector : IDisposable
{
    private static readonly Regex ExtensionPathRegex = new(
        @"/wp-content/(?<type>plugins|themes)/(?<slug>[A-Za-z0-9._-]+)/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VersionTokenRegex = new(
        @"(?:^|[-_\.])(?:v(?:ersion)?[-_\.]?)?(?<version>\d+(?:\.\d+){1,})(?=$|[-_\.])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LinkTagRegex = new(
        @"<link\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ScriptSrcRegex = new(
        @"<script\b[^>]*src\s*=\s*(['\"]) (?<url>.*?)\1",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnorePatternWhitespace);

    private static readonly Regex HrefRegex = new(
        @"href\s*=\s*(['\"])(?<url>.*?)\1",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public PublicExtensionDetector(HttpClient? httpClient = null)
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

        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var urlsToProcess = new Queue<string>();
        urlsToProcess.Enqueue(baseUri.ToString());

        var findings = new Dictionary<(string Type, string Slug), PublicExtensionFootprint>();

        while (urlsToProcess.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentUrl = urlsToProcess.Dequeue();
            if (!seenUrls.Add(currentUrl))
            {
                continue;
            }

            var content = await DownloadAsync(currentUrl, log, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(content))
            {
                continue;
            }

            ScanForExtensions(currentUrl, content, findings);

            if (!followLinkedAssets || !string.Equals(currentUrl, baseUri.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var assetUrl in ExtractLinkedAssets(content, baseUri))
            {
                if (!seenUrls.Contains(assetUrl))
                {
                    urlsToProcess.Enqueue(assetUrl);
                }
            }
        }

        return findings.Values.ToList();
    }

    private async Task<string?> DownloadAsync(string url, IProgress<string>? log, CancellationToken cancellationToken)
    {
        try
        {
            log?.Report($"GET {url}");
            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode == 404)
                {
                    log?.Report($"Public extension detection received 404 for {url}");
                    return null;
                }

                response.EnsureSuccessStatusCode();
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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

        return null;
    }

    private static void ScanForExtensions(
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

            var type = match.Groups["type"].Value.Equals("themes", StringComparison.OrdinalIgnoreCase) ? "theme" : "plugin";
            var slug = NormalizeSlug(match.Groups["slug"].Value);
            if (string.IsNullOrEmpty(slug))
            {
                continue;
            }

            var assetUrl = ExtractAssetUrl(content, match);
            var versionHint = ExtractVersionHint(assetUrl, match);

            var key = (type, slug);
            if (!findings.TryGetValue(key, out var footprint))
            {
                footprint = new PublicExtensionFootprint
                {
                    Slug = slug,
                    Type = type,
                    SourceUrl = sourceUrl,
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

    private static bool IsVersionQueryKey(string key) =>
        key.Equals("ver", StringComparison.OrdinalIgnoreCase)
        || key.Equals("version", StringComparison.OrdinalIgnoreCase)
        || key.Equals("v", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> ExtractLinkedAssets(string html, Uri baseUri)
    {
        var assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match linkMatch in LinkTagRegex.Matches(html))
        {
            var tag = linkMatch.Value;
            if (tag.IndexOf("stylesheet", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            var hrefMatch = HrefRegex.Match(tag);
            if (!hrefMatch.Success)
            {
                continue;
            }

            var resolved = ResolveUrl(baseUri, hrefMatch.Groups["url"].Value);
            if (resolved is not null)
            {
                assets.Add(resolved);
            }
        }

        foreach (Match scriptMatch in ScriptSrcRegex.Matches(html))
        {
            if (!scriptMatch.Success)
            {
                continue;
            }

            var resolved = ResolveUrl(baseUri, scriptMatch.Groups["url"].Value);
            if (resolved is not null)
            {
                assets.Add(resolved);
            }
        }

        return assets;
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
}
