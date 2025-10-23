using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace WcScraper.Core;

public sealed class WooScraper : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private HttpRetryPolicy _httpPolicy;
    private List<TermItem> _lastProductCategoryTerms = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public PublicExtensionDetectionSummary? LastPublicExtensionDetection { get; private set; }

    public WooScraper(HttpClient? httpClient = null, bool allowLegacyTls = true, HttpRetryPolicy? httpPolicy = null)
    {
        if (httpClient is null)
        {
            var handler = new SocketsHttpHandler();
            handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            if (allowLegacyTls && Enum.TryParse("Tls,Tls11", ignoreCase: true, out SslProtocols legacyProtocols))
            {
                handler.SslOptions.EnabledSslProtocols |= legacyProtocols;
            }

            _http = new HttpClient(handler, disposeHandler: true);
            _ownsClient = true;
        }
        else
        {
            _http = httpClient;
            _ownsClient = false;
        }

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("wc-local-scraper-wpf/0.1 (+https://localhost)");
        _http.Timeout = TimeSpan.FromSeconds(30);
        _httpPolicy = httpPolicy ?? new HttpRetryPolicy();
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    public IReadOnlyList<TermItem> LastFetchedProductCategories => _lastProductCategoryTerms;

    public HttpRetryPolicy HttpPolicy
    {
        get => _httpPolicy;
        set => _httpPolicy = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static string CleanBaseUrl(string baseUrl)
    {
        if (baseUrl is null)
        {
            throw new ArgumentNullException(nameof(baseUrl));
        }

        var trimmed = baseUrl.Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Base URL is required.", nameof(baseUrl));
        }

        Uri? uri = null;
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
        {
            uri = parsed;
        }
        else if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            Uri.TryCreate($"https:{trimmed}", UriKind.Absolute, out uri);
        }
        else
        {
            Uri.TryCreate($"https://{trimmed}", UriKind.Absolute, out uri);
        }

        if (uri is null || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new ArgumentException("Base URL must be a valid absolute URI (e.g., https://example.com).", nameof(baseUrl));
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Base URL must use HTTP or HTTPS (e.g., https://example.com).", nameof(baseUrl));
        }

        var path = uri.AbsolutePath;
        var query = uri.Query;

        if (path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal))
        {
            path = path.TrimEnd('/');
        }
        else if (path == "/" && string.IsNullOrEmpty(query))
        {
            path = string.Empty;
        }

        return $"{uri.Scheme}://{uri.Authority}{path}{query}";
    }

    private Task<HttpResponseMessage> GetWithRetryAsync(
        string url,
        CancellationToken cancellationToken = default,
        Action<HttpRetryAttempt>? onRetry = null)
        => _httpPolicy.SendAsync(() => _http.GetAsync(url, cancellationToken), cancellationToken, onRetry);

    private Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken = default,
        Action<HttpRetryAttempt>? onRetry = null)
        => _httpPolicy.SendAsync(async () =>
        {
            using var request = requestFactory();
            return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }, cancellationToken, onRetry);

    private Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken = default,
        Action<HttpRetryAttempt>? onRetry = null)
        => _httpPolicy.SendAsync(async () =>
        {
            using var request = requestFactory();
            return await _http.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
        }, cancellationToken, onRetry);

    public async Task<List<StoreProduct>> FetchStoreProductsAsync(
        string baseUrl,
        int perPage = 100,
        int maxPages = 100,
        IProgress<string>? log = null,
        string? categoryFilter = null,
        string? tagFilter = null)
    {
        baseUrl = CleanBaseUrl(baseUrl);
        var all = new List<StoreProduct>();

        for (int page = 1; page <= maxPages; page++)
        {
            var url = $"{baseUrl}/wp-json/wc/store/v1/products?per_page={perPage}&page={page}" +
                      (string.IsNullOrWhiteSpace(categoryFilter) ? "" : $"&category={Uri.EscapeDataString(categoryFilter)}") +
                      (string.IsNullOrWhiteSpace(tagFilter) ? "" : $"&tag={Uri.EscapeDataString(tagFilter)}");
            try
            {
                log?.Report($"GET {url}");
                using var resp = await GetWithRetryAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    if ((int)resp.StatusCode == 404) break;
                    resp.EnsureSuccessStatusCode();
                }

                var text = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(text)) break;

                var items = DeserializeListWithRecovery<StoreProduct>(text, "store products", log);
                if (items.Count == 0) break;

                foreach (var it in items)
                {
                    if (string.IsNullOrWhiteSpace(it.ShortDescription) && !string.IsNullOrWhiteSpace(it.Summary))
                    {
                        it.ShortDescription = it.Summary;
                    }

                    PopulateWooSeoMetadata(it);
                }

                all.AddRange(items);
                if (items.Count < perPage) break;
            }
            catch (TaskCanceledException ex)
            {
                log?.Report($"Store API request timed out: {ex.Message}");
                break;
            }
            catch (AuthenticationException ex)
            {
                log?.Report($"Store API TLS handshake failed: {ex.Message}");
                break;
            }
            catch (IOException ex)
            {
                log?.Report($"Store API I/O failure: {ex.Message}");
                break;
            }
            catch (HttpRequestException ex)
            {
                log?.Report($"Store API request failed: {ex.Message}");
                break;
            }
        }

        var productsNeedingSeo = all
            .Where(p => string.IsNullOrWhiteSpace(p.MetaTitle)
                        || string.IsNullOrWhiteSpace(p.MetaDescription)
                        || string.IsNullOrWhiteSpace(p.MetaKeywords))
            .ToList();

        if (productsNeedingSeo.Count > 0)
        {
            await FetchWooSeoMetadataAsync(baseUrl, productsNeedingSeo, log);
        }

        await PopulateCategoryMetadataAsync(baseUrl, all, log);
        return all;
    }

    public Task<List<WordPressPage>> FetchWordPressPagesAsync(
        string baseUrl,
        IProgress<string>? log = null,
        int perPage = 100,
        int maxPages = 100)
        => FetchWordPressContentAsync<WordPressPage>(baseUrl, "pages", perPage, maxPages, log);

    public Task<List<WordPressPost>> FetchWordPressPostsAsync(
        string baseUrl,
        IProgress<string>? log = null,
        int perPage = 100,
        int maxPages = 100)
        => FetchWordPressContentAsync<WordPressPost>(baseUrl, "posts", perPage, maxPages, log);

    public Task<FrontEndDesignSnapshotResult> FetchPublicDesignSnapshotAsync(
        string baseUrl,
        IProgress<string>? log = null,
        IEnumerable<string>? additionalPageUrls = null,
        CancellationToken cancellationToken = default)
        => FrontEndDesignSnapshot.CaptureAsync(_http, baseUrl, additionalPageUrls, log, cancellationToken, _httpPolicy);

    public async Task<List<WordPressMediaItem>> FetchWordPressMediaAsync(
        string baseUrl,
        IProgress<string>? log = null,
        int perPage = 100,
        int maxPages = 100)
    {
        baseUrl = CleanBaseUrl(baseUrl);
        var all = new List<WordPressMediaItem>();

        for (int page = 1; page <= maxPages; page++)
        {
            var url = $"{baseUrl}/wp-json/wp/v2/media?per_page={perPage}&page={page}&orderby=date&order=asc";
            try
            {
                log?.Report($"GET {url}");
                using var resp = await GetWithRetryAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    if ((int)resp.StatusCode == 404)
                    {
                        break;
                    }
                    resp.EnsureSuccessStatusCode();
                }

                var text = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(text))
                {
                    break;
                }

                var items = DeserializeListWithRecovery<WordPressMediaItem>(text, "media library", log);
                if (items.Count == 0)
                {
                    break;
                }

                foreach (var item in items)
                {
                    item.Normalize();
                }

                all.AddRange(items);
                if (items.Count < perPage)
                {
                    break;
                }
            }
            catch (TaskCanceledException ex)
            {
                log?.Report($"Media request timed out: {ex.Message}");
                break;
            }
            catch (AuthenticationException ex)
            {
                log?.Report($"Media request TLS handshake failed: {ex.Message}");
                break;
            }
            catch (IOException ex)
            {
                log?.Report($"Media request I/O failure: {ex.Message}");
                break;
            }
            catch (HttpRequestException ex)
            {
                log?.Report($"Media request failed: {ex.Message}");
                break;
            }
        }

        return all;
    }

    private static readonly IReadOnlyList<string> DefaultPublicExtensionEntryPaths = new[]
    {
        "/shop/",
        "/product/sample/",
        "/cart/",
        "/checkout/",
        "/my-account/"
    };

    public Task<List<PublicExtensionFootprint>> FetchPublicExtensionFootprintsAsync(
        string baseUrl,
        bool includeLinkedAssets = true,
        IProgress<string>? log = null,
        int? maxPages = null,
        long? maxBytes = null)
        => FetchPublicExtensionFootprintsAsync(
            baseUrl,
            includeLinkedAssets,
            log,
            additionalEntryUrls: null,
            maxPages,
            maxBytes);

    public async Task<List<PublicExtensionFootprint>> FetchPublicExtensionFootprintsAsync(
        string baseUrl,
        bool includeLinkedAssets,
        IProgress<string>? log,
        IEnumerable<string>? additionalEntryUrls,
        int? maxPages = null,
        long? maxBytes = null)
    {
        baseUrl = CleanBaseUrl(baseUrl);

        var entryCandidates = DefaultPublicExtensionEntryPaths.AsEnumerable();
        if (additionalEntryUrls is not null)
        {
            entryCandidates = entryCandidates.Concat(additionalEntryUrls);
        }

        var entryList = entryCandidates
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        LastPublicExtensionDetection = null;

        using var detector = new PublicExtensionDetector(_http, _httpPolicy);

        try
        {
            var findings = await detector
                .DetectAsync(baseUrl, includeLinkedAssets, log, entryList, maxPages, maxBytes)
                .ConfigureAwait(false);

            var wordpressVersion = detector.WordPressVersion;
            var wooCommerceVersion = detector.WooCommerceVersion;

            LastPublicExtensionDetection = BuildPublicExtensionSummary(detector);

            return findings
                .GroupBy(f => $"{f.Type}:{f.Slug}", StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var first = group.First();
                    var combinedSourceUrls = CombineSourceUrls(group);
                    var sourceUrl = combinedSourceUrls.FirstOrDefault()
                        ?? first.SourceUrl;
                    if (!string.IsNullOrWhiteSpace(sourceUrl))
                    {
                        var index = combinedSourceUrls.FindIndex(url => string.Equals(url, sourceUrl, StringComparison.OrdinalIgnoreCase));
                        if (index > 0)
                        {
                            combinedSourceUrls.RemoveAt(index);
                            combinedSourceUrls.Insert(0, sourceUrl);
                        }
                        else if (index < 0)
                        {
                            combinedSourceUrls.Insert(0, sourceUrl);
                        }
                    }
                    var assetUrl = group
                        .Select(f => f.AssetUrl)
                        .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
                    var versionHint = group
                        .Select(f => f.VersionHint)
                        .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                    return new PublicExtensionFootprint
                    {
                        Slug = first.Slug,
                        Type = first.Type,
                        SourceUrl = sourceUrl,
                        SourceUrls = combinedSourceUrls,
                        AssetUrl = assetUrl,
                        VersionHint = versionHint,
                        WordPressVersion = wordpressVersion,
                        WooCommerceVersion = wooCommerceVersion
                    };
                })
                .ToList();
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

        LastPublicExtensionDetection = BuildPublicExtensionSummary(detector);

        return new();
    }

    private static PublicExtensionDetectionSummary BuildPublicExtensionSummary(PublicExtensionDetector detector)
        => new(
            detector.LastMaxPages,
            detector.LastMaxBytes,
            detector.ScheduledPageCount,
            detector.ProcessedPageCount,
            detector.TotalBytesDownloaded,
            detector.PageLimitReached,
            detector.ByteLimitReached,
            detector.WordPressVersion,
            detector.WooCommerceVersion);

    private static List<string> CombineSourceUrls(IEnumerable<PublicExtensionFootprint> footprints)
    {
        var combined = new List<string>();
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

        foreach (var footprint in footprints)
        {
            if (footprint is null)
            {
                continue;
            }

            AddIfMissing(combined, seen, footprint.SourceUrl);

            if (footprint.SourceUrls is { Count: > 0 })
            {
                foreach (var url in footprint.SourceUrls)
                {
                    AddIfMissing(combined, seen, url);
                }
            }
        }

        return combined;
    }

    private async Task PopulateCategoryMetadataAsync(string baseUrl, List<StoreProduct> products, IProgress<string>? log)
    {
        if (products.Count == 0)
        {
            _lastProductCategoryTerms = new List<TermItem>();
            return;
        }

        var categoryTerms = await FetchProductCategoriesAsync(baseUrl, log);
        if (categoryTerms.Count == 0)
        {
            return;
        }

        var byId = categoryTerms
            .Where(term => term.Id > 0)
            .GroupBy(term => term.Id)
            .ToDictionary(group => group.Key, group => group.First(), EqualityComparer<int>.Default);

        foreach (var product in products)
        {
            if (product.Categories is null || product.Categories.Count == 0)
            {
                continue;
            }

            foreach (var category in product.Categories)
            {
                if (category is null || category.Id <= 0)
                {
                    continue;
                }

                if (!byId.TryGetValue(category.Id, out var metadata))
                {
                    continue;
                }

                category.Description = metadata.Description;
                category.Display = metadata.Display;
                category.ParentId = metadata.ParentId;
                category.MenuOrder = metadata.MenuOrder;
                category.Count = metadata.Count;
                category.Image = metadata.Image is null
                    ? null
                    : new CategoryImage
                    {
                        Id = metadata.Image.Id,
                        Src = metadata.Image.Src,
                        Alt = metadata.Image.Alt,
                        Name = metadata.Image.Name
                    };

                if (metadata.ParentId is > 0 && byId.TryGetValue(metadata.ParentId.Value, out var parentMetadata))
                {
                    category.ParentSlug = parentMetadata.Slug;
                    category.ParentName = parentMetadata.Name;
                }
                else
                {
                    category.ParentSlug = null;
                    category.ParentName = null;
                }
            }
        }
    }

    public async Task<WordPressMenuCollection?> FetchWordPressMenusAsync(
        string baseUrl,
        IProgress<string>? log = null,
        IEnumerable<string>? preferredEndpoints = null)
    {
        baseUrl = CleanBaseUrl(baseUrl);
        var endpoints = preferredEndpoints?.ToList();
        if (endpoints is null || endpoints.Count == 0)
        {
            endpoints = new List<string>
            {
                "/wp-json/wp/v2/menus",
                "/wp-json/wp-api-menus/v2/menus",
                "/wp-json/menus/v1/menus"
            };
        }

        foreach (var endpoint in endpoints)
        {
            var menuCollection = await TryFetchMenusFromEndpointAsync(baseUrl, endpoint, log);
            if (menuCollection is not null)
            {
                menuCollection.Endpoint = endpoint;
                return menuCollection;
            }
        }

        log?.Report("No WordPress menus endpoint responded.");
        return null;
    }

    public async Task<WordPressWidgetSnapshot> FetchWordPressWidgetsAsync(
        string baseUrl,
        string username,
        string applicationPassword,
        IProgress<string>? log = null)
    {
        baseUrl = CleanBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(applicationPassword))
        {
            log?.Report("Widgets request skipped: missing credentials.");
            return new WordPressWidgetSnapshot();
        }

        var snapshot = new WordPressWidgetSnapshot();

        snapshot.Areas = await FetchAuthenticatedListAsync<WordPressWidgetArea>(
            $"{baseUrl}/wp-json/wp/v2/widget-areas",
            "widget areas",
            username,
            applicationPassword,
            log);

        snapshot.Widgets = await FetchAuthenticatedListAsync<WordPressWidget>(
            $"{baseUrl}/wp-json/wp/v2/widgets",
            "widgets",
            username,
            applicationPassword,
            log);

        snapshot.WidgetTypes = await FetchAuthenticatedListAsync<WordPressWidgetType>(
            $"{baseUrl}/wp-json/wp/v2/widget-types",
            "widget types",
            username,
            applicationPassword,
            log);

        return snapshot;
    }

    public Task<List<InstalledTheme>> FetchThemesAsync(
        string baseUrl,
        string username,
        string applicationPassword,
        IProgress<string>? log = null,
        int perPage = 100)
        => FetchThemeAsync(baseUrl, username, applicationPassword, log, perPage);

    public async Task<List<InstalledPlugin>> FetchPluginsAsync(
        string baseUrl,
        string username,
        string applicationPassword,
        IProgress<string>? log = null,
        int perPage = 100)
    {
        baseUrl = CleanBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(applicationPassword))
        {
            log?.Report("Plugins request skipped: missing credentials.");
            return new();
        }

        var all = new List<InstalledPlugin>();
        bool attemptedApi = false;

        for (int page = 1; page <= 100; page++)
        {
            var url = $"{baseUrl}/wp-json/wp/v2/plugins?context=edit&per_page={perPage}&page={page}";
            try
            {
                attemptedApi = true;
                log?.Report($"GET {url} (authenticated)");
                using var resp = await SendWithRetryAsync(() =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Authorization = CreateBasicAuthHeader(username, applicationPassword);
                    return request;
                });
                if (!resp.IsSuccessStatusCode)
                {
                    if ((int)resp.StatusCode == 404)
                    {
                        log?.Report("Plugins API endpoint not found. Falling back to admin page scrape.");
                    }
                    else
                    {
                        log?.Report($"Plugins API returned {(int)resp.StatusCode} ({resp.ReasonPhrase}).");
                    }
                    all.Clear();
                    break;
                }

                var text = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(text))
                {
                    break;
                }

                var items = DeserializeListWithRecovery<InstalledPlugin>(text, "plugins", log);
                if (items.Count == 0)
                {
                    break;
                }

                foreach (var plugin in items)
                {
                    plugin.Normalize();
                }

                all.AddRange(items);
                if (items.Count < perPage)
                {
                    break;
                }
            }
            catch (TaskCanceledException ex)
            {
                log?.Report($"Plugins request timed out: {ex.Message}");
                break;
            }
            catch (AuthenticationException ex)
            {
                log?.Report($"Plugins request TLS handshake failed: {ex.Message}");
                break;
            }
            catch (IOException ex)
            {
                log?.Report($"Plugins request I/O failure: {ex.Message}");
                break;
            }
            catch (HttpRequestException ex)
            {
                log?.Report($"Plugins request failed: {ex.Message}");
                break;
            }
        }

        if (all.Count > 0)
        {
            return all;
        }

        if (attemptedApi)
        {
            log?.Report("Attempting plugins admin page scrape fallback.");
        }

        return await FetchPluginsViaAdminAsync(baseUrl, username, applicationPassword, log);
    }

    public async Task<List<InstalledTheme>> FetchThemeAsync(
        string baseUrl,
        string username,
        string applicationPassword,
        IProgress<string>? log = null,
        int perPage = 100)
    {
        baseUrl = CleanBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(applicationPassword))
        {
            log?.Report("Themes request skipped: missing credentials.");
            return new();
        }

        var all = new List<InstalledTheme>();
        bool attemptedApi = false;

        for (int page = 1; page <= 100; page++)
        {
            var url = $"{baseUrl}/wp-json/wp/v2/themes?context=edit&per_page={perPage}&page={page}";
            try
            {
                attemptedApi = true;
                log?.Report($"GET {url} (authenticated)");
                using var resp = await SendWithRetryAsync(() =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Authorization = CreateBasicAuthHeader(username, applicationPassword);
                    return request;
                });
                if (!resp.IsSuccessStatusCode)
                {
                    if ((int)resp.StatusCode == 404)
                    {
                        log?.Report("Themes API endpoint not found. Falling back to admin page scrape.");
                    }
                    else
                    {
                        log?.Report($"Themes API returned {(int)resp.StatusCode} ({resp.ReasonPhrase}).");
                    }
                    all.Clear();
                    break;
                }

                var text = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(text))
                {
                    break;
                }

                var items = DeserializeListWithRecovery<InstalledTheme>(text, "themes", log);
                if (items.Count == 0)
                {
                    break;
                }

                foreach (var theme in items)
                {
                    theme.Normalize();
                }

                all.AddRange(items);
                if (items.Count < perPage)
                {
                    break;
                }
            }
            catch (TaskCanceledException ex)
            {
                log?.Report($"Themes request timed out: {ex.Message}");
                break;
            }
            catch (AuthenticationException ex)
            {
                log?.Report($"Themes request TLS handshake failed: {ex.Message}");
                break;
            }
            catch (IOException ex)
            {
                log?.Report($"Themes request I/O failure: {ex.Message}");
                break;
            }
            catch (HttpRequestException ex)
            {
                log?.Report($"Themes request failed: {ex.Message}");
                break;
            }
        }

        if (all.Count > 0)
        {
            return all;
        }

        if (attemptedApi)
        {
            log?.Report("Attempting themes admin page scrape fallback.");
        }

        return await FetchThemesViaAdminAsync(baseUrl, username, applicationPassword, log);
    }

    public async Task<Dictionary<string, JsonElement>> FetchWordPressSettingsAsync(
        string baseUrl,
        string username,
        string applicationPassword,
        IProgress<string>? log = null)
    {
        baseUrl = CleanBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(applicationPassword))
        {
            log?.Report("WordPress settings request skipped: missing credentials.");
            return new();
        }

        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var element = await TryFetchJsonAsync(baseUrl, "/wp-json/wp/v2/settings", username, applicationPassword, log, "WordPress settings");
        if (element is not JsonElement json || json.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in json.EnumerateObject())
        {
            result[property.Name] = property.Value.Clone();
        }

        return result;
    }

    public async Task<Dictionary<string, JsonElement>> FetchPluginOptionsAsync(
        string baseUrl,
        string username,
        string applicationPassword,
        InstalledPlugin plugin,
        IProgress<string>? log = null,
        IReadOnlyDictionary<string, JsonElement>? sharedSettings = null)
    {
        if (plugin is null) throw new ArgumentNullException(nameof(plugin));

        baseUrl = CleanBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(applicationPassword))
        {
            log?.Report("Plugin options request skipped: missing credentials.");
            return new();
        }

        var identifiers = EnumeratePluginIdentifiers(plugin).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        if (sharedSettings is { Count: > 0 })
        {
            foreach (var kvp in sharedSettings)
            {
                if (MatchesIdentifier(kvp.Key, identifiers))
                {
                    result[kvp.Key] = kvp.Value.Clone();
                }
            }
        }

        foreach (var endpoint in BuildPluginOptionEndpoints(plugin))
        {
            var element = await TryFetchJsonAsync(baseUrl, endpoint, username, applicationPassword, log, $"Plugin options ({plugin.Name ?? plugin.Slug ?? plugin.PluginFile ?? "plugin"})");
            if (element is null)
            {
                continue;
            }

            MergeKeyedValues(element.Value, result);
        }

        return result;
    }

    public async Task<ExtensionAssetSnapshot> FetchPluginAssetManifestAsync(
        string baseUrl,
        string username,
        string applicationPassword,
        InstalledPlugin plugin,
        IProgress<string>? log = null)
    {
        if (plugin is null) throw new ArgumentNullException(nameof(plugin));

        baseUrl = CleanBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(applicationPassword))
        {
            log?.Report("Plugin manifest request skipped: missing credentials.");
            return new ExtensionAssetSnapshot(null, null);
        }

        foreach (var endpoint in BuildPluginManifestEndpoints(plugin))
        {
            var snapshot = await TryFetchAssetSnapshotAsync(baseUrl, endpoint, username, applicationPassword, log,
                $"Plugin manifest ({plugin.Name ?? plugin.Slug ?? plugin.PluginFile ?? "plugin"})");

            if (snapshot.Paths.Count > 0 || !string.IsNullOrWhiteSpace(snapshot.ManifestJson))
            {
                return snapshot;
            }
        }

        var slug = DeterminePluginSlug(plugin);
        if (!string.IsNullOrWhiteSpace(slug))
        {
            return new ExtensionAssetSnapshot(null, new[] { $"/wp-content/plugins/{slug}/" });
        }

        return new ExtensionAssetSnapshot(null, null);
    }

    public async Task<bool> DownloadPluginArchiveAsync(
        string baseUrl,
        string username,
        string applicationPassword,
        InstalledPlugin plugin,
        string destinationPath,
        IProgress<string>? log = null)
    {
        if (plugin is null) throw new ArgumentNullException(nameof(plugin));

        baseUrl = CleanBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(applicationPassword))
        {
            log?.Report("Plugin archive download skipped: missing credentials.");
            return false;
        }

        foreach (var endpoint in BuildPluginArchiveEndpoints(plugin))
        {
            var success = await TryDownloadBinaryAsync(baseUrl, endpoint, username, applicationPassword, destinationPath, log, $"Plugin archive ({plugin.Name ?? plugin.Slug ?? plugin.PluginFile ?? "plugin"})");
            if (success)
            {
                return true;
            }
        }

        return false;
    }

    public async Task<Dictionary<string, JsonElement>> FetchThemeOptionsAsync(
        string baseUrl,
        string username,
        string applicationPassword,
        InstalledTheme theme,
        IProgress<string>? log = null,
        IReadOnlyDictionary<string, JsonElement>? sharedSettings = null)
    {
        if (theme is null) throw new ArgumentNullException(nameof(theme));

        baseUrl = CleanBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(applicationPassword))
        {
            log?.Report("Theme options request skipped: missing credentials.");
            return new();
        }

        var identifiers = EnumerateThemeIdentifiers(theme).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        if (sharedSettings is { Count: > 0 })
        {
            foreach (var kvp in sharedSettings)
            {
                if (MatchesIdentifier(kvp.Key, identifiers))
                {
                    result[kvp.Key] = kvp.Value.Clone();
                }
            }
        }

        foreach (var endpoint in BuildThemeOptionEndpoints(theme))
        {
            var element = await TryFetchJsonAsync(baseUrl, endpoint, username, applicationPassword, log, $"Theme options ({theme.Name ?? theme.Slug ?? theme.Stylesheet ?? "theme"})");
            if (element is null)
            {
                continue;
            }

            MergeKeyedValues(element.Value, result);
        }

        return result;
    }

    public async Task<ExtensionAssetSnapshot> FetchThemeAssetManifestAsync(
        string baseUrl,
        string username,
        string applicationPassword,
        InstalledTheme theme,
        IProgress<string>? log = null)
    {
        if (theme is null) throw new ArgumentNullException(nameof(theme));

        baseUrl = CleanBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(applicationPassword))
        {
            log?.Report("Theme manifest request skipped: missing credentials.");
            return new ExtensionAssetSnapshot(null, null);
        }

        foreach (var endpoint in BuildThemeManifestEndpoints(theme))
        {
            var snapshot = await TryFetchAssetSnapshotAsync(baseUrl, endpoint, username, applicationPassword, log,
                $"Theme manifest ({theme.Name ?? theme.Slug ?? theme.Stylesheet ?? "theme"})");
            if (snapshot.Paths.Count > 0 || !string.IsNullOrWhiteSpace(snapshot.ManifestJson))
            {
                return snapshot;
            }
        }

        var slug = DetermineThemeSlug(theme);
        if (!string.IsNullOrWhiteSpace(slug))
        {
            return new ExtensionAssetSnapshot(null, new[] { $"/wp-content/themes/{slug}/" });
        }

        return new ExtensionAssetSnapshot(null, null);
    }

    public async Task<bool> DownloadThemeArchiveAsync(
        string baseUrl,
        string username,
        string applicationPassword,
        InstalledTheme theme,
        string destinationPath,
        IProgress<string>? log = null)
    {
        if (theme is null) throw new ArgumentNullException(nameof(theme));

        baseUrl = CleanBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(applicationPassword))
        {
            log?.Report("Theme archive download skipped: missing credentials.");
            return false;
        }

        foreach (var endpoint in BuildThemeArchiveEndpoints(theme))
        {
            var success = await TryDownloadBinaryAsync(baseUrl, endpoint, username, applicationPassword, destinationPath, log, $"Theme archive ({theme.Name ?? theme.Slug ?? theme.Stylesheet ?? "theme"})");
            if (success)
            {
                return true;
            }
        }

        return false;
    }

    public async Task<List<StoreReview>> FetchStoreReviewsAsync(
        string baseUrl,
        IEnumerable<int> productIds,
        int perPage = 100,
        IProgress<string>? log = null)
    {
        baseUrl = CleanBaseUrl(baseUrl);
        var all = new List<StoreReview>();
        var ids = productIds.Distinct().ToList();
        const int chunk = 20;

        for (int i = 0; i < ids.Count; i += chunk)
        {
            var slice = ids.Skip(i).Take(chunk);
            var pid = string.Join(",", slice);
            var url =
                $"{baseUrl}/wp-json/wc/store/v1/products/reviews?product_id={HttpUtility.UrlEncode(pid)}&per_page={perPage}";
            try
            {
                log?.Report($"GET {url}");
                using var resp = await GetWithRetryAsync(url);
                if (!resp.IsSuccessStatusCode) continue;

                var text = await resp.Content.ReadAsStringAsync();
                var items = DeserializeListWithRecovery<StoreReview>(text, "store reviews", log);
                if (items.Count > 0)
                {
                    all.AddRange(items);
                }
            }
            catch (TaskCanceledException ex)
            {
                log?.Report($"Reviews request timed out: {ex.Message}");
            }
            catch (AuthenticationException ex)
            {
                log?.Report($"Reviews request TLS handshake failed: {ex.Message}");
            }
            catch (IOException ex)
            {
                log?.Report($"Reviews request I/O failure: {ex.Message}");
            }
            catch (HttpRequestException ex)
            {
                log?.Report($"Reviews request failed: {ex.Message}");
            }
        }

        return all;
    }

    public async Task<List<WooStoreSetting>> FetchStoreSettingsAsync(
        string baseUrl,
        string username,
        string applicationPassword,
        IProgress<string>? log = null)
    {
        baseUrl = CleanBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(applicationPassword))
        {
            log?.Report("Store settings request skipped: missing credentials.");
            return new();
        }

        var groupsUrl = $"{baseUrl}/wp-json/wc/v3/settings";
        var groups = await FetchAuthenticatedListAsync<WooSettingGroup>(groupsUrl, "store settings groups", username, applicationPassword, log);
        if (groups.Count == 0)
        {
            return new();
        }

        var all = new List<WooStoreSetting>();
        foreach (var group in groups.Where(g => !string.IsNullOrWhiteSpace(g.Id)))
        {
            var url = $"{baseUrl}/wp-json/wc/v3/settings/{group.Id}";
            var entries = await FetchAuthenticatedListAsync<WooStoreSetting>(url, $"settings group '{group.Id}'", username, applicationPassword, log);
            foreach (var entry in entries)
            {
                entry.GroupId ??= group.Id;
            }
            all.AddRange(entries);
        }

        return all;
    }

    public async Task<List<ShippingZoneSetting>> FetchShippingZonesAsync(
        string baseUrl,
        string username,
        string applicationPassword,
        IProgress<string>? log = null)
    {
        baseUrl = CleanBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(applicationPassword))
        {
            log?.Report("Shipping zones request skipped: missing credentials.");
            return new();
        }

        var zonesUrl = $"{baseUrl}/wp-json/wc/v3/shipping/zones";
        var zones = await FetchAuthenticatedListAsync<ShippingZoneSetting>(zonesUrl, "shipping zones", username, applicationPassword, log);
        if (zones.Count == 0)
        {
            return zones;
        }

        foreach (var zone in zones)
        {
            if (zone.Id <= 0)
            {
                continue;
            }

            var locationsUrl = $"{baseUrl}/wp-json/wc/v3/shipping/zones/{zone.Id}/locations";
            var locations = await FetchAuthenticatedListAsync<ShippingZoneLocation>(locationsUrl, $"shipping zone {zone.Id} locations", username, applicationPassword, log);
            zone.Locations.Clear();
            zone.Locations.AddRange(locations);

            var methodsUrl = $"{baseUrl}/wp-json/wc/v3/shipping/zones/{zone.Id}/methods";
            var methods = await FetchAuthenticatedListAsync<ShippingZoneMethodSetting>(methodsUrl, $"shipping zone {zone.Id} methods", username, applicationPassword, log);
            zone.Methods.Clear();
            zone.Methods.AddRange(methods);
        }

        return zones;
    }

    public async Task<List<PaymentGatewaySetting>> FetchPaymentGatewaysAsync(
        string baseUrl,
        string username,
        string applicationPassword,
        IProgress<string>? log = null)
    {
        baseUrl = CleanBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(applicationPassword))
        {
            log?.Report("Payment gateways request skipped: missing credentials.");
            return new();
        }

        var url = $"{baseUrl}/wp-json/wc/v3/payment_gateways";
        return await FetchAuthenticatedListAsync<PaymentGatewaySetting>(url, "payment gateways", username, applicationPassword, log);
    }

    public async Task<List<WooCustomer>> FetchCustomersAsync(
        string baseUrl,
        string username,
        string applicationPassword,
        IProgress<string>? log = null,
        int perPage = 100,
        int maxPages = 100)
    {
        baseUrl = CleanBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(applicationPassword))
        {
            log?.Report("Customers request skipped: missing credentials.");
            return new();
        }

        var all = new List<WooCustomer>();

        for (int page = 1; page <= maxPages; page++)
        {
            var url = $"{baseUrl}/wp-json/wc/v3/customers?per_page={perPage}&page={page}&orderby=id&order=asc";
            try
            {
                log?.Report($"GET {url} (authenticated)");
                using var resp = await SendWithRetryAsync(() =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Authorization = CreateBasicAuthHeader(username, applicationPassword);
                    return request;
                });
                if (!resp.IsSuccessStatusCode)
                {
                    if (resp.StatusCode == HttpStatusCode.NotFound)
                    {
                        log?.Report("Customers endpoint returned 404.");
                    }
                    else
                    {
                        log?.Report($"Customers request failed: {(int)resp.StatusCode} ({resp.ReasonPhrase}).");
                    }
                    break;
                }

                var text = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(text))
                {
                    break;
                }

                var items = DeserializeListWithRecovery<WooCustomer>(text, "customers", log);
                if (items.Count == 0)
                {
                    break;
                }

                all.AddRange(items);
                if (items.Count < perPage)
                {
                    break;
                }
            }
            catch (TaskCanceledException ex)
            {
                log?.Report($"Customers request timed out: {ex.Message}");
                break;
            }
            catch (AuthenticationException ex)
            {
                log?.Report($"Customers request TLS handshake failed: {ex.Message}");
                break;
            }
            catch (IOException ex)
            {
                log?.Report($"Customers request I/O failure: {ex.Message}");
                break;
            }
            catch (HttpRequestException ex)
            {
                log?.Report($"Customers request failed: {ex.Message}");
                break;
            }
        }

        return all;
    }

    public async Task<List<WooOrder>> FetchOrdersAsync(
        string baseUrl,
        string username,
        string applicationPassword,
        IProgress<string>? log = null,
        int perPage = 100,
        int maxPages = 100)
    {
        baseUrl = CleanBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(applicationPassword))
        {
            log?.Report("Orders request skipped: missing credentials.");
            return new();
        }

        var all = new List<WooOrder>();

        for (int page = 1; page <= maxPages; page++)
        {
            var url = $"{baseUrl}/wp-json/wc/v3/orders?per_page={perPage}&page={page}&orderby=id&order=asc";
            try
            {
                log?.Report($"GET {url} (authenticated)");
                using var resp = await SendWithRetryAsync(() =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Authorization = CreateBasicAuthHeader(username, applicationPassword);
                    return request;
                });
                if (!resp.IsSuccessStatusCode)
                {
                    if (resp.StatusCode == HttpStatusCode.NotFound)
                    {
                        log?.Report("Orders endpoint returned 404.");
                    }
                    else
                    {
                        log?.Report($"Orders request failed: {(int)resp.StatusCode} ({resp.ReasonPhrase}).");
                    }
                    break;
                }

                var text = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(text))
                {
                    break;
                }

                var items = DeserializeListWithRecovery<WooOrder>(text, "orders", log);
                if (items.Count == 0)
                {
                    break;
                }

                all.AddRange(items);
                if (items.Count < perPage)
                {
                    break;
                }
            }
            catch (TaskCanceledException ex)
            {
                log?.Report($"Orders request timed out: {ex.Message}");
                break;
            }
            catch (AuthenticationException ex)
            {
                log?.Report($"Orders request TLS handshake failed: {ex.Message}");
                break;
            }
            catch (IOException ex)
            {
                log?.Report($"Orders request I/O failure: {ex.Message}");
                break;
            }
            catch (HttpRequestException ex)
            {
                log?.Report($"Orders request failed: {ex.Message}");
                break;
            }
        }

        return all;
    }

    public async Task<List<WooCoupon>> FetchCouponsAsync(
        string baseUrl,
        string username,
        string applicationPassword,
        IProgress<string>? log = null,
        int perPage = 100,
        int maxPages = 100)
    {
        baseUrl = CleanBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(applicationPassword))
        {
            log?.Report("Coupons request skipped: missing credentials.");
            return new();
        }

        var all = new List<WooCoupon>();

        for (int page = 1; page <= maxPages; page++)
        {
            var url = $"{baseUrl}/wp-json/wc/v3/coupons?per_page={perPage}&page={page}&orderby=id&order=asc";
            try
            {
                log?.Report($"GET {url} (authenticated)");
                using var resp = await SendWithRetryAsync(() =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Authorization = CreateBasicAuthHeader(username, applicationPassword);
                    return request;
                });
                if (!resp.IsSuccessStatusCode)
                {
                    if (resp.StatusCode == HttpStatusCode.NotFound)
                    {
                        log?.Report("Coupons endpoint returned 404.");
                    }
                    else
                    {
                        log?.Report($"Coupons request failed: {(int)resp.StatusCode} ({resp.ReasonPhrase}).");
                    }
                    break;
                }

                var text = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(text))
                {
                    break;
                }

                var items = DeserializeListWithRecovery<WooCoupon>(text, "coupons", log);
                if (items.Count == 0)
                {
                    break;
                }

                all.AddRange(items);
                if (items.Count < perPage)
                {
                    break;
                }
            }
            catch (TaskCanceledException ex)
            {
                log?.Report($"Coupons request timed out: {ex.Message}");
                break;
            }
            catch (AuthenticationException ex)
            {
                log?.Report($"Coupons request TLS handshake failed: {ex.Message}");
                break;
            }
            catch (IOException ex)
            {
                log?.Report($"Coupons request I/O failure: {ex.Message}");
                break;
            }
            catch (HttpRequestException ex)
            {
                log?.Report($"Coupons request failed: {ex.Message}");
                break;
            }
        }

        return all;
    }

    public async Task<List<WooSubscription>> FetchSubscriptionsAsync(
        string baseUrl,
        string username,
        string applicationPassword,
        IProgress<string>? log = null,
        int perPage = 100,
        int maxPages = 100)
    {
        baseUrl = CleanBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(applicationPassword))
        {
            log?.Report("Subscriptions request skipped: missing credentials.");
            return new();
        }

        var all = new List<WooSubscription>();

        for (int page = 1; page <= maxPages; page++)
        {
            var url = $"{baseUrl}/wp-json/wc/v1/subscriptions?per_page={perPage}&page={page}&orderby=id&order=asc";
            try
            {
                log?.Report($"GET {url} (authenticated)");
                using var resp = await SendWithRetryAsync(() =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Authorization = CreateBasicAuthHeader(username, applicationPassword);
                    return request;
                });
                if (!resp.IsSuccessStatusCode)
                {
                    if (resp.StatusCode == HttpStatusCode.NotFound)
                    {
                        log?.Report("Subscriptions endpoint returned 404.");
                    }
                    else
                    {
                        log?.Report($"Subscriptions request failed: {(int)resp.StatusCode} ({resp.ReasonPhrase}).");
                    }
                    break;
                }

                var text = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(text))
                {
                    break;
                }

                var items = DeserializeListWithRecovery<WooSubscription>(text, "subscriptions", log);
                if (items.Count == 0)
                {
                    break;
                }

                all.AddRange(items);
                if (items.Count < perPage)
                {
                    break;
                }
            }
            catch (TaskCanceledException ex)
            {
                log?.Report($"Subscriptions request timed out: {ex.Message}");
                break;
            }
            catch (AuthenticationException ex)
            {
                log?.Report($"Subscriptions request TLS handshake failed: {ex.Message}");
                break;
            }
            catch (IOException ex)
            {
                log?.Report($"Subscriptions request I/O failure: {ex.Message}");
                break;
            }
            catch (HttpRequestException ex)
            {
                log?.Report($"Subscriptions request failed: {ex.Message}");
                break;
            }
        }

        return all;
    }

    public async Task<List<StoreProduct>> FetchWpProductsBasicAsync(
        string baseUrl,
        int perPage = 100,
        int maxPages = 100,
        IProgress<string>? log = null)
    {
        baseUrl = CleanBaseUrl(baseUrl);
        var all = new List<StoreProduct>();

        for (int page = 1; page <= maxPages; page++)
        {
            var url = $"{baseUrl}/wp-json/wp/v2/product?per_page={perPage}&page={page}&_embed=1";
            try
            {
                log?.Report($"GET {url}");
                using var resp = await GetWithRetryAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    if ((int)resp.StatusCode == 404) break;
                    resp.EnsureSuccessStatusCode();
                }

                using JsonDocument? doc = await ParseDocumentAsync(resp, log, "Failed to parse WP product response");
                if (doc is null) break;
                if (doc.RootElement.ValueKind != JsonValueKind.Array) break;

                var arr = doc.RootElement.EnumerateArray().ToList();
                if (arr.Count == 0) break;

                foreach (var it in arr)
                {
                    var id = it.GetPropertyOrDefault("id")?.GetInt32() ?? 0;
                    var slug = it.GetPropertyOrDefault("slug")?.GetString();
                    var link = it.GetPropertyOrDefault("link")?.GetString();
                    var title = it.GetPropertyOrDefault("title")?.GetPropertyOrDefault("rendered")?.GetString();
                    var content = it.GetPropertyOrDefault("content")?.GetPropertyOrDefault("rendered")?.GetString();
                    var excerpt = it.GetPropertyOrDefault("excerpt")?.GetPropertyOrDefault("rendered")?.GetString();
                    var yoast = it.GetPropertyOrDefault("yoast_head_json");
                    YoastHead? yoastHead = null;
                    if (yoast is JsonElement yoastElement && yoastElement.ValueKind == JsonValueKind.Object)
                    {
                        try
                        {
                            yoastHead = yoastElement.Deserialize<YoastHead>(_jsonOptions);
                        }
                        catch (JsonException)
                        {
                            // ignore malformed Yoast payloads
                        }
                    }

                    var images = new List<ProductImage>();
                    var emb = it.GetPropertyOrDefault("_embedded");
                    var media = emb?.GetPropertyOrDefault("wp:featuredmedia");
                    if (media is JsonElement mediaElement && mediaElement.ValueKind == JsonValueKind.Array && mediaElement.GetArrayLength() > 0)
                    {
                        var m0 = mediaElement[0];
                        var src = m0.GetPropertyOrDefault("source_url")?.GetString();
                        if (!string.IsNullOrWhiteSpace(src))
                        {
                            images.Add(new ProductImage { Id = 0, Src = src, Alt = title });
                        }
                    }

                    var storeProduct = new StoreProduct
                    {
                        Id = id,
                        Name = title,
                        Slug = slug,
                        Permalink = link,
                        Sku = "",
                        Type = "simple",
                        Status = it.GetPropertyOrDefault("status")?.GetString()?.Trim(),
                        Description = content,
                        ShortDescription = excerpt,
                        Prices = null,
                        IsInStock = null,
                        AverageRating = null,
                        ReviewCount = null,
                        Images = images,
                        YoastHead = yoastHead,
                        MetaTitle = yoastHead?.Title,
                        MetaDescription = yoastHead?.Description,
                        MetaKeywords = yoastHead?.Keywords
                    };

                    PopulateWooSeoMetadata(storeProduct);
                    all.Add(storeProduct);
                }

                if (arr.Count < perPage) break;
            }
            catch (TaskCanceledException ex)
            {
                log?.Report($"WP REST request timed out: {ex.Message}");
                break;
            }
            catch (AuthenticationException ex)
            {
                log?.Report($"WP REST request TLS handshake failed: {ex.Message}");
                break;
            }
            catch (IOException ex)
            {
                log?.Report($"WP REST request I/O failure: {ex.Message}");
                break;
            }
            catch (HttpRequestException ex)
            {
                log?.Report($"WP REST request failed: {ex.Message}");
                break;
            }
        }

        return all;
    }

    public async Task<List<TermItem>> FetchProductCategoriesAsync(string baseUrl, IProgress<string>? log = null)
    {
        baseUrl = CleanBaseUrl(baseUrl);
        var url = $"{baseUrl}/wp-json/wc/store/v1/products/categories";
        try
        {
            log?.Report($"GET {url}");
            using var resp = await GetWithRetryAsync(url);
            if (!resp.IsSuccessStatusCode) return new();

            var text = await resp.Content.ReadAsStringAsync();
            var list = DeserializeListWithRecovery<TermItem>(text, "product categories", log);
            _lastProductCategoryTerms = new List<TermItem>(list);
            return list;
        }
        catch (TaskCanceledException ex)
        {
            log?.Report($"Categories request timed out: {ex.Message}");
            _lastProductCategoryTerms = new List<TermItem>();
            return new();
        }
        catch (AuthenticationException ex)
        {
            log?.Report($"Categories request TLS handshake failed: {ex.Message}");
            _lastProductCategoryTerms = new List<TermItem>();
            return new();
        }
        catch (IOException ex)
        {
            log?.Report($"Categories request I/O failure: {ex.Message}");
            _lastProductCategoryTerms = new List<TermItem>();
            return new();
        }
        catch (HttpRequestException ex)
        {
            log?.Report($"Categories request failed: {ex.Message}");
            _lastProductCategoryTerms = new List<TermItem>();
            return new();
        }
    }

    public async Task<List<TermItem>> FetchProductTagsAsync(string baseUrl, IProgress<string>? log = null)
    {
        baseUrl = CleanBaseUrl(baseUrl);
        var url = $"{baseUrl}/wp-json/wc/store/v1/products/tags";
        try
        {
            log?.Report($"GET {url}");
            using var resp = await GetWithRetryAsync(url);
            if (!resp.IsSuccessStatusCode) return new();

            var text = await resp.Content.ReadAsStringAsync();
            return DeserializeListWithRecovery<TermItem>(text, "product tags", log);
        }
        catch (TaskCanceledException ex)
        {
            log?.Report($"Tags request timed out: {ex.Message}");
            return new();
        }
        catch (AuthenticationException ex)
        {
            log?.Report($"Tags request TLS handshake failed: {ex.Message}");
            return new();
        }
        catch (IOException ex)
        {
            log?.Report($"Tags request I/O failure: {ex.Message}");
            return new();
        }
        catch (HttpRequestException ex)
        {
            log?.Report($"Tags request failed: {ex.Message}");
            return new();
        }
    }

    public async Task<List<TermItem>> FetchProductAttributesAsync(string baseUrl, IProgress<string>? log = null)
    {
        baseUrl = CleanBaseUrl(baseUrl);
        var url = $"{baseUrl}/wp-json/wc/store/v1/products/attributes";
        try
        {
            log?.Report($"GET {url}");
            using var resp = await GetWithRetryAsync(url);
            if (!resp.IsSuccessStatusCode) return new();

            var text = await resp.Content.ReadAsStringAsync();
            return DeserializeListWithRecovery<TermItem>(text, "product attributes", log);
        }
        catch (TaskCanceledException ex)
        {
            log?.Report($"Attributes request timed out: {ex.Message}");
            return new();
        }
        catch (AuthenticationException ex)
        {
            log?.Report($"Attributes request TLS handshake failed: {ex.Message}");
            return new();
        }
        catch (IOException ex)
        {
            log?.Report($"Attributes request I/O failure: {ex.Message}");
            return new();
        }
        catch (HttpRequestException ex)
        {
            log?.Report($"Attributes request failed: {ex.Message}");
            return new();
        }
    }

    public async Task<List<StoreProduct>> FetchStoreVariationsAsync(
        string baseUrl,
        IEnumerable<int> parentIds,
        int perPage = 100,
        IProgress<string>? log = null)
    {
        baseUrl = CleanBaseUrl(baseUrl);
        var all = new List<StoreProduct>();
        var parents = parentIds.Distinct().ToList();
        const int chunk = 20;

        for (int i = 0; i < parents.Count; i += chunk)
        {
            var slice = parents.Skip(i).Take(chunk);
            var parentParam = string.Join(",", slice);
            int page = 1;
            while (true)
            {
                var url =
                    $"{baseUrl}/wp-json/wc/store/v1/products?type=variation&parent={parentParam}&per_page={perPage}&page={page}";
                try
                {
                    log?.Report($"GET {url}");
                    using var resp = await GetWithRetryAsync(url);
                    if (!resp.IsSuccessStatusCode) break;

                    var text = await resp.Content.ReadAsStringAsync();
                    var items = DeserializeListWithRecovery<StoreProduct>(text, "product variations", log);
                    if (items.Count == 0) break;

                    all.AddRange(items);
                    if (items.Count < perPage) break;
                    page++;
                }
                catch (TaskCanceledException ex)
                {
                    log?.Report($"Variations request timed out: {ex.Message}");
                    break;
                }
                catch (AuthenticationException ex)
                {
                    log?.Report($"Variations request TLS handshake failed: {ex.Message}");
                    break;
                }
                catch (IOException ex)
                {
                    log?.Report($"Variations request I/O failure: {ex.Message}");
                    break;
                }
                catch (HttpRequestException ex)
                {
                    log?.Report($"Variations request failed: {ex.Message}");
                    break;
                }
            }
        }

        await PopulateCategoryMetadataAsync(baseUrl, all, log);
        return all;
    }

    private async Task FetchWooSeoMetadataAsync(string baseUrl, IReadOnlyCollection<StoreProduct> products, IProgress<string>? log)
    {
        if (products.Count == 0)
        {
            return;
        }

        baseUrl = CleanBaseUrl(baseUrl);
        var targets = new Dictionary<string, List<StoreProduct>>(StringComparer.OrdinalIgnoreCase);

        foreach (var product in products)
        {
            var url = ResolveProductUrl(baseUrl, product);
            if (url is null)
            {
                continue;
            }

            if (!targets.TryGetValue(url, out var list))
            {
                list = new List<StoreProduct>();
                targets[url] = list;
            }

            list.Add(product);
        }

        foreach (var pair in targets)
        {
            var url = pair.Key;
            var associatedProducts = pair.Value;

            if (associatedProducts.Count == 0)
            {
                continue;
            }

            try
            {
                log?.Report($"GET {url} (SEO fallback)");
                using var resp = await GetWithRetryAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    continue;
                }

                var html = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(html))
                {
                    continue;
                }

                var metadata = ParseSeoMetadataFromHtml(html);
                foreach (var product in associatedProducts)
                {
                    if (string.IsNullOrWhiteSpace(product.MetaTitle) && metadata.Title is not null)
                    {
                        product.MetaTitle = metadata.Title;
                    }

                    if (string.IsNullOrWhiteSpace(product.MetaDescription) && metadata.Description is not null)
                    {
                        product.MetaDescription = metadata.Description;
                    }

                    if (string.IsNullOrWhiteSpace(product.MetaKeywords) && metadata.Keywords is not null)
                    {
                        product.MetaKeywords = metadata.Keywords;
                    }
                }
            }
            catch (TaskCanceledException ex)
            {
                log?.Report($"SEO fallback request timed out: {ex.Message}");
            }
            catch (AuthenticationException ex)
            {
                log?.Report($"SEO fallback TLS handshake failed: {ex.Message}");
            }
            catch (IOException ex)
            {
                log?.Report($"SEO fallback I/O failure: {ex.Message}");
            }
            catch (HttpRequestException ex)
            {
                log?.Report($"SEO fallback request failed: {ex.Message}");
            }
        }

        foreach (var product in products)
        {
            if (!string.IsNullOrWhiteSpace(product.MetaDescription))
            {
                continue;
            }

            var fallbackDescription = Normalize(product.ShortDescription);
            if (fallbackDescription is not null)
            {
                product.MetaDescription = fallbackDescription;
            }
        }
    }

    private static string? ResolveProductUrl(string baseUrl, StoreProduct product)
    {
        if (!string.IsNullOrWhiteSpace(product.Permalink))
        {
            var permalink = product.Permalink.Trim();
            if (Uri.TryCreate(permalink, UriKind.Absolute, out var absolute))
            {
                return absolute.ToString();
            }

            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
                && Uri.TryCreate(baseUri, permalink, out var combined))
            {
                return combined.ToString();
            }
        }

        if (product.Id > 0 && Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUrlUri))
        {
            var builder = new UriBuilder(baseUrlUri)
            {
                Path = baseUrlUri.AbsolutePath,
                Query = string.IsNullOrEmpty(baseUrlUri.Query)
                    ? $"p={product.Id}"
                    : baseUrlUri.Query.TrimStart('?') + $"&p={product.Id}"
            };

            return builder.Uri.ToString();
        }

        return null;
    }

    private static readonly Regex MetaTagRegex = new("<meta\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MetaAttributeRegex = new(
        "(?<name>[a-zA-Z_:][-a-zA-Z0-9_:.]*)\\s*=\\s*(?:\"(?<value1>[^\"]*)\"|'(?<value2>[^']*)'|(?<value3>[^\\s\"'>/]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TitleTagRegex = new("<title[^>]*>(?<content>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static (string? Title, string? Description, string? Keywords) ParseSeoMetadataFromHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return (null, null, null);
        }

        var metaValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match metaMatch in MetaTagRegex.Matches(html))
        {
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match attrMatch in MetaAttributeRegex.Matches(metaMatch.Value))
            {
                var name = attrMatch.Groups["name"].Value;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var value = attrMatch.Groups["value1"].Success
                    ? attrMatch.Groups["value1"].Value
                    : attrMatch.Groups["value2"].Success
                        ? attrMatch.Groups["value2"].Value
                        : attrMatch.Groups["value3"].Value;

                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                attributes[name] = HttpUtility.HtmlDecode(value);
            }

            if (!attributes.TryGetValue("content", out var content) || string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            var key = attributes.TryGetValue("name", out var nameAttr) ? nameAttr
                : attributes.TryGetValue("property", out var propertyAttr) ? propertyAttr
                : attributes.TryGetValue("http-equiv", out var httpEquivAttr) ? httpEquivAttr
                : null;

            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            metaValues[key] = content;
        }

        var titleMatch = TitleTagRegex.Match(html);
        if (titleMatch.Success)
        {
            var rawTitle = HttpUtility.HtmlDecode(titleMatch.Groups["content"].Value);
            if (!string.IsNullOrWhiteSpace(rawTitle))
            {
                metaValues["html-title"] = rawTitle;
            }
        }

        string? GetMetaValue(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (metaValues.TryGetValue(key, out var value))
                {
                    var normalized = Normalize(value);
                    if (normalized is not null)
                    {
                        return normalized;
                    }
                }
            }

            return null;
        }

        var title = FirstNonEmpty(
            GetMetaValue("og:title"),
            GetMetaValue("twitter:title"),
            GetMetaValue("aioseo:title"),
            GetMetaValue("aioseo-title"),
            GetMetaValue("title"),
            GetMetaValue("html-title"));

        var description = FirstNonEmpty(
            GetMetaValue("description"),
            GetMetaValue("og:description"),
            GetMetaValue("twitter:description"),
            GetMetaValue("aioseo:description"),
            GetMetaValue("aioseo-description"));

        var keywords = FirstNonEmpty(
            GetMetaValue("keywords"),
            GetMetaValue("og:keywords"),
            GetMetaValue("twitter:keywords"),
            GetMetaValue("aioseo:keywords"),
            GetMetaValue("aioseo_keywords"),
            GetMetaValue("aioseo-keywords"));

        return (title, description, keywords);
    }

    private static void PopulateWooSeoMetadata(StoreProduct product)
    {
        if (product is null)
        {
            return;
        }

        string? ExtractFromMeta(params string[] keys)
        {
            if (product.MetaData is null || product.MetaData.Count == 0)
            {
                return null;
            }

            foreach (var key in keys)
            {
                var entry = product.MetaData.FirstOrDefault(m =>
                    string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                {
                    continue;
                }

                var value = ExtractMetaValue(entry, key);
                if (value is not null)
                {
                    return value;
                }
            }

            return null;
        }

        string? ExtractMetaValue(StoreMetaData entry, string key)
        {
            var raw = entry.ValueAsString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var trimmed = raw.Trim();
            if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
            {
                if (TryExtractFromAioseoJson(trimmed, key, out var fromJson))
                {
                    var normalizedJson = Normalize(fromJson);
                    if (normalizedJson is not null)
                    {
                        return normalizedJson;
                    }
                }
            }

            return Normalize(raw);
        }

        bool TryExtractFromAioseoJson(string json, string key, out string? value)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    value = null;
                    return false;
                }

                var fields = GetAioseoFieldCandidates(key);
                foreach (var field in fields)
                {
                    if (!doc.RootElement.TryGetProperty(field, out var prop))
                    {
                        continue;
                    }

                    var extracted = ExtractJsonString(prop);
                    if (extracted is not null)
                    {
                        value = extracted;
                        return true;
                    }
                }

                value = ExtractJsonString(doc.RootElement);
                return value is not null;
            }
            catch (JsonException)
            {
                value = null;
                return false;
            }
        }

        static IEnumerable<string> GetAioseoFieldCandidates(string key)
        {
            if (key.Contains("title", StringComparison.OrdinalIgnoreCase))
            {
                yield return "title";
            }

            if (key.Contains("description", StringComparison.OrdinalIgnoreCase))
            {
                yield return "description";
            }

            if (key.Contains("keyword", StringComparison.OrdinalIgnoreCase))
            {
                yield return "keywords";
                yield return "keyword";
                yield return "focus";
                yield return "focus_keyphrase";
                yield return "focusKeyphrase";
            }
        }

        static string? ExtractJsonString(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return element.ToString();
                case JsonValueKind.Array:
                {
                    var items = element
                        .EnumerateArray()
                        .Select(ExtractJsonString)
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .ToList();
                    return items.Count == 0 ? null : string.Join(", ", items);
                }
                case JsonValueKind.Object:
                {
                    if (element.TryGetProperty("value", out var valueProperty))
                    {
                        var fromValue = ExtractJsonString(valueProperty);
                        if (!string.IsNullOrWhiteSpace(fromValue))
                        {
                            return fromValue;
                        }
                    }

                    foreach (var property in element.EnumerateObject())
                    {
                        var nested = ExtractJsonString(property.Value);
                        if (!string.IsNullOrWhiteSpace(nested))
                        {
                            return nested;
                        }
                    }

                    return null;
                }
                default:
                    return null;
            }
        }

        var metaTitle = FirstNonEmpty(
            product.MetaTitle,
            ExtractFromMeta(
                "_yoast_wpseo_title",
                "_rank_math_title",
                "rank_math_title",
                "_aioseo_title",
                "_aioseop_title"),
            product.YoastHead?.Title,
            product.YoastHead?.OgTitle,
            product.YoastHead?.TwitterTitle);

        var metaDescription = FirstNonEmpty(
            product.MetaDescription,
            ExtractFromMeta(
                "_yoast_wpseo_metadesc",
                "_rank_math_description",
                "rank_math_description",
                "_aioseo_description",
                "_aioseop_description"),
            product.YoastHead?.Description,
            product.YoastHead?.OgDescription,
            product.YoastHead?.TwitterDescription);

        var metaKeywords = FirstNonEmpty(
            product.MetaKeywords,
            ExtractFromMeta(
                "_yoast_wpseo_focuskeywords",
                "_yoast_wpseo_focuskw",
                "_yoast_wpseo_metakeywords",
                "rank_math_focus_keyword",
                "_rank_math_focus_keyword",
                "_aioseo_keywords",
                "_aioseo_focus_keyword",
                "_aioseop_keywords",
                "_aioseop_focuskw"),
            product.YoastHead?.Keywords);

        if (metaKeywords is null && product.Tags is { Count: > 0 })
        {
            var keywords = product.Tags
                .Select(t => Normalize(t.Name))
                .Where(s => s is not null)
                .Select(s => s!)
                .ToList();

            if (keywords.Count == 0)
            {
                keywords = product.Tags
                    .Select(t => Normalize(t.Slug))
                    .Where(s => s is not null)
                    .Select(s => s!)
                    .ToList();
            }

            if (keywords.Count > 0)
            {
                metaKeywords = string.Join(", ", keywords);
            }
        }

        product.MetaTitle = metaTitle;
        product.MetaDescription = metaDescription;
        product.MetaKeywords = metaKeywords;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var normalized = Normalize(value);
            if (normalized is not null)
            {
                return normalized;
            }
        }

        return null;
    }

    private async Task<List<T>> FetchWordPressContentAsync<T>(
        string baseUrl,
        string resource,
        int perPage,
        int maxPages,
        IProgress<string>? log) where T : WordPressContentBase
    {
        baseUrl = CleanBaseUrl(baseUrl);
        var all = new List<T>();

        for (int page = 1; page <= maxPages; page++)
        {
            var url = $"{baseUrl}/wp-json/wp/v2/{resource}?per_page={perPage}&page={page}&_embed=1";
            try
            {
                log?.Report($"GET {url}");
                using var resp = await GetWithRetryAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    if ((int)resp.StatusCode == 404)
                    {
                        break;
                    }
                    resp.EnsureSuccessStatusCode();
                }

                var text = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(text))
                {
                    break;
                }

                var items = DeserializeListWithRecovery<T>(text, $"WordPress {resource}", log);
                if (items.Count == 0)
                {
                    break;
                }

                foreach (var item in items)
                {
                    item.Normalize();
                }

                all.AddRange(items);
                if (items.Count < perPage)
                {
                    break;
                }
            }
            catch (TaskCanceledException ex)
            {
                log?.Report($"WordPress {resource} request timed out: {ex.Message}");
                break;
            }
            catch (AuthenticationException ex)
            {
                log?.Report($"WordPress {resource} TLS handshake failed: {ex.Message}");
                break;
            }
            catch (IOException ex)
            {
                log?.Report($"WordPress {resource} request I/O failure: {ex.Message}");
                break;
            }
            catch (HttpRequestException ex)
            {
                log?.Report($"WordPress {resource} request failed: {ex.Message}");
                break;
            }
        }

        return all;
    }

    private async Task<WordPressMenuCollection?> TryFetchMenusFromEndpointAsync(
        string baseUrl,
        string endpoint,
        IProgress<string>? log)
    {
        var url = CombineEndpoint(baseUrl, endpoint);
        try
        {
            log?.Report($"GET {url}");
            using var resp = await GetWithRetryAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                if ((int)resp.StatusCode == 404)
                {
                    return null;
                }
                resp.EnsureSuccessStatusCode();
            }

            var payload = await resp.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(payload);
            List<WordPressMenu> menus;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                menus = await DeserializeMenusAsync(baseUrl, endpoint, doc.RootElement, log);
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object
                     && doc.RootElement.TryGetProperty("menus", out var menusProperty)
                     && menusProperty.ValueKind == JsonValueKind.Array)
            {
                menus = await DeserializeMenusAsync(baseUrl, endpoint, menusProperty, log);
            }
            else
            {
                log?.Report($"Unexpected menu response shape from {endpoint}.");
                return null;
            }

            var locations = await FetchMenuLocationsAsync(baseUrl, log);
            return new WordPressMenuCollection
            {
                Menus = menus,
                Locations = locations
            };
        }
        catch (TaskCanceledException ex)
        {
            log?.Report($"Menus request timed out for {endpoint}: {ex.Message}");
        }
        catch (AuthenticationException ex)
        {
            log?.Report($"Menus request TLS handshake failed for {endpoint}: {ex.Message}");
        }
        catch (IOException ex)
        {
            log?.Report($"Menus request I/O failure for {endpoint}: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            log?.Report($"Menus request failed for {endpoint}: {ex.Message}");
        }
        catch (JsonException ex)
        {
            log?.Report($"Menus response parse error for {endpoint}: {ex.Message}");
        }

        return null;
    }

    private async Task<List<WordPressMenu>> DeserializeMenusAsync(
        string baseUrl,
        string endpoint,
        JsonElement array,
        IProgress<string>? log)
    {
        var menus = new List<WordPressMenu>();
        foreach (var element in array.EnumerateArray())
        {
            WordPressMenu? menu = null;
            try
            {
                menu = element.Deserialize<WordPressMenu>(_jsonOptions);
            }
            catch (JsonException)
            {
                // ignore invalid entry
            }

            if (menu is null)
            {
                continue;
            }

            var detailed = await FetchMenuDetailAsync(baseUrl, endpoint, menu, log);
            var resolved = detailed ?? menu;

            if (resolved.Items is null || resolved.Items.Count == 0)
            {
                var fallbackItems = await FetchMenuItemsAsync(baseUrl, resolved, log);
                if (fallbackItems.Count > 0)
                {
                    resolved.Items = fallbackItems;
                }
            }

            menus.Add(resolved);
        }

        return menus;
    }

    private async Task<WordPressMenu?> FetchMenuDetailAsync(
        string baseUrl,
        string endpoint,
        WordPressMenu summary,
        IProgress<string>? log)
    {
        if (summary.Id > 0)
        {
            var detailById = await FetchMenuDetailInternalAsync(
                CombineEndpoint(baseUrl, $"{TrimEndpoint(endpoint)}/{summary.Id}"),
                log);
            if (detailById is not null)
            {
                return MergeMenu(summary, detailById);
            }
        }

        if (!string.IsNullOrWhiteSpace(summary.Slug))
        {
            var detailBySlug = await FetchMenuDetailInternalAsync(
                CombineEndpoint(baseUrl, $"{TrimEndpoint(endpoint)}/{summary.Slug}"),
                log);
            if (detailBySlug is not null)
            {
                return MergeMenu(summary, detailBySlug);
            }
        }

        return summary;
    }

    private static WordPressMenu MergeMenu(WordPressMenu summary, WordPressMenu detail)
    {
        detail.Id = detail.Id == 0 ? summary.Id : detail.Id;
        detail.Slug ??= summary.Slug;
        detail.Name ??= summary.Name;
        detail.Description ??= summary.Description;
        return detail;
    }

    private async Task<WordPressMenu?> FetchMenuDetailInternalAsync(string url, IProgress<string>? log)
    {
        try
        {
            log?.Report($"GET {url}");
            using var resp = await GetWithRetryAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                if ((int)resp.StatusCode == 404)
                {
                    return null;
                }
                resp.EnsureSuccessStatusCode();
            }

            var text = await resp.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var menu = JsonSerializer.Deserialize<WordPressMenu>(text, _jsonOptions);
            return menu;
        }
        catch (TaskCanceledException ex)
        {
            log?.Report($"Menu detail request timed out: {ex.Message}");
        }
        catch (AuthenticationException ex)
        {
            log?.Report($"Menu detail TLS failure: {ex.Message}");
        }
        catch (IOException ex)
        {
            log?.Report($"Menu detail I/O failure: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            log?.Report($"Menu detail request failed: {ex.Message}");
        }
        catch (JsonException ex)
        {
            log?.Report($"Menu detail parse error: {ex.Message}");
        }

        return null;
    }

    private async Task<List<WordPressMenuItem>> FetchMenuItemsAsync(
        string baseUrl,
        WordPressMenu menu,
        IProgress<string>? log)
    {
        if (menu is null)
        {
            return new List<WordPressMenuItem>();
        }

        var candidates = new List<string>
        {
            "/wp-json/wp/v2/menu-items",
            "/wp-json/wp-api-menus/v2/menu-items",
            "/wp-json/menus/v1/menu-items"
        };

        foreach (var candidate in candidates)
        {
            var items = await FetchMenuItemsFromEndpointAsync(baseUrl, candidate, menu, log);
            if (items.Count > 0)
            {
                return BuildMenuTree(items);
            }
        }

        return new List<WordPressMenuItem>();
    }

    private async Task<List<WordPressMenuItem>> FetchMenuItemsFromEndpointAsync(
        string baseUrl,
        string endpoint,
        WordPressMenu menu,
        IProgress<string>? log)
    {
        var queries = BuildMenuItemQueryCandidates(menu);
        var results = new List<WordPressMenuItem>();

        foreach (var query in queries)
        {
            var collected = new List<WordPressMenuItem>();

            for (var page = 1; page <= 50; page++)
            {
                var baseEndpoint = CombineEndpoint(baseUrl, endpoint);
                var separator = baseEndpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?';
                var url = $"{baseEndpoint}{separator}per_page=100&page={page}";
                if (!string.IsNullOrWhiteSpace(query))
                {
                    url += $"&{query}";
                }

                try
                {
                    log?.Report($"GET {url}");
                    using var resp = await GetWithRetryAsync(url);
                    if (!resp.IsSuccessStatusCode)
                    {
                        if ((int)resp.StatusCode is 400 or 404)
                        {
                            break;
                        }

                        resp.EnsureSuccessStatusCode();
                    }

                    var text = await resp.Content.ReadAsStringAsync();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        break;
                    }

                    var batch = DeserializeListWithRecovery<WordPressMenuItem>(text, "menu items", log);
                    if (batch.Count == 0)
                    {
                        break;
                    }

                    collected.AddRange(batch);
                    if (batch.Count < 100)
                    {
                        break;
                    }
                }
                catch (TaskCanceledException ex)
                {
                    log?.Report($"Menu items request timed out for {endpoint}: {ex.Message}");
                    break;
                }
                catch (AuthenticationException ex)
                {
                    log?.Report($"Menu items TLS failure for {endpoint}: {ex.Message}");
                    break;
                }
                catch (IOException ex)
                {
                    log?.Report($"Menu items I/O failure for {endpoint}: {ex.Message}");
                    break;
                }
                catch (HttpRequestException ex)
                {
                    log?.Report($"Menu items request failed for {endpoint}: {ex.Message}");
                    break;
                }
            }

            if (collected.Count > 0)
            {
                results = collected;
                break;
            }
        }

        return results;
    }

    private static List<string> BuildMenuItemQueryCandidates(WordPressMenu menu)
    {
        var queries = new List<string>();
        if (menu.Id > 0)
        {
            queries.Add($"menus={menu.Id}");
            queries.Add($"menu={menu.Id}");
        }

        if (!string.IsNullOrWhiteSpace(menu.Slug))
        {
            var encoded = Uri.EscapeDataString(menu.Slug);
            queries.Add($"slug={encoded}");
            queries.Add($"menu_slug={encoded}");
        }

        queries.Add(string.Empty);
        return queries
            .Where(q => q is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<WordPressMenuItem> BuildMenuTree(List<WordPressMenuItem> flatItems)
    {
        if (flatItems.Count == 0)
        {
            return flatItems;
        }

        var byId = flatItems
            .Where(i => i.Id > 0)
            .ToDictionary(i => i.Id, i => i, EqualityComparer<int>.Default);

        foreach (var item in flatItems)
        {
            item.Children = new List<WordPressMenuItem>();
        }

        var roots = new List<WordPressMenuItem>();

        foreach (var item in flatItems)
        {
            if (item.ParentId is int parent && parent > 0 && byId.TryGetValue(parent, out var parentItem) && !ReferenceEquals(parentItem, item))
            {
                parentItem.Children.Add(item);
            }
            else
            {
                roots.Add(item);
            }
        }

        void SortChildren(WordPressMenuItem node)
        {
            node.Children = node.Children
                .OrderBy(child => child.Order ?? child.Id)
                .ToList();

            foreach (var child in node.Children)
            {
                SortChildren(child);
            }
        }

        foreach (var root in roots)
        {
            SortChildren(root);
        }

        return roots
            .OrderBy(item => item.Order ?? item.Id)
            .ToList();
    }

    private async Task<List<WordPressMenuLocation>> FetchMenuLocationsAsync(string baseUrl, IProgress<string>? log)
    {
        var url = $"{baseUrl}/wp-json/wp/v2/menu-locations";
        try
        {
            log?.Report($"GET {url}");
            using var resp = await GetWithRetryAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                if ((int)resp.StatusCode == 404)
                {
                    return new List<WordPressMenuLocation>();
                }
                resp.EnsureSuccessStatusCode();
            }

            var text = await resp.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                return new List<WordPressMenuLocation>();
            }

            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var list = JsonSerializer.Deserialize<List<WordPressMenuLocation>>(text, _jsonOptions);
                return list ?? new List<WordPressMenuLocation>();
            }

            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var locations = new List<WordPressMenuLocation>();
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    try
                    {
                        var location = property.Value.Deserialize<WordPressMenuLocation>(_jsonOptions) ?? new WordPressMenuLocation();
                        location.Slug ??= property.Name;
                        locations.Add(location);
                    }
                    catch (JsonException)
                    {
                        // ignore invalid entry
                    }
                }

                return locations;
            }
        }
        catch (TaskCanceledException ex)
        {
            log?.Report($"Menu locations request timed out: {ex.Message}");
        }
        catch (AuthenticationException ex)
        {
            log?.Report($"Menu locations TLS failure: {ex.Message}");
        }
        catch (IOException ex)
        {
            log?.Report($"Menu locations I/O failure: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            log?.Report($"Menu locations request failed: {ex.Message}");
        }
        catch (JsonException ex)
        {
            log?.Report($"Menu locations parse error: {ex.Message}");
        }

        return new List<WordPressMenuLocation>();
    }

    private static string CombineEndpoint(string baseUrl, string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return baseUrl;
        }

        if (endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return endpoint;
        }

        if (!endpoint.StartsWith('/'))
        {
            endpoint = "/" + endpoint;
        }

        return baseUrl + endpoint;
    }

    private static string TrimEndpoint(string endpoint)
        => endpoint.TrimEnd('/');

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();


    private List<T> DeserializeListWithRecovery<T>(string json, string entityName, IProgress<string>? log)
    {
        try
        {
            var items = JsonSerializer.Deserialize<List<T>>(json, _jsonOptions);
            return items ?? new();
        }
        catch (JsonException ex)
        {

            return RecoverList<T>(json, entityName, log, ex);
        }
        catch (InvalidOperationException ex)
        {
            return RecoverList<T>(json, entityName, log, ex);

        }
    }

    private List<T> RecoverList<T>(string json, string entityName, IProgress<string>? log, Exception ex)
    {
        log?.Report($"Failed to parse {entityName}: {ex.Message}. Attempting to recover remaining entries.");
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new();
            }

            var recovered = new List<T>();
            var failedIds = new List<string>();

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                try
                {
                    var item = element.Deserialize<T>(_jsonOptions);
                    if (item is not null)
                    {
                        recovered.Add(item);
                    }
                }
                catch (JsonException)
                {
                    failedIds.Add(ExtractElementId(element));
                }
                catch (InvalidOperationException)
                {
                    failedIds.Add(ExtractElementId(element));
                }
            }

            if (failedIds.Count > 0)
            {
                var preview = string.Join(", ", failedIds.Where(id => !string.IsNullOrWhiteSpace(id)).DefaultIfEmpty("?").Take(10));
                if (failedIds.Count > 10)
                {
                    preview += ", …";
                }

                log?.Report($"Skipped {failedIds.Count} {entityName} due to parse errors (IDs: {preview}).");
            }

            return recovered;
        }
        catch (JsonException recoveryEx)
        {
            log?.Report($"Failed to recover {entityName}: {recoveryEx.Message}");
            return new();
        }
        catch (InvalidOperationException recoveryEx)
        {
            log?.Report($"Failed to recover {entityName}: {recoveryEx.Message}");
            return new();
        }
    }

    private static string ExtractElementId(JsonElement element)
    {
        var idProp = element.GetPropertyOrDefault("id");
        if (idProp is JsonElement idElement)
        {
            return idElement.ValueKind switch
            {
                JsonValueKind.String => idElement.GetString() ?? string.Empty,
                JsonValueKind.Number when idElement.TryGetInt64(out var id) => id.ToString(CultureInfo.InvariantCulture),
                JsonValueKind.Number => idElement.GetRawText(),
                _ => string.Empty
            };
        }

        return string.Empty;
    }

    private static async Task<JsonDocument?> ParseDocumentAsync(
        HttpResponseMessage response,
        IProgress<string>? log,
        string errorPrefix)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync();
            return await JsonDocument.ParseAsync(stream);
        }
        catch (JsonException ex)
        {
            log?.Report($"{errorPrefix}: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            log?.Report($"{errorPrefix}: {ex.Message}");
        }

        return null;
    }

    private async Task<JsonElement?> TryFetchJsonAsync(
        string baseUrl,
        string path,
        string username,
        string applicationPassword,
        IProgress<string>? log,
        string context)
    {
        var url = CombineUrl(baseUrl, path);
        try
        {
            log?.Report($"GET {url} (authenticated)");
            using var resp = await SendWithRetryAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = CreateBasicAuthHeader(username, applicationPassword);
                return request;
            }, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode)
            {
                if ((int)resp.StatusCode != 404)
                {
                    log?.Report($"{context} endpoint returned {(int)resp.StatusCode} ({resp.ReasonPhrase}).");
                }
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            return doc.RootElement.Clone();
        }
        catch (TaskCanceledException ex)
        {
            log?.Report($"{context} request timed out: {ex.Message}");
        }
        catch (AuthenticationException ex)
        {
            log?.Report($"{context} request TLS handshake failed: {ex.Message}");
        }
        catch (IOException ex)
        {
            log?.Report($"{context} request I/O failure: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            log?.Report($"{context} request failed: {ex.Message}");
        }
        catch (JsonException ex)
        {
            log?.Report($"{context} response parsing failed: {ex.Message}");
        }

        return null;
    }

    private async Task<ExtensionAssetSnapshot> TryFetchAssetSnapshotAsync(
        string baseUrl,
        string path,
        string username,
        string applicationPassword,
        IProgress<string>? log,
        string context)
    {
        var element = await TryFetchJsonAsync(baseUrl, path, username, applicationPassword, log, context);
        if (element is not JsonElement json)
        {
            return new ExtensionAssetSnapshot(null, null);
        }

        var clone = json.Clone();
        var raw = clone.GetRawText();
        var paths = ExtractAssetPaths(clone);
        return new ExtensionAssetSnapshot(raw, paths);
    }

    private async Task<List<string>> TryFetchStringListAsync(
        string baseUrl,
        string path,
        string username,
        string applicationPassword,
        IProgress<string>? log,
        string context)
    {
        var element = await TryFetchJsonAsync(baseUrl, path, username, applicationPassword, log, context);
        if (element is not JsonElement json)
        {
            return new();
        }

        var items = ExtractStringList(json);
        return items;
    }

    private async Task<bool> TryDownloadBinaryAsync(
        string baseUrl,
        string path,
        string username,
        string applicationPassword,
        string destinationPath,
        IProgress<string>? log,
        string context)
    {
        var url = CombineUrl(baseUrl, path);
        try
        {
            log?.Report($"GET {url} (authenticated)");
            using var resp = await SendWithRetryAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = CreateBasicAuthHeader(username, applicationPassword);
                return request;
            }, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode)
            {
                if ((int)resp.StatusCode != 404)
                {
                    log?.Report($"{context} endpoint returned {(int)resp.StatusCode} ({resp.ReasonPhrase}).");
                }
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var source = await resp.Content.ReadAsStreamAsync();
            await using var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(target);
            log?.Report($"{context} downloaded to {destinationPath}.");
            return true;
        }
        catch (TaskCanceledException ex)
        {
            log?.Report($"{context} download timed out: {ex.Message}");
        }
        catch (AuthenticationException ex)
        {
            log?.Report($"{context} download TLS handshake failed: {ex.Message}");
        }
        catch (IOException ex)
        {
            log?.Report($"{context} download I/O failure: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            log?.Report($"{context} download failed: {ex.Message}");
        }

        return false;
    }

    private static void MergeKeyedValues(JsonElement element, Dictionary<string, JsonElement> destination)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            destination[property.Name] = property.Value.Clone();
        }
    }

    private static IEnumerable<string> EnumeratePluginIdentifiers(InstalledPlugin plugin)
    {
        if (!string.IsNullOrWhiteSpace(plugin.Slug))
        {
            yield return plugin.Slug!;
        }

        if (!string.IsNullOrWhiteSpace(plugin.PluginFile))
        {
            var pluginFile = plugin.PluginFile!;
            yield return pluginFile;

            var slash = pluginFile.IndexOf('/');
            if (slash > 0)
            {
                yield return pluginFile[..slash];
            }

            var segments = pluginFile.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var last = segments.LastOrDefault();
            if (!string.IsNullOrWhiteSpace(last))
            {
                if (last.EndsWith(".php", StringComparison.OrdinalIgnoreCase))
                {
                    yield return last[..^4];
                }
                else
                {
                    yield return last;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(plugin.Name))
        {
            yield return plugin.Name!;
        }
    }

    private static IEnumerable<string> EnumerateThemeIdentifiers(InstalledTheme theme)
    {
        if (!string.IsNullOrWhiteSpace(theme.Slug))
        {
            yield return theme.Slug!;
        }
        if (!string.IsNullOrWhiteSpace(theme.Stylesheet))
        {
            yield return theme.Stylesheet!;
        }
        if (!string.IsNullOrWhiteSpace(theme.Template))
        {
            yield return theme.Template!;
        }
        if (!string.IsNullOrWhiteSpace(theme.Name))
        {
            yield return theme.Name!;
        }
    }

    private static bool MatchesIdentifier(string key, IEnumerable<string> identifiers)
    {
        foreach (var identifier in identifiers)
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                continue;
            }

            if (key.Contains(identifier, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> BuildPluginOptionEndpoints(InstalledPlugin plugin)
    {
        var slug = DeterminePluginSlug(plugin);
        if (!string.IsNullOrWhiteSpace(slug))
        {
            var esc = Uri.EscapeDataString(slug);
            yield return $"/wp-json/wc-scraper/v1/plugins/{esc}/options";
            yield return $"/wp-json/wc-scraper/v1/plugin-options?slug={esc}";
            yield return $"/?rest_route=/wc-scraper/v1/plugin-options&slug={esc}";
            yield return $"/wp-json/{slug}/v1/options";
            yield return $"/wp-json/{slug}/v1/settings";
        }

        if (!string.IsNullOrWhiteSpace(plugin.PluginFile))
        {
            var segments = plugin.PluginFile.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length > 0)
            {
                var fileStem = segments.Last();
                if (fileStem.EndsWith(".php", StringComparison.OrdinalIgnoreCase))
                {
                    fileStem = fileStem[..^4];
                }

                if (!string.IsNullOrWhiteSpace(fileStem))
                {
                    yield return $"/wp-json/{fileStem}/v1/options";
                    yield return $"/wp-json/{fileStem}/v1/settings";
                }
            }
        }
    }

    private static IEnumerable<string> BuildPluginManifestEndpoints(InstalledPlugin plugin)
    {
        var slug = DeterminePluginSlug(plugin);
        if (!string.IsNullOrWhiteSpace(slug))
        {
            var esc = Uri.EscapeDataString(slug);
            yield return $"/wp-json/wc-scraper/v1/plugins/{esc}/manifest";
            yield return $"/wp-json/wc-scraper/v1/plugin-manifest?slug={esc}";
            yield return $"/?rest_route=/wc-scraper/v1/plugin-manifest&slug={esc}";
        }
    }

    private static IEnumerable<string> BuildPluginArchiveEndpoints(InstalledPlugin plugin)
    {
        var slug = DeterminePluginSlug(plugin);
        if (!string.IsNullOrWhiteSpace(slug))
        {
            var esc = Uri.EscapeDataString(slug);
            yield return $"/wp-json/wc-scraper/v1/plugins/{esc}/archive";
            yield return $"/wp-json/wc-scraper/v1/plugin-archive?slug={esc}";
            yield return $"/?rest_route=/wc-scraper/v1/plugin-archive&slug={esc}";
        }

        if (!string.IsNullOrWhiteSpace(plugin.Update?.Package))
        {
            yield return plugin.Update.Package!;
        }
    }

    private static IEnumerable<string> BuildThemeOptionEndpoints(InstalledTheme theme)
    {
        var slug = DetermineThemeSlug(theme);
        if (!string.IsNullOrWhiteSpace(slug))
        {
            var esc = Uri.EscapeDataString(slug);
            yield return $"/wp-json/wc-scraper/v1/themes/{esc}/options";
            yield return $"/wp-json/wc-scraper/v1/theme-options?slug={esc}";
            yield return $"/?rest_route=/wc-scraper/v1/theme-options&slug={esc}";
            yield return $"/wp-json/{slug}/v1/options";
            yield return $"/wp-json/{slug}/v1/settings";
        }

        if (!string.IsNullOrWhiteSpace(theme.Stylesheet))
        {
            yield return $"/wp-json/{theme.Stylesheet}/v1/options";
            yield return $"/wp-json/{theme.Stylesheet}/v1/settings";
        }
    }

    private static IEnumerable<string> BuildThemeManifestEndpoints(InstalledTheme theme)
    {
        var slug = DetermineThemeSlug(theme);
        if (!string.IsNullOrWhiteSpace(slug))
        {
            var esc = Uri.EscapeDataString(slug);
            yield return $"/wp-json/wc-scraper/v1/themes/{esc}/manifest";
            yield return $"/wp-json/wc-scraper/v1/theme-manifest?slug={esc}";
            yield return $"/?rest_route=/wc-scraper/v1/theme-manifest&slug={esc}";
        }
    }

    private static IEnumerable<string> BuildThemeArchiveEndpoints(InstalledTheme theme)
    {
        var slug = DetermineThemeSlug(theme);
        if (!string.IsNullOrWhiteSpace(slug))
        {
            var esc = Uri.EscapeDataString(slug);
            yield return $"/wp-json/wc-scraper/v1/themes/{esc}/archive";
            yield return $"/wp-json/wc-scraper/v1/theme-archive?slug={esc}";
            yield return $"/?rest_route=/wc-scraper/v1/theme-archive&slug={esc}";
        }
    }

    private static string? DeterminePluginSlug(InstalledPlugin plugin)
    {
        if (!string.IsNullOrWhiteSpace(plugin.Slug))
        {
            return plugin.Slug;
        }

        if (!string.IsNullOrWhiteSpace(plugin.PluginFile))
        {
            var pluginFile = plugin.PluginFile;
            var slash = pluginFile.IndexOf('/');
            if (slash > 0)
            {
                return pluginFile[..slash];
            }

            if (pluginFile.EndsWith(".php", StringComparison.OrdinalIgnoreCase))
            {
                return pluginFile[..^4];
            }
        }

        if (!string.IsNullOrWhiteSpace(plugin.Name))
        {
            return plugin.Name;
        }

        return null;
    }

    private static string? DetermineThemeSlug(InstalledTheme theme)
    {
        if (!string.IsNullOrWhiteSpace(theme.Slug))
        {
            return theme.Slug;
        }

        if (!string.IsNullOrWhiteSpace(theme.Stylesheet))
        {
            return theme.Stylesheet;
        }

        if (!string.IsNullOrWhiteSpace(theme.Template))
        {
            return theme.Template;
        }

        if (!string.IsNullOrWhiteSpace(theme.Name))
        {
            return theme.Name;
        }

        return null;
    }

    private static string CombineUrl(string baseUrl, string pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl))
        {
            return baseUrl;
        }

        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        if (pathOrUrl.StartsWith('?'))
        {
            return baseUrl + pathOrUrl;
        }

        return baseUrl.TrimEnd('/') + "/" + pathOrUrl.TrimStart('/');
    }

    private static List<string> ExtractStringList(JsonElement element)
    {
        var items = new List<string>();

        void AddIfValid(JsonElement candidate)
        {
            if (candidate.ValueKind == JsonValueKind.String)
            {
                var value = candidate.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    items.Add(value);
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                AddIfValid(item);
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "files", "paths", "items" })
            {
                if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in property.EnumerateArray())
                    {
                        AddIfValid(item);
                    }
                }
            }
        }

        return items
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> ExtractAssetPaths(JsonElement element)
    {
        var paths = ExtractStringList(element);
        if (paths.Count > 0)
        {
            return paths;
        }

        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Harvest(JsonElement node)
        {
            switch (node.ValueKind)
            {
                case JsonValueKind.String:
                {
                    var value = node.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        results.Add(value);
                    }
                    break;
                }
                case JsonValueKind.Array:
                {
                    foreach (var child in node.EnumerateArray())
                    {
                        Harvest(child);
                    }
                    break;
                }
                case JsonValueKind.Object:
                {
                    foreach (var property in node.EnumerateObject())
                    {
                        if (IsPathLikeProperty(property.Name))
                        {
                            Harvest(property.Value);
                        }
                        else if (property.Value.ValueKind == JsonValueKind.Array || property.Value.ValueKind == JsonValueKind.Object)
                        {
                            Harvest(property.Value);
                        }
                    }
                    break;
                }
            }
        }

        Harvest(element);
        return results.ToList();
    }

    private static bool IsPathLikeProperty(string name)
    {
        return name.Equals("path", StringComparison.OrdinalIgnoreCase)
               || name.Equals("paths", StringComparison.OrdinalIgnoreCase)
               || name.Equals("file", StringComparison.OrdinalIgnoreCase)
               || name.Equals("files", StringComparison.OrdinalIgnoreCase)
               || name.Equals("href", StringComparison.OrdinalIgnoreCase)
               || name.Equals("src", StringComparison.OrdinalIgnoreCase)
               || name.Equals("relative_path", StringComparison.OrdinalIgnoreCase)
               || name.Equals("url", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<List<InstalledPlugin>> FetchPluginsViaAdminAsync(
        string baseUrl,
        string username,
        string applicationPassword,
        IProgress<string>? log)
    {
        var url = $"{baseUrl}/wp-admin/plugins.php";
        try
        {
            log?.Report($"GET {url} (scrape)");
            using var resp = await SendWithRetryAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = CreateBasicAuthHeader(username, applicationPassword);
                return request;
            });
            if (!resp.IsSuccessStatusCode)
            {
                log?.Report($"Plugins admin page returned {(int)resp.StatusCode} ({resp.ReasonPhrase}).");
                return new();
            }

            var html = await resp.Content.ReadAsStringAsync();
            var parsed = ParsePluginsFromHtml(html);
            foreach (var plugin in parsed)
            {
                plugin.Normalize();
            }

            if (parsed.Count == 0)
            {
                log?.Report("No plugins detected on admin page.");
            }

            return parsed;
        }
        catch (TaskCanceledException ex)
        {
            log?.Report($"Plugins fallback request timed out: {ex.Message}");
            return new();
        }
        catch (AuthenticationException ex)
        {
            log?.Report($"Plugins fallback TLS handshake failed: {ex.Message}");
            return new();
        }
        catch (IOException ex)
        {
            log?.Report($"Plugins fallback I/O failure: {ex.Message}");
            return new();
        }
        catch (HttpRequestException ex)
        {
            log?.Report($"Plugins fallback request failed: {ex.Message}");
            return new();
        }
    }

    private async Task<List<InstalledTheme>> FetchThemesViaAdminAsync(
        string baseUrl,
        string username,
        string applicationPassword,
        IProgress<string>? log)
    {
        var url = $"{baseUrl}/wp-admin/themes.php";
        try
        {
            log?.Report($"GET {url} (scrape)");
            using var resp = await SendWithRetryAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = CreateBasicAuthHeader(username, applicationPassword);
                return request;
            });
            if (!resp.IsSuccessStatusCode)
            {
                log?.Report($"Themes admin page returned {(int)resp.StatusCode} ({resp.ReasonPhrase}).");
                return new();
            }

            var html = await resp.Content.ReadAsStringAsync();
            var parsed = ParseThemesFromHtml(html);
            foreach (var theme in parsed)
            {
                theme.Normalize();
            }

            if (parsed.Count == 0)
            {
                log?.Report("No themes detected on admin page.");
            }

            return parsed;
        }
        catch (TaskCanceledException ex)
        {
            log?.Report($"Themes fallback request timed out: {ex.Message}");
            return new();
        }
        catch (AuthenticationException ex)
        {
            log?.Report($"Themes fallback TLS handshake failed: {ex.Message}");
            return new();
        }
        catch (IOException ex)
        {
            log?.Report($"Themes fallback I/O failure: {ex.Message}");
            return new();
        }
        catch (HttpRequestException ex)
        {
            log?.Report($"Themes fallback request failed: {ex.Message}");
            return new();
        }
    }

    private async Task<List<T>> FetchAuthenticatedListAsync<T>(
        string url,
        string entityName,
        string username,
        string applicationPassword,
        IProgress<string>? log)
    {
        try
        {
            log?.Report($"GET {url} (authenticated)");
            using var resp = await SendWithRetryAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = CreateBasicAuthHeader(username, applicationPassword);
                return request;
            });
            if (!resp.IsSuccessStatusCode)
            {
                if ((int)resp.StatusCode == 404)
                {
                    log?.Report($"{entityName} endpoint returned 404.");
                }
                else
                {
                    log?.Report($"{entityName} request failed: {(int)resp.StatusCode} ({resp.ReasonPhrase}).");
                }
                return new();
            }

            var text = await resp.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                return new();
            }

            return DeserializeListWithRecovery<T>(text, entityName, log);
        }
        catch (TaskCanceledException ex)
        {
            log?.Report($"{entityName} request timed out: {ex.Message}");
        }
        catch (AuthenticationException ex)
        {
            log?.Report($"{entityName} TLS handshake failed: {ex.Message}");
        }
        catch (IOException ex)
        {
            log?.Report($"{entityName} request I/O failure: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            log?.Report($"{entityName} request failed: {ex.Message}");
        }

        return new();
    }

    private static AuthenticationHeaderValue CreateBasicAuthHeader(string username, string password)
    {
        var token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{username}:{password}"));
        return new AuthenticationHeaderValue("Basic", token);
    }

    private static List<InstalledPlugin> ParsePluginsFromHtml(string html)
    {
        var list = new List<InstalledPlugin>();
        if (string.IsNullOrWhiteSpace(html))
        {
            return list;
        }

        var rowRegex = new Regex(@"<tr[^>]*class=""(?<class>[^""]*plugin[^""]*)""(?<attrs>[^>]*)>(?<content>.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        foreach (Match match in rowRegex.Matches(html))
        {
            var classes = match.Groups["class"].Value;
            var attrs = match.Groups["attrs"].Value;
            var content = match.Groups["content"].Value;
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            var plugin = new InstalledPlugin
            {
                Slug = ExtractAttribute(attrs, "data-slug"),
                PluginFile = ExtractAttribute(attrs, "data-plugin"),
                Status = classes.Contains("inactive", StringComparison.OrdinalIgnoreCase) ? "inactive" :
                    (classes.Contains("active", StringComparison.OrdinalIgnoreCase) ? "active" : null),
                AutoUpdate = classes.Contains("auto-update-enabled", StringComparison.OrdinalIgnoreCase)
                    ? true
                    : classes.Contains("auto-update-disabled", StringComparison.OrdinalIgnoreCase)
                        ? false
                        : null,
            };

            var nameMatch = Regex.Match(content, @"<strong[^>]*>\s*(?<name>[^<]+)", RegexOptions.IgnoreCase);
            if (nameMatch.Success)
            {
                plugin.Name = HttpUtility.HtmlDecode(nameMatch.Groups["name"].Value.Trim());
            }

            var versionMatch = Regex.Match(content, @"Version\s*(?<version>[0-9A-Za-z._-]+)", RegexOptions.IgnoreCase);
            if (versionMatch.Success)
            {
                plugin.Version = HttpUtility.HtmlDecode(versionMatch.Groups["version"].Value.Trim());
            }

            if (!string.IsNullOrWhiteSpace(plugin.Name))
            {
                list.Add(plugin);
            }
        }

        return list;
    }

    private static List<InstalledTheme> ParseThemesFromHtml(string html)
    {
        var list = new List<InstalledTheme>();
        if (string.IsNullOrWhiteSpace(html))
        {
            return list;
        }

        var themeRegex = new Regex(@"<div[^>]*class=""(?<class>[^""]*theme[^""]*)""(?<attrs>[^>]*)>(?<content>.*?)(?=<div[^>]*class=""[^""]*theme[^""]*""|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        foreach (Match match in themeRegex.Matches(html))
        {
            var classes = match.Groups["class"].Value;
            var attrs = match.Groups["attrs"].Value;
            var content = match.Groups["content"].Value;
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            var theme = new InstalledTheme
            {
                Slug = ExtractAttribute(attrs, "data-slug"),
                Stylesheet = ExtractAttribute(attrs, "data-slug"),
                Template = ExtractAttribute(attrs, "data-template"),
                Status = classes.Contains("active", StringComparison.OrdinalIgnoreCase) ? "active" : "inactive",
                AutoUpdate = DetermineAutoUpdate(attrs, classes)
            };

            var nameMatch = Regex.Match(content, @"class=""theme-name""[^>]*>\s*(?<name>[^<]+)", RegexOptions.IgnoreCase);
            if (!nameMatch.Success)
            {
                nameMatch = Regex.Match(content, @"aria-label=""(?<name>[^""]+)""", RegexOptions.IgnoreCase);
            }
            if (nameMatch.Success)
            {
                theme.Name = HttpUtility.HtmlDecode(nameMatch.Groups["name"].Value.Trim());
            }

            var versionMatch = Regex.Match(content, @"Version[:\s]*<span[^>]*>\s*(?<version>[^<]+)", RegexOptions.IgnoreCase);
            if (!versionMatch.Success)
            {
                versionMatch = Regex.Match(content, @"Version[:\s]*(?<version>[0-9A-Za-z._-]+)", RegexOptions.IgnoreCase);
            }
            if (versionMatch.Success)
            {
                theme.Version = HttpUtility.HtmlDecode(versionMatch.Groups["version"].Value.Trim());
            }

            if (!string.IsNullOrWhiteSpace(theme.Name))
            {
                list.Add(theme);
            }
        }

        return list;
    }

    private static bool? DetermineAutoUpdate(string attrs, string classes)
    {
        var attrValue = ExtractAttribute(attrs, "data-autoupdate");
        if (!string.IsNullOrWhiteSpace(attrValue))
        {
            if (attrValue.Equals("enabled", StringComparison.OrdinalIgnoreCase)
                || attrValue.Equals("on", StringComparison.OrdinalIgnoreCase)
                || attrValue.Equals("1"))
            {
                return true;
            }

            if (attrValue.Equals("disabled", StringComparison.OrdinalIgnoreCase)
                || attrValue.Equals("off", StringComparison.OrdinalIgnoreCase)
                || attrValue.Equals("0"))
            {
                return false;
            }
        }

        if (classes.Contains("autoupdate-enabled", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (classes.Contains("autoupdate-disabled", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return null;
    }

    private static string? ExtractAttribute(string input, string attributeName)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var pattern = attributeName + "\\s*=\\s*\"(?<value>[^\"]*)\"";
        var match = Regex.Match(input, pattern, RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var raw = match.Groups["value"].Value;
            return string.IsNullOrWhiteSpace(raw) ? null : HttpUtility.HtmlDecode(raw.Trim());
        }

        return null;
    }
}

internal static class JsonExt
{
    public static JsonElement? GetPropertyOrDefault(this JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var prop))
        {
            return prop;
        }

        return null;
    }
}
