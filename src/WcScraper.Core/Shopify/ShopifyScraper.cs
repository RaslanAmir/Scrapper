using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Web;

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
            if (settings.HasAdminAccess)
            {
                request.Headers.Add("X-Shopify-Access-Token", settings.AdminAccessToken);
            }
            else if (settings.HasPrivateAppCredentials)
            {
                var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.ApiKey}:{settings.ApiSecret}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            }
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
