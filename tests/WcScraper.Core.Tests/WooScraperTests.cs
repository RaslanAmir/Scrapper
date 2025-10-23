using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WcScraper.Core;
using Xunit;

namespace WcScraper.Core.Tests;

public sealed class WooScraperTests
{
    [Theory]
    [InlineData("https://example.com/", "https://example.com")]
    [InlineData("http://example.com/store", "http://example.com/store")]
    [InlineData("example.com", "https://example.com")]
    [InlineData("example.com/store/", "https://example.com/store")]
    [InlineData(" //example.com/path ", "https://example.com/path")]
    public void CleanBaseUrl_NormalizesAndValidatesAbsoluteUrls(string input, string expected)
    {
        var normalized = WooScraper.CleanBaseUrl(input);
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void CleanBaseUrl_NullInputThrows()
    {
        Assert.Throws<ArgumentNullException>(() => WooScraper.CleanBaseUrl(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://example.com")]
    public void CleanBaseUrl_InvalidInputThrowsArgumentException(string input)
    {
        Assert.Throws<ArgumentException>(() => WooScraper.CleanBaseUrl(input));
    }

    [Fact]
    public async Task FetchStoreProductsAsync_PopulatesSeoMetadataFromApi()
    {
        const string payload = """
        [
          {
            "id": 1,
            "name": "Product One",
            "description": "<p>One</p>",
            "short_description": "<p>Short One</p>",
            "summary": "<p>Summary One</p>",
            "meta_data": [
              { "id": 101, "key": "_yoast_wpseo_title", "value": "Primary Meta Title" },
              { "id": 102, "key": "_yoast_wpseo_metadesc", "value": "Primary Meta Description" },
              { "id": 103, "key": "_yoast_wpseo_focuskw", "value": "Focus Keyword" }
            ],
            "tags": [
              { "id": 1, "name": "Alpha", "slug": "alpha" }
            ],
            "images": []
          },
          {
            "id": 2,
            "name": "Product Two",
            "description": "<p>Two</p>",
            "short_description": "",
            "summary": "",
            "yoast_head_json": {
              "title": "Yoast Title",
              "description": "Yoast Description",
              "og_title": "OG Title"
            },
            "tags": [
              { "id": 2, "name": "Bravo Tag", "slug": "bravo" }
            ],
            "images": []
          }
        ]
        """;

        using var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        });
        using var client = new HttpClient(handler);
        var scraper = new WooScraper(client, allowLegacyTls: false);

        var products = await scraper.FetchStoreProductsAsync("https://example.com");

        Assert.Equal(2, products.Count);
        var first = products[0];
        Assert.Equal("Primary Meta Title", first.MetaTitle);
        Assert.Equal("Primary Meta Description", first.MetaDescription);
        Assert.Equal("Focus Keyword", first.MetaKeywords);

        var second = products[1];
        Assert.Equal("Yoast Title", second.MetaTitle);
        Assert.Equal("Yoast Description", second.MetaDescription);
        Assert.Equal("Bravo Tag", second.MetaKeywords);
    }

    [Fact]
    public async Task FetchStoreProductsAsync_PopulatesSeoMetadataFromAllInOneSeo()
    {
        const string payload = """
        [
          {
            "id": 10,
            "name": "Product AIO",
            "description": "<p>Description</p>",
            "short_description": "<p>Short</p>",
            "meta_data": [
              {
                "id": 201,
                "key": "_aioseo_title",
                "value": { "title": "All in One Title", "default": "Default Title" }
              },
              {
                "id": 202,
                "key": "_aioseo_description",
                "value": { "description": "All in One Description" }
              },
              {
                "id": 203,
                "key": "_aioseo_keywords",
                "value": { "keywords": ["First Keyword", "Second Keyword"] }
              },
              {
                "id": 204,
                "key": "_aioseo_focus_keyword",
                "value": "Unused Focus"
              }
            ],
            "tags": [],
            "images": []
          }
        ]
        """;

        using var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        });
        using var client = new HttpClient(handler);
        var scraper = new WooScraper(client, allowLegacyTls: false);

        var products = await scraper.FetchStoreProductsAsync("https://example.com");

        Assert.Single(products);
        var product = products[0];
        Assert.Equal("All in One Title", product.MetaTitle);
        Assert.Equal("All in One Description", product.MetaDescription);
        Assert.Equal("First Keyword, Second Keyword", product.MetaKeywords);
    }

