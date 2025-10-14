using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using WcScraper.Core;

namespace WcScraper.Core.Shopify;

public sealed class ShopifyScraper : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ShopifyScraper(HttpClient? httpClient = null)
    {
        if (httpClient is null)
        {
            _http = new HttpClient();
            _ownsClient = true;
        }
        else
        {
            _http = httpClient;
            _ownsClient = false;
        }

        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("wc-local-scraper-shopify/0.1 (+https://localhost)");
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    public async Task<IReadOnlyList<StoreProduct>> FetchStoreProductsAsync(
        ShopifySettings settings,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var products = await FetchShopifyProductsAsync(settings, log, cancellationToken).ConfigureAwait(false);
        return products.Select(p => ShopifyConverters.ToStoreProduct(p, settings)).ToList();
    }

    public async Task<IReadOnlyList<ShopifyProduct>> FetchShopifyProductsAsync(
        ShopifySettings settings,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (!settings.HasAdminAccess && !settings.HasPrivateAppCredentials)
        {
            throw new InvalidOperationException("Admin access token or private app credentials are required to fetch products via REST API.");
        }

        var restProducts = await FetchProductsFromRestAsync(settings, log, cancellationToken).ConfigureAwait(false);

        if (settings.HasStorefrontAccess)
        {
            await EnrichCollectionsFromGraphAsync(restProducts, settings, log, cancellationToken).ConfigureAwait(false);
        }

        return restProducts;
    }

    private async Task<List<ShopifyProduct>> FetchProductsFromRestAsync(
        ShopifySettings settings,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var all = new List<ShopifyProduct>();
        string? nextPageInfo = null;

        for (int page = 0; page < settings.MaxPages; page++)
        {
            var query = nextPageInfo is null
                ? $"limit={settings.PageSize}"
                : $"limit={settings.PageSize}&page_info={HttpUtility.UrlEncode(nextPageInfo)}";
            var uri = settings.BuildRestUri("products.json", query);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            ApplyAuthentication(request, settings);
            log?.Report($"GET {uri}");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(payload)) break;

            var data = JsonSerializer.Deserialize<ShopifyRestProductResponse>(payload, _jsonOptions);
            if (data?.Products is { Count: > 0 })
            {
                all.AddRange(data.Products);
            }
            else
            {
                break;
            }

            nextPageInfo = TryParseNextPageInfo(response.Headers);
            if (nextPageInfo is null) break;
        }

        return all;
    }

    private static string? TryParseNextPageInfo(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Link", out var values)) return null;

        foreach (var value in values)
        {
            var segments = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var segment in segments)
            {
                var parts = segment.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 2) continue;

                var linkPart = parts[0].Trim();
                if (!linkPart.StartsWith('<') || !linkPart.EndsWith('>')) continue;
                var rel = parts.Skip(1).FirstOrDefault(p => p.Contains("rel=", StringComparison.OrdinalIgnoreCase));
                if (rel is null || !rel.Contains("\"next\"", StringComparison.OrdinalIgnoreCase)) continue;

                var link = linkPart.Trim('<', '>');
                if (Uri.TryCreate(link, UriKind.Absolute, out var uri))
                {
                    var query = HttpUtility.ParseQueryString(uri.Query);
                    var pageInfo = query.Get("page_info");
                    if (!string.IsNullOrWhiteSpace(pageInfo)) return pageInfo;
                }
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<TermItem>> FetchCollectionsAsync(
        ShopifySettings settings,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (!settings.HasAdminAccess && !settings.HasPrivateAppCredentials)
        {
            throw new InvalidOperationException("Admin access token or private app credentials are required to fetch collections.");
        }

        var all = new List<TermItem>();
        foreach (var resource in new[] { "custom_collections", "smart_collections" })
        {
            var collections = await FetchCollectionsAsync(resource, settings, log, cancellationToken).ConfigureAwait(false);
            all.AddRange(collections);
        }

        return all
            .GroupBy(c => string.IsNullOrWhiteSpace(c.Slug) ? c.Name ?? c.Id.ToString(CultureInfo.InvariantCulture) : c.Slug, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(c => c.Name)
            .ToList();
    }

    public async Task<IReadOnlyList<TermItem>> FetchProductTagsAsync(
        ShopifySettings settings,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (!settings.HasAdminAccess && !settings.HasPrivateAppCredentials)
        {
            throw new InvalidOperationException("Admin access token or private app credentials are required to fetch tags.");
        }

        var tags = new List<TermItem>();
        string? nextPageInfo = null;
        var index = 1;

        do
        {
            var query = nextPageInfo is null
                ? "limit=250&order=alpha"
                : $"limit=250&order=alpha&page_info={HttpUtility.UrlEncode(nextPageInfo)}";
            var uri = settings.BuildRestUri("products/tags.json", query);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            ApplyAuthentication(request, settings);
            log?.Report($"GET {uri}");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(payload)) break;

            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("tags", out var array)) break;

            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String) continue;
                var tagName = element.GetString();
                if (string.IsNullOrWhiteSpace(tagName)) continue;

                tags.Add(new TermItem
                {
                    Id = index++,
                    Name = tagName,
                    Slug = Slugify(tagName)
                });
            }

            nextPageInfo = TryParseNextPageInfo(response.Headers);
        } while (!string.IsNullOrEmpty(nextPageInfo));

        return tags;
    }

    private async Task<List<TermItem>> FetchCollectionsAsync(
        string resource,
        ShopifySettings settings,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var results = new List<TermItem>();
        string? nextPageInfo = null;

        do
        {
            var query = nextPageInfo is null
                ? "limit=250"
                : $"limit=250&page_info={HttpUtility.UrlEncode(nextPageInfo)}";
            var uri = settings.BuildRestUri($"{resource}.json", query);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            ApplyAuthentication(request, settings);
            log?.Report($"GET {uri}");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(payload)) break;

            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty(resource, out var array)) break;

            foreach (var element in array.EnumerateArray())
            {
                var id = element.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number
                    ? idProp.GetInt64()
                    : 0L;
                var title = element.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
                var handle = element.TryGetProperty("handle", out var handleProp) ? handleProp.GetString() : null;

                results.Add(new TermItem
                {
                    Id = id != 0 ? unchecked((int)(id % int.MaxValue)) : Math.Abs((handle ?? title ?? Guid.NewGuid().ToString()).GetHashCode()),
                    Name = title,
                    Slug = !string.IsNullOrWhiteSpace(handle) ? handle : Slugify(title)
                });
            }

            nextPageInfo = TryParseNextPageInfo(response.Headers);
        } while (!string.IsNullOrEmpty(nextPageInfo));

        return results;
    }

    private static void ApplyAuthentication(HttpRequestMessage request, ShopifySettings settings)
    {
        if (settings.HasAdminAccess)
        {
            request.Headers.Remove("X-Shopify-Access-Token");
            request.Headers.Add("X-Shopify-Access-Token", settings.AdminAccessToken);
        }
        else if (settings.HasPrivateAppCredentials)
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.ApiKey}:{settings.ApiSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
    }

    private static string? Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var span = value.Trim().ToLowerInvariant().AsSpan();
        var builder = new StringBuilder(span.Length);
        foreach (var ch in span)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
            else if (char.IsWhiteSpace(ch) || ch == '-' || ch == '_')
            {
                if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? null : slug;
    }

    private async Task EnrichCollectionsFromGraphAsync(
        IEnumerable<ShopifyProduct> products,
        ShopifySettings settings,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        var handleLookup = products
            .Select(p => p.Handle)
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (handleLookup.Count == 0) return;

        const int chunkSize = 10;
        foreach (var chunk in handleLookup.Chunk(chunkSize))
        {
            var handleQuery = string.Join(" OR ", chunk.Select(h => $"handle:'{h!.Replace("'", "\\'", StringComparison.Ordinal)}'"));
            var payload = new
            {
                query = @"query ProductCollections($query: String!) {
  products(first: 10, query: $query) {
    edges {
      node {
        handle
        collections(first: 20) {
          edges {
            node {
              id
              handle
              title
            }
          }
        }
      }
    }
  }
}",
                variables = new { query = handleQuery }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, settings.BuildGraphUri())
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Add("X-Shopify-Storefront-Access-Token", settings.StorefrontAccessToken);
            log?.Report($"POST {request.RequestUri}");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) continue;

            var document = await response.Content.ReadFromJsonAsync<ShopifyGraphQlResponse<ShopifyGraphQlProducts>>(
                _jsonOptions,
                cancellationToken).ConfigureAwait(false);
            if (document?.Data?.Products?.Edges is null) continue;

            foreach (var edge in document.Data.Products.Edges)
            {
                var handle = edge.Node?.Handle;
                if (string.IsNullOrWhiteSpace(handle)) continue;
                var target = products.FirstOrDefault(p => string.Equals(p.Handle, handle, StringComparison.OrdinalIgnoreCase));
                if (target is null) continue;

                var collections = edge.Node!.Collections.Edges
                    .Select(e => e.Node)
                    .Where(n => n is not null)
                    .Select(n => new ShopifyCollection
                    {
                        Id = n!.Id,
                        Handle = n.Handle,
                        Title = n.Title
                    })
                    .ToList();
                target.Collections = collections;
            }
        }
    }
}
