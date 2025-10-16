using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Web;

namespace WcScraper.Core;

public sealed class WooScraper : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public WooScraper(HttpClient? httpClient = null, bool allowLegacyTls = true)
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
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    public static string CleanBaseUrl(string baseUrl)
    {
        baseUrl = baseUrl.Trim();
        if (baseUrl.EndsWith("/")) baseUrl = baseUrl[..^1];
        return baseUrl;
    }

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
                using var resp = await _http.GetAsync(url);
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

        return all;
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
                using var resp = await _http.GetAsync(url);
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
                using var resp = await _http.GetAsync(url);
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
            using var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return new();

            var text = await resp.Content.ReadAsStringAsync();
            return DeserializeListWithRecovery<TermItem>(text, "product categories", log);
        }
        catch (TaskCanceledException ex)
        {
            log?.Report($"Categories request timed out: {ex.Message}");
            return new();
        }
        catch (AuthenticationException ex)
        {
            log?.Report($"Categories request TLS handshake failed: {ex.Message}");
            return new();
        }
        catch (IOException ex)
        {
            log?.Report($"Categories request I/O failure: {ex.Message}");
            return new();
        }
        catch (HttpRequestException ex)
        {
            log?.Report($"Categories request failed: {ex.Message}");
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
            using var resp = await _http.GetAsync(url);
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
            using var resp = await _http.GetAsync(url);
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
                    using var resp = await _http.GetAsync(url);
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

        return all;
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
                var value = Normalize(entry?.ValueAsString());
                if (value is not null)
                {
                    return value;
                }
            }

            return null;
        }

        var metaTitle = FirstNonEmpty(
            product.MetaTitle,
            ExtractFromMeta("_yoast_wpseo_title", "_rank_math_title", "rank_math_title"),
            product.YoastHead?.Title,
            product.YoastHead?.OgTitle,
            product.YoastHead?.TwitterTitle);

        var metaDescription = FirstNonEmpty(
            product.MetaDescription,
            ExtractFromMeta("_yoast_wpseo_metadesc", "_rank_math_description", "rank_math_description"),
            product.YoastHead?.Description,
            product.YoastHead?.OgDescription,
            product.YoastHead?.TwitterDescription,
            product.ShortDescription,
            product.Summary);

        var metaKeywords = FirstNonEmpty(
            product.MetaKeywords,
            ExtractFromMeta(
                "_yoast_wpseo_focuskeywords",
                "_yoast_wpseo_focuskw",
                "_yoast_wpseo_metakeywords",
                "rank_math_focus_keyword",
                "_rank_math_focus_keyword"),
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
