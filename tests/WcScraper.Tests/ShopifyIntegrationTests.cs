using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using WcScraper.Core;
using WcScraper.Core.Exporters;
using WcScraper.Core.Shopify;
using Xunit;

namespace WcScraper.Tests;

public sealed class ShopifyIntegrationTests
{
    private static readonly string[] GenericHeaders =
    [
        "id",
        "name",
        "slug",
        "permalink",
        "sku",
        "type",
        "description_html",
        "short_description_html",
        "summary_html",
        "regular_price",
        "sale_price",
        "price",
        "currency",
        "in_stock",
        "stock_status",
        "average_rating",
        "review_count",
        "has_options",
        "parent_id",
        "categories",
        "category_slugs",
        "tags",
        "tag_slugs",
        "images",
        "image_alts"
    ];

    [Fact]
    public async Task ShopifyScraper_CreatesGenericExportWithWooHeaders()
    {
        const string restPayload = """
        {
          "products": [
            {
              "id": 987654321,
              "title": "Test Hoodie",
              "body_html": "<p>Soft fleece</p>",
              "vendor": "Contoso",
              "product_type": "Apparel",
              "handle": "test-hoodie",
              "tags": "hoodie, winter",
              "variants": [
                {
                  "id": 5555,
                  "title": "Default Title",
                  "sku": "HD-001",
                  "price": "59.00",
                  "compare_at_price": "79.00",
                  "inventory_quantity": 12,
                  "requires_shipping": true,
                  "weight": 0.75,
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
                  "id": 888,
                  "src": "https://cdn.example.com/hoodie.jpg",
                  "alt": "Front view"
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
            Assert.True(request.Headers.TryGetValues("X-Shopify-Access-Token", out var tokens));
            Assert.Contains("admin-token", tokens);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(restPayload, Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        var scraper = new ShopifyScraper(httpClient);
        var settings = new ShopifySettings("https://example.myshopify.com", adminAccessToken: "admin-token")
        {
            MaxPages = 1,
            PageSize = 50
        };

        var storeProducts = await scraper.FetchStoreProductsAsync(settings);
        Assert.Single(storeProducts);

        var shopifyGenericRows = Mappers.ToGenericRows(storeProducts)
            .Select(ToGenericDictionary)
            .ToList();

        var wooStoreProducts = new[]
        {
            new StoreProduct
            {
                Id = 42,
                Name = "Woo Tee",
                Slug = "woo-tee",
                Permalink = "https://store.local/products/woo-tee",
                Sku = "WT-001",
                Type = "simple",
                Description = "<p>Classic tee</p>",
                ShortDescription = "Classic tee",
                Summary = "Classic tee",
                Prices = new PriceInfo
                {
                    CurrencyCode = "USD",
                    CurrencyMinorUnit = 2,
                    Price = "1999",
                    RegularPrice = "2499",
                    SalePrice = "1999"
                },
                IsInStock = true,
                StockStatus = "instock",
                AverageRating = 4.8,
                ReviewCount = 12,
                HasOptions = false,
                ParentId = null,
                Categories =
                {
                    new Category { Id = 1, Name = "Apparel", Slug = "apparel" }
                },
                Tags =
                {
                    new ProductTag { Id = 10, Name = "tee", Slug = "tee" }
                },
                Images =
                {
                    new ProductImage { Id = 5, Src = "https://store.local/img/tee.jpg", Alt = "Woo tee" }
                }
            }
        };

        var wooGenericRows = Mappers.ToGenericRows(wooStoreProducts)
            .Select(ToGenericDictionary)
            .ToList();

        var shopifyPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-shopify.csv");
        var wooPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-woo.csv");

        try
        {
            CsvExporter.Write(shopifyPath, shopifyGenericRows);
            CsvExporter.Write(wooPath, wooGenericRows);

            var shopifyHeaders = File.ReadLines(shopifyPath).First().Split(',');
            var wooHeaders = File.ReadLines(wooPath).First().Split(',');

            Assert.Equal(wooHeaders, shopifyHeaders);
            Assert.Equal(GenericHeaders, wooHeaders);
        }
        finally
        {
            if (File.Exists(shopifyPath))
            {
                File.Delete(shopifyPath);
            }

            if (File.Exists(wooPath))
            {
                File.Delete(wooPath);
            }
        }
    }

    private static Dictionary<string, object?> ToGenericDictionary(GenericRow row)
        => new()
        {
            ["id"] = row.Id,
            ["name"] = row.Name,
            ["slug"] = row.Slug,
            ["permalink"] = row.Permalink,
            ["sku"] = row.Sku,
            ["type"] = row.Type,
            ["description_html"] = row.DescriptionHtml,
            ["short_description_html"] = row.ShortDescriptionHtml,
            ["summary_html"] = row.SummaryHtml,
            ["regular_price"] = row.RegularPrice,
            ["sale_price"] = row.SalePrice,
            ["price"] = row.Price,
            ["currency"] = row.Currency,
            ["in_stock"] = row.InStock,
            ["stock_status"] = row.StockStatus,
            ["average_rating"] = row.AverageRating,
            ["review_count"] = row.ReviewCount,
            ["has_options"] = row.HasOptions,
            ["parent_id"] = row.ParentId,
            ["categories"] = row.Categories,
            ["category_slugs"] = row.CategorySlugs,
            ["tags"] = row.Tags,
            ["tag_slugs"] = row.TagSlugs,
            ["images"] = row.Images,
            ["image_alts"] = row.ImageAlts
        };

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
