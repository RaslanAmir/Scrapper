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
            variations: new[] { variation },
            progress: new Progress<string>(logs.Add));

        Assert.Contains(logs, message => message.Contains("Provisioning 1 variations", StringComparison.Ordinal));
        Assert.Contains(logs, message => message.Contains("Creating variation 'PARENT-SKU-BLU'", StringComparison.Ordinal));

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

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Query, string? Content)> Calls { get; } = new();

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

        private static HttpResponseMessage JsonResponse(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
