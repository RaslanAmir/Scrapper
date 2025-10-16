using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
              "created_at": "2024-01-02T03:04:05-05:00",
              "updated_at": "2024-01-03T03:04:05-05:00",
              "published_at": "2024-01-04T03:04:05-05:00",
              "template_suffix": "custom",
              "published_scope": "web",
              "metafields_global_title_tag": "Sample Meta Title",
              "metafields_global_description_tag": "Sample Meta Description",
              "admin_graphql_api_id": "gid://shopify/Product/123456789",
              "tags": "tag-a, tag-b",
              "variants": [
                {
                  "id": 1111,
                  "product_id": 123456789,
                  "title": "Default Title",
                  "sku": "SKU-1",
                  "price": "19.99",
                  "compare_at_price": "24.99",
                  "position": 1,
                  "fulfillment_service": "manual",
                  "inventory_management": "shopify",
                  "inventory_policy": "deny",
                  "inventory_item_id": 987654,
                  "inventory_quantity": 5,
                  "old_inventory_quantity": 3,
                  "requires_shipping": true,
                  "taxable": true,
                  "tax_code": "P0000000",
                  "barcode": "123456789012",
                  "grams": 500,
                  "weight": 0.5,
                  "weight_unit": "kg",
                  "created_at": "2024-01-02T03:04:05-05:00",
                  "updated_at": "2024-01-03T03:04:05-05:00",
                  "admin_graphql_api_id": "gid://shopify/ProductVariant/1111",
                  "image_id": 777,
                  "option1": "Default Title",
                  "presentment_prices": [
                    {
                      "price": {
                        "amount": "19.99",
                        "currency_code": "USD"
                      },
                      "compare_at_price": {
                        "amount": "24.99",
                        "currency_code": "USD"
                      }
                    }
                  ],
                  "tax_lines": [
                    {
                      "title": "State Tax",
                      "price": "1.50",
                      "rate": 0.075,
                      "channel_liable": false
                    }
                  ]
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
                  "product_id": 123456789,
                  "src": "https://cdn.example/image.jpg",
                  "alt": "Front",
                  "position": 1,
                  "created_at": "2024-01-02T03:04:05-05:00",
                  "updated_at": "2024-01-03T03:04:05-05:00",
                  "width": 1024,
                  "height": 768,
                  "variant_ids": [1111],
                  "admin_graphql_api_id": "gid://shopify/ProductImage/777"
                }
              ],
              "image": {
                "id": 777,
                "product_id": 123456789,
                "src": "https://cdn.example/image.jpg",
                "alt": "Front",
                "position": 1,
                "created_at": "2024-01-02T03:04:05-05:00",
                "updated_at": "2024-01-03T03:04:05-05:00",
                "width": 1024,
                "height": 768,
                "variant_ids": [1111],
                "admin_graphql_api_id": "gid://shopify/ProductImage/777"
              }
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
        Assert.Equal("Sample Meta Title", product.MetafieldsGlobalTitleTag);
        Assert.Equal("Sample Meta Description", product.MetafieldsGlobalDescriptionTag);
        var collection = Assert.Single(product.Collections);
        Assert.Equal("frontpage", collection.Handle);
    }

    [Fact]
    public void ShopifyProduct_DeserializesFullRestPayload()
    {
        const string payload = """
        {
          "products": [
            {
              "id": 321,
              "title": "Full",
              "body_html": "<p>Details</p>",
              "vendor": "Vendor",
              "product_type": "Type",
              "handle": "full",
              "created_at": "2024-02-01T10:00:00-05:00",
              "updated_at": "2024-02-02T10:00:00-05:00",
              "published_at": "2024-02-03T10:00:00-05:00",
              "template_suffix": "template",
              "published_scope": "global",
              "status": "active",
              "admin_graphql_api_id": "gid://shopify/Product/321",
              "metafields_global_title_tag": "Meta Title",
              "metafields_global_description_tag": "Meta Description",
              "tags": ["one", "two"],
              "variants": [
                {
                  "id": 654,
                  "product_id": 321,
                  "title": "Variant",
                  "sku": "SKU",
                  "price": "9.99",
                  "compare_at_price": "14.99",
                  "position": "1",
                  "fulfillment_service": "manual",
                  "inventory_management": "shopify",
                  "inventory_policy": "deny",
                  "inventory_item_id": "7001",
                  "inventory_quantity": 7,
                  "old_inventory_quantity": 5,
                  "requires_shipping": true,
                  "taxable": true,
                  "tax_code": "P0000000",
                  "barcode": "999",
                  "grams": 200,
                  "weight": 0.2,
                  "weight_unit": "kg",
                  "created_at": "2024-02-01T10:00:00-05:00",
                  "updated_at": "2024-02-02T10:00:00-05:00",
                  "admin_graphql_api_id": "gid://shopify/ProductVariant/654",
                  "image_id": "91011",
                  "option1": "Default",
                  "presentment_prices": [
                    {
                      "price": { "amount": "9.99", "currency_code": "USD" },
                      "compare_at_price": { "amount": "14.99", "currency_code": "USD" }
                    }
                  ],
                  "tax_lines": [
                    { "title": "State", "price": "0.70", "rate": 0.07, "channel_liable": true }
                  ]
                }
              ],
              "options": [ { "name": "Title", "values": ["Default"] } ],
              "images": [
                {
                  "id": 91011,
                  "product_id": "321",
                  "src": "https://cdn/image.png",
                  "alt": "Alt",
                  "position": "1",
                  "created_at": "2024-02-01T10:00:00-05:00",
                  "updated_at": "2024-02-02T10:00:00-05:00",
                  "width": "1000",
                  "height": "800",
                  "variant_ids": [654],
                  "admin_graphql_api_id": "gid://shopify/ProductImage/91011"
                }
              ],
              "image": {
                "id": 91011,
                "product_id": 321,
                "src": "https://cdn/image.png",
                "alt": "Alt",
                "position": 1,
                "created_at": "2024-02-01T10:00:00-05:00",
                "updated_at": "2024-02-02T10:00:00-05:00",
                "width": 1000,
                "height": 800,
                "variant_ids": [654],
                "admin_graphql_api_id": "gid://shopify/ProductImage/91011"
              }
            }
          ]
        }
        """;

        var response = JsonSerializer.Deserialize<ShopifyRestProductResponse>(payload);
        Assert.NotNull(response);
        var product = Assert.Single(response!.Products);
        Assert.Equal("2024-02-01T10:00:00-05:00", product.CreatedAt);
        Assert.Equal("2024-02-02T10:00:00-05:00", product.UpdatedAt);
        Assert.Equal("global", product.PublishedScope);
        Assert.Equal("gid://shopify/Product/321", product.AdminGraphqlApiId);
        Assert.Equal("Meta Title", product.MetafieldsGlobalTitleTag);
        Assert.Equal("Meta Description", product.MetafieldsGlobalDescriptionTag);
        var variant = Assert.Single(product.Variants);
        Assert.Equal(321, variant.ProductId);
        Assert.Equal("manual", variant.FulfillmentService);
        Assert.Equal("shopify", variant.InventoryManagement);
        Assert.Equal("deny", variant.InventoryPolicy);
        Assert.Equal(7001, variant.InventoryItemId);
        Assert.Equal(5, variant.OldInventoryQuantity);
        Assert.True(variant.Taxable);
        Assert.Equal("P0000000", variant.TaxCode);
        Assert.Equal("999", variant.Barcode);
        Assert.Equal("2024-02-01T10:00:00-05:00", variant.CreatedAt);
        Assert.Equal(91011, variant.ImageId);
        Assert.Single(variant.PresentmentPrices);
        Assert.Single(variant.TaxLines);
        var image = Assert.Single(product.Images);
        Assert.Equal(321, image.ProductId);
        Assert.Equal(1000, image.Width);
        Assert.Equal(800, image.Height);
        Assert.Equal("gid://shopify/ProductImage/91011", image.AdminGraphqlApiId);
        Assert.NotNull(product.Image);
        Assert.Equal(91011, product.Image!.Id);
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

    [Fact]
    public void ToShopifyDetailDictionary_ProducesFlattenedPayload()
    {
        var product = new ShopifyProduct
        {
            Id = 1,
            Title = "Product",
            BodyHtml = "<p>Body</p>",
            Vendor = "Vendor",
            ProductType = "Type",
            Handle = "product",
            Status = "active",
            CreatedAt = "2024-01-01T00:00:00-05:00",
            UpdatedAt = "2024-01-02T00:00:00-05:00",
            PublishedAt = "2024-01-03T00:00:00-05:00",
            TemplateSuffix = "suffix",
            PublishedScope = "web",
            AdminGraphqlApiId = "gid://shopify/Product/1",
            Tags = new List<string> { "one", "two" },
            Variants =
            {
                new ShopifyVariant
                {
                    Id = 11,
                    ProductId = 1,
                    Title = "Default",
                    Price = "9.99",
                    InventoryQuantity = 5
                }
            },
            Options =
            {
                new ShopifyOption { Name = "Size", Values = { "S", "M" } }
            },
            Images =
            {
                new ShopifyImage { Id = 21, ProductId = 1, Src = "https://cdn/img.png", Width = 100, Height = 200 }
            },
            Image = new ShopifyImage { Id = 21, ProductId = 1, Src = "https://cdn/img.png" },
            Collections =
            {
                new ShopifyCollection { Handle = "frontpage", Title = "Frontpage" },
                new ShopifyCollection { Handle = "FrontPage", Title = "Duplicate case" },
                new ShopifyCollection { Title = "No Handle" }
            }
        };

        var detail = ShopifyConverters.ToShopifyDetailDictionary(product);

        Assert.Equal("Product", detail["title"]);
        Assert.Equal("2024-01-01T00:00:00-05:00", detail["created_at"]);
        Assert.Equal("web", detail["published_scope"]);
        Assert.Equal("one, two", detail["tags"]);

        var variantsJson = Assert.IsType<string>(detail["variants_json"]);
        using var variantsDoc = JsonDocument.Parse(variantsJson);
        Assert.Equal(11, variantsDoc.RootElement[0].GetProperty("id").GetInt64());

        var optionsJson = Assert.IsType<string>(detail["options_json"]);
        using var optionsDoc = JsonDocument.Parse(optionsJson);
        Assert.Equal("Size", optionsDoc.RootElement[0].GetProperty("name").GetString());

        var imagesJson = Assert.IsType<string>(detail["images_json"]);
        using var imagesDoc = JsonDocument.Parse(imagesJson);
        Assert.Equal(21, imagesDoc.RootElement[0].GetProperty("id").GetInt64());

        Assert.Equal("frontpage", detail["collection_handles"]);
        var handlesJson = Assert.IsType<string>(detail["collection_handles_json"]);
        using var handlesDoc = JsonDocument.Parse(handlesJson);
        Assert.Equal("frontpage", handlesDoc.RootElement[0].GetString());

        var imageJson = Assert.IsType<string>(detail["image_json"]);
        using var imageDoc = JsonDocument.Parse(imageJson);
        Assert.Equal(21, imageDoc.RootElement.GetProperty("id").GetInt64());
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
