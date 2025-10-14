using System.Net;
using System.Net.Http;
using System.Text;
using WcScraper.Core.Shopify;
using Xunit;

namespace WcScraper.Core.Tests;

public class ShopifyScraperTests
{
    [Fact]
    public async Task FetchShopifyProductsAsync_UsesRestAndGraphEndpoints()
    {
        const string restPayload = """
        {
          "products": [
            {
              "id": 123456789,
              "title": "Sample Product",
              "body_html": "<p>Example</p>",
              "vendor": "Example Vendor",
              "product_type": "Accessories",
              "handle": "sample-product",
              "tags": "tag-a, tag-b",
              "variants": [
                {
                  "id": 1111,
                  "title": "Default Title",
                  "sku": "SKU-1",
                  "price": "19.99",
                  "compare_at_price": "24.99",
                  "inventory_quantity": 5,
                  "requires_shipping": true,
                  "weight": 0.5,
                  "weight_unit": "kg",
                  "option1": "Default Title"
                }
              ],
              "options": [
                {
                  "name": "Title",
                  "values": ["Default Title"]
                }
              ],
              "images": [
                {
                  "id": 777,
                  "src": "https://cdn.example/image.jpg",
                  "alt": "Front"
                }
              ]
            }
          ]
        }
        """;

        const string graphPayload = """
        {
          "data": {
            "products": {
              "edges": [
                {
                  "node": {
                    "handle": "sample-product",
                    "collections": {
                      "edges": [
                        {
                          "node": {
                            "id": "gid://shopify/Collection/987654321",
                            "handle": "frontpage",
                            "title": "Frontpage"
                          }
                        }
                      ]
                    }
                  }
                }
              ]
            }
          }
        }
        """;

        using var handler = new StubHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/products.json", StringComparison.Ordinal))
            {
                Assert.True(request.Headers.TryGetValues("X-Shopify-Access-Token", out var tokens));
                Assert.Contains("admin-token", tokens);
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(restPayload, Encoding.UTF8, "application/json")
                };
                return response;
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/graphql.json", StringComparison.Ordinal))
            {
                Assert.True(request.Headers.TryGetValues("X-Shopify-Storefront-Access-Token", out var tokens));
                Assert.Contains("storefront-token", tokens);
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(graphPayload, Encoding.UTF8, "application/json")
                };
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        var scraper = new ShopifyScraper(httpClient);
        var settings = new ShopifySettings("https://example.myshopify.com", "admin-token", "storefront-token");

        var products = await scraper.FetchShopifyProductsAsync(settings);

        var product = Assert.Single(products);
        Assert.Equal("sample-product", product.Handle);
        var collection = Assert.Single(product.Collections);
        Assert.Equal("frontpage", collection.Handle);
    }

    [Fact]
    public async Task FetchStoreProductsAsync_ConvertsToStoreProduct()
    {
        const string restPayload = """
        {
          "products": [
            {
              "id": 123456789,
              "title": "Sample Product",
              "body_html": "<p>Example</p>",
              "product_type": "Accessories",
              "handle": "sample-product",
              "tags": "tag-a, tag-b",
              "variants": [
                {
                  "id": 1111,
                  "title": "Default Title",
                  "sku": "SKU-1",
                  "price": "19.99",
                  "compare_at_price": "24.99",
                  "inventory_quantity": 5,
                  "requires_shipping": true,
                  "option1": "Default Title"
                }
              ],
              "options": [
                {
                  "name": "Title",
                  "values": ["Default Title"]
                }
              ],
              "images": [
                {
                  "id": 777,
                  "src": "https://cdn.example/image.jpg",
                  "alt": "Front"
                }
              ]
            }
          ]
        }
        """;

        using var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.EndsWith("/products.json", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(restPayload, Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        var scraper = new ShopifyScraper(httpClient);
        var settings = new ShopifySettings("https://example.myshopify.com", "admin-token");

        var storeProducts = await scraper.FetchStoreProductsAsync(settings);

        var storeProduct = Assert.Single(storeProducts);
        Assert.Equal("Sample Product", storeProduct.Name);
        Assert.Equal("sample-product", storeProduct.Slug);
        Assert.Equal("https://example.myshopify.com/products/sample-product", storeProduct.Permalink);
        Assert.Equal("SKU-1", storeProduct.Sku);
        Assert.True(storeProduct.IsInStock);
        Assert.Equal("tag-a", storeProduct.Tags[0].Name);
        Assert.Equal("tag-b", storeProduct.Tags[1].Name);
        Assert.Equal("https://cdn.example/image.jpg", storeProduct.Images[0].Src);
        Assert.Equal("<p>Example</p>", storeProduct.Description);
        Assert.Equal("1999", storeProduct.Prices?.Price);
        Assert.Equal("2499", storeProduct.Prices?.RegularPrice);
    }

    [Fact]
    public async Task FetchShopifyProductsAsync_UsesBasicAuthenticationWhenApiKeyProvided()
    {
        const string restPayload = """{"products":[]}""";
        var expectedToken = Convert.ToBase64String(Encoding.UTF8.GetBytes("key:secret"));

        using var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            Assert.Equal(expectedToken, request.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(restPayload, Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        var settings = new ShopifySettings("https://example.myshopify.com", apiKey: "key", apiSecret: "secret");
        var scraper = new ShopifyScraper(httpClient);

        var products = await scraper.FetchShopifyProductsAsync(settings);

        Assert.Empty(products);
    }

    [Fact]
    public async Task FetchShopifyProductsAsync_FallsBackToPublicStorefrontWhenNoCredentialsProvided()
    {
        const string restPayload = """
        {
          "products": [
            {
              "id": 123,
              "title": "Anonymous Product",
              "handle": "anonymous-product",
              "tags": "tag-a"
            }
          ]
        }
        """;

        var requestCount = 0;

        using var handler = new StubHttpMessageHandler(request =>
        {
            requestCount++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/products.json", request.RequestUri!.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("X-Shopify-Access-Token"));
            Assert.Contains("limit=50", request.RequestUri.Query, StringComparison.Ordinal);

            if (requestCount == 1)
            {
                Assert.Contains("page=1", request.RequestUri.Query, StringComparison.Ordinal);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(restPayload, Encoding.UTF8, "application/json")
                };
            }

            Assert.Contains("page=2", request.RequestUri.Query, StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"products\":[]}", Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        var settings = new ShopifySettings("https://example.myshopify.com")
        {
            PageSize = 50,
            MaxPages = 2
        };
        var scraper = new ShopifyScraper(httpClient);

        var products = await scraper.FetchShopifyProductsAsync(settings);

        var product = Assert.Single(products);
        Assert.Equal("anonymous-product", product.Handle);
        Assert.Equal(2, requestCount);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
