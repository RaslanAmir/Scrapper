using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WcScraper.Core;
using Xunit;

namespace WcScraper.Core.Tests;

public class WooProvisioningServiceTests
{
    [Fact]
    public async Task ProvisionAsync_VariableProduct_CreatesVariations()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var service = new WooProvisioningService(httpClient);
        var settings = new WooProvisioningSettings("https://target.example", "ck", "cs");

        var parent = new StoreProduct
        {
            Id = 101,
            Name = "Parent Product",
            Slug = "parent-product",
            Sku = "PARENT-SKU",
            Type = "variable",
            Attributes =
            {
                new VariationAttribute { AttributeKey = "pa_color", Option = "Blue" }
            }
        };

        var variation = new StoreProduct
        {
            Id = 301,
            ParentId = 101,
            Name = "Parent Product - Blue",
            Slug = "parent-product-blue",
            Sku = "PARENT-SKU-BLU",
            Prices = new PriceInfo { RegularPrice = "29.99" },
            StockStatus = "instock",
            Attributes =
            {
                new VariationAttribute { AttributeKey = "pa_color", Option = "Blue" }
            },
            Images =
            {
                new ProductImage { Src = "https://example.com/blue.png", Alt = "Blue" }
            }
        };

        var logs = new List<string>();
        await service.ProvisionAsync(
            settings,
            new[] { parent },
            variableProducts: new[] { new ProvisioningVariableProduct(parent, new[] { variation }) },
            progress: new Progress<string>(logs.Add));

        Assert.Contains(logs, message => message.Contains("Provisioning 1 variations", StringComparison.Ordinal));
        Assert.Contains(logs, message => message.Contains("Creating variation 'PARENT-SKU-BLU'", StringComparison.Ordinal));

        var productCall = handler.Calls.Single(call => call.Method == HttpMethod.Post && call.Path == "/wp-json/wc/v3/products");
        using (var doc = JsonDocument.Parse(productCall.Content))
        {
            Assert.Equal("variable", doc.RootElement.GetProperty("type").GetString());
        }

        var variationCall = handler.Calls.Single(call => call.Method == HttpMethod.Post && call.Path == "/wp-json/wc/v3/products/200/variations");
        using (var doc = JsonDocument.Parse(variationCall.Content))
        {
            var root = doc.RootElement;
            Assert.Equal("PARENT-SKU-BLU", root.GetProperty("sku").GetString());
            Assert.Equal("29.99", root.GetProperty("regular_price").GetString());
            Assert.Equal("instock", root.GetProperty("stock_status").GetString());
            var attributes = root.GetProperty("attributes").EnumerateArray().ToList();
            Assert.Single(attributes);
            Assert.Equal(10, attributes[0].GetProperty("id").GetInt32());
            Assert.Equal("Blue", attributes[0].GetProperty("option").GetString());
            var image = root.GetProperty("image");
            Assert.Equal("https://example.com/blue.png", image.GetProperty("src").GetString());
        }

