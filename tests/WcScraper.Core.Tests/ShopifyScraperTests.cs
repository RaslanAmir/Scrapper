using System;
using System.Linq;
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
    public async Task FetchCollectionsAsync_PublicStorefrontLoadsCollections()
    {
        const string pageOne = """
        {
          "collections": [
            {
              "id": 123456,
              "handle": "frontpage",
              "title": "Frontpage",
              "body_html": "<p>Frontpage collection</p>",
              "published_at": "2023-05-01T00:00:00-04:00",
              "updated_at": "2023-05-02T01:00:00-04:00",
              "sort_order": "manual",
              "template_suffix": "custom",
              "published_scope": "web",
              "products_count": "5",
              "image": {
                "id": "987",
                "src": "https://cdn.example.com/frontpage.png",
                "alt": "Frontpage hero",
                "width": "800",
                "height": 600,
                "created_at": "2023-04-30T12:00:00-04:00"
              }
            },
            {
              "id": 234567,
              "title": "Second Collection",
              "handle": "second-collection"
            }
          ]
        }
        """;

        const string emptyPage = """
        {
          "collections": []
        }
        """;

        var calls = 0;
        using var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.EndsWith("/collections.json", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            calls++;

            if (request.RequestUri!.Query.Contains("page=1", StringComparison.Ordinal))
            {
                Assert.Contains("limit=250", request.RequestUri.Query, StringComparison.Ordinal);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(pageOne, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(emptyPage, Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        var scraper = new ShopifyScraper(httpClient);
        var settings = new ShopifySettings("https://example.myshopify.com");

        var collections = await scraper.FetchCollectionsAsync(settings);

        Assert.Equal(2, collections.Terms.Count);
        var first = collections.Terms[0];
        Assert.Equal("Frontpage", first.Name);
        Assert.Equal("frontpage", first.Slug);

        var frontpage = Assert.Contains("frontpage", collections.ByHandle);
        Assert.Equal("<p>Frontpage collection</p>", frontpage.BodyHtml);
        Assert.Equal(5, frontpage.ProductsCount);
        Assert.Equal("custom", frontpage.TemplateSuffix);
        Assert.NotNull(frontpage.Image);
        Assert.Equal(987, frontpage.Image!.Id);
        Assert.Equal("Frontpage hero", frontpage.Image.Alt);
        Assert.Equal(800, frontpage.Image.Width);
        Assert.Equal(600, frontpage.Image.Height);

        var second = collections.Terms[1];
        Assert.Equal("Second Collection", second.Name);
        Assert.Equal("second-collection", second.Slug);
        Assert.Null(collections.FindByTerm(second)?.Image?.Src);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task FetchCollectionsAsync_RestCollectionsIncludeDetails()
    {
        const string customCollections = """
        {
          "custom_collections": [
            {
              "id": 101,
              "handle": "frontpage",
              "title": "Frontpage",
              "body_html": "<p>Main page</p>",
              "published_at": "2023-05-01T10:00:00-04:00",
              "updated_at": "2023-05-02T11:00:00-04:00",
              "sort_order": "alpha-asc",
              "template_suffix": "custom-template",
              "published_scope": "global",
              "products_count": 7,
              "admin_graphql_api_id": "gid://shopify/Collection/101",
              "published": true,
              "image": {
                "id": 501,
                "src": "https://cdn.example.com/frontpage.jpg",
                "alt": "Frontpage image",
                "width": 1280,
                "height": "720",
                "created_at": "2023-04-30T12:00:00-04:00"
              }
            }
          ]
        }
        """;

        const string smartCollections = """
        {
          "smart_collections": [
            {
              "id": 202,
              "handle": "automatic",
              "title": "Automatic Picks",
              "body_html": "<p>Smart rules</p>",
              "published_at": "2023-05-03T08:00:00-04:00",
              "updated_at": "2023-05-04T09:00:00-04:00",
              "sort_order": "best-selling",
              "published_scope": "web",
              "products_count": "3",
              "admin_graphql_api_id": "gid://shopify/Collection/202",
              "published": false,
              "disjunctive": true,
              "rules": [
                { "column": "tag", "relation": "equals", "condition": "Featured" },
                { "column": "title", "relation": "starts_with", "condition": "Sale" }
              ],
              "image": {
                "src": "https://cdn.example.com/automatic.png",
                "alt": "Automatic hero"
              }
            }
          ]
        }
        """;

        using var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Contains("limit=250", request.RequestUri!.Query, StringComparison.Ordinal);

            if (request.RequestUri!.AbsolutePath.EndsWith("/custom_collections.json", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(customCollections, Encoding.UTF8, "application/json")
                };
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/smart_collections.json", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(smartCollections, Encoding.UTF8, "application/json")
                };
            }

            throw new InvalidOperationException($"Unexpected URI: {request.RequestUri}");
        });

        using var httpClient = new HttpClient(handler);
        var scraper = new ShopifyScraper(httpClient);
        var settings = new ShopifySettings("https://example.myshopify.com", "admin-token");

        var collections = await scraper.FetchCollectionsAsync(settings);

        Assert.Equal(2, collections.Terms.Count);
        var frontpage = Assert.Contains(101, collections.ById);
        Assert.Equal("custom-template", frontpage.TemplateSuffix);
        Assert.True(frontpage.Published);
        Assert.Equal("gid://shopify/Collection/101", frontpage.AdminGraphqlApiId);
        Assert.NotNull(frontpage.Image);
        Assert.Equal(1280, frontpage.Image!.Width);
        Assert.Equal(720, frontpage.Image.Height);
        Assert.Equal("2023-04-30T12:00:00-04:00", frontpage.Image.CreatedAt);

        var automatic = Assert.Contains("automatic", collections.ByHandle);
        Assert.False(automatic.Published);
        Assert.True(automatic.Disjunctive);
        Assert.Equal(2, automatic.Rules.Count);
        Assert.Contains(automatic.Rules, r => r.Column == "tag" && r.Relation == "equals" && r.Condition == "Featured");

        var automaticTerm = collections.Terms.Single(t => string.Equals(t.Slug, "automatic", StringComparison.OrdinalIgnoreCase));
        Assert.Same(automatic, collections.FindByTerm(automaticTerm));
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
    public async Task FetchStoreProductsAsync_HandlesArrayTags()
    {
        const string restPayload = """
        {
          "products": [
            {
              "id": 987654321,
              "title": "Array Product",
              "body_html": "<p>Array Example</p>",
              "product_type": "Accessories",
              "handle": "array-product",
              "tags": ["tag-a", "tag-b"],
              "variants": [
                {
                  "id": 2222,
                  "title": "Default Title",
                  "sku": "SKU-ARRAY",
                  "price": "29.99",
                  "inventory_quantity": 2,
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
                  "id": 888,
                  "src": "https://cdn.example/array.jpg",
                  "alt": "Array"
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
        Assert.Equal("Array Product", storeProduct.Name);
        Assert.Collection(storeProduct.Tags,
            t => Assert.Equal("tag-a", t.Name),
            t => Assert.Equal("tag-b", t.Name));
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

    [Fact]
    public async Task FetchStoreProductsAsync_FallsBackToProductTypeCategory()
    {
        const string restPayload = """
        {
          "products": [
            {
              "id": 24680,
              "title": "Beauty Kit",
              "body_html": "<p>Includes essentials</p>",
              "product_type": "Beauty & Health",
              "handle": "beauty-kit",
              "variants": [
                {
                  "id": 13579,
                  "title": "Default Title",
                  "sku": "BK-1",
                  "price": "9.99",
                  "inventory_quantity": 3
                }
              ],
              "options": [
                {
                  "name": "Title",
                  "values": ["Default Title"]
                }
              ],
              "images": []
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
        var settings = new ShopifySettings("https://demo.myshopify.com", "admin-token");

        var storeProducts = await scraper.FetchStoreProductsAsync(settings);

        var storeProduct = Assert.Single(storeProducts);
        var category = Assert.Single(storeProduct.Categories);
        Assert.Equal("Beauty & Health", category.Name);
        Assert.Equal("beauty-health", category.Slug);
    }

    [Fact]
    public async Task FetchCollectionsAsync_WithoutCredentials_ReturnsEmptyList()
    {
        var invoked = false;
        using var handler = new StubHttpMessageHandler(request =>
        {
            invoked = true;
            throw new InvalidOperationException("HTTP request should not be sent when credentials are missing.");
        });

        using var httpClient = new HttpClient(handler);
        var scraper = new ShopifyScraper(httpClient);
        var settings = new ShopifySettings();

        var collections = await scraper.FetchCollectionsAsync(settings);

        Assert.Empty(collections.Terms);
        Assert.False(invoked);
    }

    [Fact]
    public async Task FetchProductTagsAsync_WithoutCredentials_ReturnsEmptyList()
    {
        var invoked = false;
        using var handler = new StubHttpMessageHandler(request =>
        {
            invoked = true;
            throw new InvalidOperationException("HTTP request should not be sent when credentials are missing.");
        });

        using var httpClient = new HttpClient(handler);
        var scraper = new ShopifyScraper(httpClient);
        var settings = new ShopifySettings("https://example.myshopify.com");

        var tags = await scraper.FetchProductTagsAsync(settings);

        Assert.Empty(tags);
        Assert.False(invoked);
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
