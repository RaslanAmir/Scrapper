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