    [Fact]
    public async Task FetchStoreProductsAsync_PopulatesSeoMetadataFromHtmlFallback()
    {
        const string storePayload = """
        [
          {
            "id": 77,
            "name": "Fallback Product",
            "permalink": "https://example.com/product/fallback-product/",
            "description": "<p>Description</p>",
            "short_description": "<p>Short</p>",
            "meta_data": [],
            "tags": [],
            "images": []
          }
        ]
        """;

        const string htmlResponse = """
        <!doctype html>
        <html>
          <head>
            <meta name="aioseo:title" content="HTML Meta Title" />
            <meta name="aioseo:description" content="HTML Meta Description" />
            <meta name="aioseo:keywords" content="alpha, beta, gamma" />
          </head>
          <body><h1>Fallback Product</h1></body>
        </html>
        """;

        using var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri is null)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }

            if (request.RequestUri.AbsolutePath.StartsWith("/wp-json/wc/store/v1/products", StringComparison.Ordinal))
            {
                var content = request.RequestUri.Query.Contains("page=1", StringComparison.Ordinal)
                    ? storePayload
                    : "[]";

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(content, Encoding.UTF8, "application/json")
                };
            }

            if (string.Equals(request.RequestUri.AbsoluteUri, "https://example.com/product/fallback-product/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(htmlResponse, Encoding.UTF8, "text/html")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new HttpClient(handler);
        var scraper = new WooScraper(client, allowLegacyTls: false);

        var products = await scraper.FetchStoreProductsAsync("https://example.com");

        Assert.Single(products);
        var product = products[0];
        Assert.Equal("HTML Meta Title", product.MetaTitle);
        Assert.Equal("HTML Meta Description", product.MetaDescription);
        Assert.Equal("alpha, beta, gamma", product.MetaKeywords);
    }

    [Fact]
    public async Task FetchStoreProductsAsync_PrefersHtmlDescriptionOverShortDescriptionFallback()
    {
        const string storePayload = """
        [
          {
            "id": 88,
            "name": "HTML Priority Product",
            "permalink": "https://example.com/product/html-priority-product/",
            "description": "<p>Description</p>",
            "short_description": "Woo Short Description",
            "yoast_head_json": {
              "title": "Yoast Provided Title"
            },
            "tags": [
              { "id": 5, "name": "Sample Tag", "slug": "sample-tag" }
            ],
            "images": []
          }
        ]
        """;

        const string htmlResponse = """
        <!doctype html>
        <html>
          <head>
            <meta name="description" content="HTML Provided Description" />
          </head>
          <body><h1>HTML Priority Product</h1></body>
        </html>
        """;

        using var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri is null)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }

            if (request.RequestUri.AbsolutePath.StartsWith("/wp-json/wc/store/v1/products", StringComparison.Ordinal))
            {
                var content = request.RequestUri.Query.Contains("page=1", StringComparison.Ordinal)
                    ? storePayload
                    : "[]";

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(content, Encoding.UTF8, "application/json")
                };
            }

            if (string.Equals(request.RequestUri.AbsoluteUri, "https://example.com/product/html-priority-product/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(htmlResponse, Encoding.UTF8, "text/html")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new HttpClient(handler);
        var scraper = new WooScraper(client, allowLegacyTls: false);

        var products = await scraper.FetchStoreProductsAsync("https://example.com");

        Assert.Single(products);
        var product = products[0];
        Assert.Equal("Yoast Provided Title", product.MetaTitle);
        Assert.Equal("HTML Provided Description", product.MetaDescription);
        Assert.Equal("Sample Tag", product.MetaKeywords);
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