        Assert.Contains(handler.Calls, call => call.Method == HttpMethod.Post && call.Path == "/wp-json/wc/v3/products");
    }

    [Fact]
    public async Task ProvisionAsync_VariationsCollection_CreatesVariations()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var service = new WooProvisioningService(httpClient);
        var settings = new WooProvisioningSettings("https://target.example", "ck", "cs");

        var parent = new StoreProduct
        {
            Id = 102,
            Name = "Parent From Variations",
            Slug = "parent-from-variations",
            Sku = "PARENT-SKU",
            Type = "simple"
        };

        var variation = new StoreProduct
        {
            Id = 401,
            ParentId = 102,
            Name = "Parent From Variations - Large",
            Slug = "parent-from-variations-large",
            Sku = "PARENT-SKU-LRG",
            Prices = new PriceInfo { RegularPrice = "39.99" },
            StockStatus = "instock",
            Attributes =
            {
                new VariationAttribute { AttributeKey = "pa_size", Option = "Large" }
            },
            Images =
            {
                new ProductImage { Src = "https://example.com/large.png", Alt = "Large" }
            }
        };

        var logs = new List<string>();
        await service.ProvisionAsync(
            settings,
            new[] { parent },
            variations: new[] { variation },
            progress: new Progress<string>(logs.Add));

        Assert.Contains(logs, message => message.Contains("Provisioning 1 variations", StringComparison.Ordinal));
        Assert.Contains(logs, message => message.Contains("Creating variation 'PARENT-SKU-LRG'", StringComparison.Ordinal));

        var productCall = handler.Calls.Single(call => call.Method == HttpMethod.Post && call.Path == "/wp-json/wc/v3/products");
        using (var doc = JsonDocument.Parse(productCall.Content))
        {
            Assert.Equal("variable", doc.RootElement.GetProperty("type").GetString());
        }

        var variationCall = handler.Calls.Single(call => call.Method == HttpMethod.Post && call.Path == "/wp-json/wc/v3/products/200/variations");
        using (var doc = JsonDocument.Parse(variationCall.Content))
        {
            var root = doc.RootElement;
            Assert.Equal("PARENT-SKU-LRG", root.GetProperty("sku").GetString());
            Assert.Equal("39.99", root.GetProperty("regular_price").GetString());
            Assert.Equal("instock", root.GetProperty("stock_status").GetString());
            var attributes = root.GetProperty("attributes").EnumerateArray().ToList();
            Assert.Single(attributes);
            Assert.Equal(10, attributes[0].GetProperty("id").GetInt32());
            Assert.Equal("Large", attributes[0].GetProperty("option").GetString());
            var image = root.GetProperty("image");
            Assert.Equal("https://example.com/large.png", image.GetProperty("src").GetString());
        }
    }

    [Fact]
    public async Task ProvisionAsync_CreatesCategoryHierarchyBeforeChildren()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var service = new WooProvisioningService(httpClient);
        var settings = new WooProvisioningSettings("https://target.example", "ck", "cs");

        var product = new StoreProduct
        {
            Id = 42,
            Name = "Child Product",
            Categories =
            {
                new Category { Id = 200, Name = "Child", Slug = "child", ParentId = 100 },
                new Category { Id = 100, Name = "Parent", Slug = "parent" }
            }
        };

        await service.ProvisionAsync(settings, new[] { product });

        var categoryPosts = handler.Calls
            .Where(call => call.Method == HttpMethod.Post && call.Path == "/wp-json/wc/v3/products/categories")
            .ToList();

        Assert.Equal(2, categoryPosts.Count);

        using (var parentDoc = JsonDocument.Parse(categoryPosts[0].Content))
        {
            var root = parentDoc.RootElement;
            Assert.Equal("parent", root.GetProperty("slug").GetString());
            Assert.False(root.TryGetProperty("parent", out _));
        }

        using (var childDoc = JsonDocument.Parse(categoryPosts[1].Content))
        {
            var root = childDoc.RootElement;
            Assert.Equal("child", root.GetProperty("slug").GetString());
            var parentId = handler.GetCreatedCategoryId("parent");
            Assert.NotNull(parentId);
            Assert.Equal(parentId.Value, root.GetProperty("parent").GetInt32());
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Query, string? Content)> Calls { get; } = new();

        private readonly Dictionary<string, int> _createdCategoryIds = new(StringComparer.OrdinalIgnoreCase);
        private int _nextCategoryId = 500;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri!.Query;
            var content = request.Content is null ? null : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            Calls.Add((request.Method, path, query, content));

            if (request.Method == HttpMethod.Get && path == "/wp-json/wc/v3/products" && query.Contains("sku=PARENT-SKU", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("[]"));
            }

            if (request.Method == HttpMethod.Get && path == "/wp-json/wc/v3/products" && query.Contains("slug=parent-product", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("[]"));
            }

            if (request.Method == HttpMethod.Get && path == "/wp-json/wc/v3/products" && query.Contains("slug=parent-from-variations", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("[]"));
            }

            if (request.Method == HttpMethod.Get && path == "/wp-json/wc/v3/products" && query.Contains("slug=child-product", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("[]"));
            }

            if (request.Method == HttpMethod.Get && path == "/wp-json/wc/v3/products/attributes")
            {
                return Task.FromResult(JsonResponse("[]"));
            }

            if (request.Method == HttpMethod.Post && path == "/wp-json/wc/v3/products/attributes")
            {
                return Task.FromResult(JsonResponse("{\"id\":10,\"name\":\"Color\",\"slug\":\"color\"}"));
            }

            if (request.Method == HttpMethod.Get && path == "/wp-json/wc/v3/products/attributes/10/terms")
            {
                return Task.FromResult(JsonResponse("[]"));
            }

            if (request.Method == HttpMethod.Post && path == "/wp-json/wc/v3/products/attributes/10/terms")
            {
                return Task.FromResult(JsonResponse("{\"id\":100,\"name\":\"Blue\",\"slug\":\"blue\"}"));
            }

            if (request.Method == HttpMethod.Get && path == "/wp-json/wc/v3/products/categories")
            {
                return Task.FromResult(JsonResponse("[]"));
            }

            if (request.Method == HttpMethod.Post && path == "/wp-json/wc/v3/products/categories")
            {
                if (string.IsNullOrWhiteSpace(content))
                {
                    throw new InvalidOperationException("Category payload was empty.");
                }

                using var doc = JsonDocument.Parse(content);
                var slug = doc.RootElement.GetProperty("slug").GetString();
                if (string.IsNullOrWhiteSpace(slug))
                {
                    throw new InvalidOperationException("Category slug missing in payload.");
                }

                var id = ++_nextCategoryId;
                _createdCategoryIds[slug!] = id;
                return Task.FromResult(JsonResponse($"{{\"id\":{id},\"slug\":\"{slug}\",\"name\":\"{slug}\"}}"));
            }

            if (request.Method == HttpMethod.Post && path == "/wp-json/wc/v3/products")
            {
                return Task.FromResult(JsonResponse("{\"id\":200,\"sku\":\"PARENT-SKU\",\"slug\":\"parent-product\"}"));
            }

            if (request.Method == HttpMethod.Get && path == "/wp-json/wc/v3/products/200/variations")
            {
                return Task.FromResult(JsonResponse("[]"));
            }

            if (request.Method == HttpMethod.Post && path == "/wp-json/wc/v3/products/200/variations")
            {
                return Task.FromResult(JsonResponse("{\"id\":210,\"sku\":\"PARENT-SKU-BLU\",\"attributes\":[{\"id\":10,\"option\":\"Blue\"}]}"));
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        }

        public int? GetCreatedCategoryId(string slug)
        {
            return _createdCategoryIds.TryGetValue(slug, out var id) ? id : null;
        }

        private static HttpResponseMessage JsonResponse(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
