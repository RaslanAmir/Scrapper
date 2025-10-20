using System;
using System.Collections.Generic;
using System.IO;
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

    [Fact]
    public async Task ProvisionAsync_LocalImageUploads_UsesWordPressCredentials()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var service = new WooProvisioningService(httpClient);
        var settings = new WooProvisioningSettings(
            "https://target.example",
            "ck",
            "cs",
            wordpressUsername: "wpuser",
            wordpressApplicationPassword: "app-pass");

        var tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg");
        await File.WriteAllTextAsync(tempFile, "test image content");

        try
        {
            var product = new StoreProduct
            {
                Id = 777,
                Name = "Local Image Product",
                Slug = "local-image-product",
                Sku = "LOCAL-IMAGE-SKU"
            };
            product.LocalImageFilePaths.Add(tempFile);

            await service.ProvisionAsync(settings, new[] { product });

            var mediaCall = handler.Calls.Single(call => call.Method == HttpMethod.Post && call.Path == "/wp-json/wp/v2/media");
            var expectedAuth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("wpuser:app-pass"));
            Assert.Equal(expectedAuth, mediaCall.Authorization);

            var productCall = handler.Calls.Single(call => call.Method == HttpMethod.Post && call.Path == "/wp-json/wc/v3/products");
            Assert.NotNull(productCall.Content);
            using var doc = JsonDocument.Parse(productCall.Content!);
            var images = doc.RootElement.GetProperty("images").EnumerateArray().ToList();
            Assert.Single(images);
            Assert.Equal(301, images[0].GetProperty("id").GetInt32());
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public async Task ProvisionAsync_RemoteImageWithCachedMapping_UsesAttachmentId()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var service = new WooProvisioningService(httpClient);
        var settings = new WooProvisioningSettings(
            "https://target.example",
            "ck",
            "cs",
            wordpressUsername: "wpuser",
            wordpressApplicationPassword: "app-pass");

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var mediaFile = Path.Combine(tempDir, "gallery.jpg");
        await File.WriteAllTextAsync(mediaFile, "exported image");

        try
        {
            var exportedUrl = "https://source.example/wp-content/uploads/gallery.jpg";

            var siteContent = new WordPressSiteContent
            {
                MediaRootDirectory = tempDir
            };
            siteContent.MediaLibrary.Add(new WordPressMediaItem
            {
                Id = 555,
                SourceUrl = exportedUrl,
                LocalFilePath = mediaFile
            });

            var product = new StoreProduct
            {
                Id = 888,
                Name = "Remote Image Product",
                Slug = "local-image-product",
                Sku = "LOCAL-IMAGE-SKU",
                Images =
                {
                    new ProductImage { Src = exportedUrl, Alt = "Gallery" }
                }
            };

            await service.ProvisionAsync(
                settings,
                new[] { product },
                siteContent: siteContent);

            var productCall = handler.Calls.Single(call => call.Method == HttpMethod.Post && call.Path == "/wp-json/wc/v3/products");
            Assert.NotNull(productCall.Content);
            using var doc = JsonDocument.Parse(productCall.Content!);
            var images = doc.RootElement.GetProperty("images").EnumerateArray().ToList();
            Assert.Single(images);
            var image = images[0];
            Assert.True(image.TryGetProperty("id", out var idProperty));
            Assert.Equal(301, idProperty.GetInt32());
            Assert.False(image.TryGetProperty("src", out _));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProvisionAsync_PageWithoutMappedAuthor_DoesNotSendAuthorField()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var service = new WooProvisioningService(httpClient);
        var settings = new WooProvisioningSettings(
            "https://target.example",
            "ck",
            "cs",
            wordpressUsername: "wpuser",
            wordpressApplicationPassword: "app-pass");

        var page = new WordPressPage
        {
            Id = 10,
            Title = new WordPressRenderedText { Rendered = "Sample Page" },
            Content = new WordPressRenderedText { Rendered = "<p>Hello</p>" },
            Author = 123
        };

        var siteContent = new WordPressSiteContent
        {
            Pages = { page }
        };

        await service.ProvisionAsync(
            settings,
            Array.Empty<StoreProduct>(),
            siteContent: siteContent);

        var pageCall = handler.Calls.Single(call => call.Method == HttpMethod.Post && call.Path == "/wp-json/wp/v2/pages");
        Assert.NotNull(pageCall.Content);
        using var doc = JsonDocument.Parse(pageCall.Content!);
        Assert.False(doc.RootElement.TryGetProperty("author", out _));
    }

    [Fact]
    public async Task ProvisionAsync_PostWithoutMappedAuthor_DoesNotSendAuthorField()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var service = new WooProvisioningService(httpClient);
        var settings = new WooProvisioningSettings(
            "https://target.example",
            "ck",
            "cs",
            wordpressUsername: "wpuser",
            wordpressApplicationPassword: "app-pass");

        var post = new WordPressPost
        {
            Id = 20,
            Title = new WordPressRenderedText { Rendered = "Sample Post" },
            Content = new WordPressRenderedText { Rendered = "<p>World</p>" },
            Author = 456
        };

        var siteContent = new WordPressSiteContent
        {
            Posts = { post }
        };

        await service.ProvisionAsync(
            settings,
            Array.Empty<StoreProduct>(),
            siteContent: siteContent);

        var postCall = handler.Calls.Single(call => call.Method == HttpMethod.Post && call.Path == "/wp-json/wp/v2/posts");
        Assert.NotNull(postCall.Content);
        using var doc = JsonDocument.Parse(postCall.Content!);
        Assert.False(doc.RootElement.TryGetProperty("author", out _));
    }

    [Fact]
    public async Task ProvisionAsync_PageWithMappedAuthor_UsesMappedAuthor()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var service = new WooProvisioningService(httpClient);
        var settings = new WooProvisioningSettings(
            "https://target.example",
            "ck",
            "cs",
            wordpressUsername: "wpuser",
            wordpressApplicationPassword: "app-pass");

        var page = new WordPressPage
        {
            Id = 11,
            Title = new WordPressRenderedText { Rendered = "Mapped Page" },
            Content = new WordPressRenderedText { Rendered = "<p>Mapped</p>" },
            Author = 321
        };

        var siteContent = new WordPressSiteContent
        {
            Pages = { page }
        };

        var authorMap = new Dictionary<int, int> { [321] = 654 };

        await service.ProvisionAsync(
            settings,
            Array.Empty<StoreProduct>(),
            siteContent: siteContent,
            authorIdMap: authorMap);

        var pageCall = handler.Calls.Single(call => call.Method == HttpMethod.Post && call.Path == "/wp-json/wp/v2/pages");
        Assert.NotNull(pageCall.Content);
        using var doc = JsonDocument.Parse(pageCall.Content!);
        Assert.Equal(654, doc.RootElement.GetProperty("author").GetInt32());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Query, string? Content, string? Authorization)> Calls { get; } = new();

        private readonly Dictionary<string, int> _createdCategoryIds = new(StringComparer.OrdinalIgnoreCase);
        private int _nextCategoryId = 500;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri!.Query;
            var content = request.Content is null ? null : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            var authorization = request.Headers.Authorization?.ToString();
            Calls.Add((request.Method, path, query, content, authorization));

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

            if (request.Method == HttpMethod.Get && path == "/wp-json/wc/v3/products" && query.Contains("sku=LOCAL-IMAGE-SKU", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("[]"));
            }

            if (request.Method == HttpMethod.Get && path == "/wp-json/wc/v3/products" && query.Contains("slug=local-image-product", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("[]"));
            }

            if (request.Method == HttpMethod.Get && path == "/wp-json/wp/v2/media")
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

            if (request.Method == HttpMethod.Post && path == "/wp-json/wp/v2/media")
            {
                return Task.FromResult(JsonResponse("{\"id\":301,\"source_url\":\"https://target.example/wp-content/uploads/gallery.jpg\"}"));
            }

            if (request.Method == HttpMethod.Post && path == "/wp-json/wc/v3/products")
            {
                return Task.FromResult(JsonResponse("{\"id\":200,\"sku\":\"PARENT-SKU\",\"slug\":\"parent-product\"}"));
            }

            if (request.Method == HttpMethod.Get && path == "/wp-json/wp/v2/pages")
            {
                return Task.FromResult(JsonResponse("[]"));
            }

            if (request.Method == HttpMethod.Post && path == "/wp-json/wp/v2/pages")
            {
                return Task.FromResult(JsonResponse("{\"id\":601}"));
            }

            if (request.Method == HttpMethod.Get && path == "/wp-json/wp/v2/posts")
            {
                return Task.FromResult(JsonResponse("[]"));
            }

            if (request.Method == HttpMethod.Post && path == "/wp-json/wp/v2/posts")
            {
                return Task.FromResult(JsonResponse("{\"id\":701}"));
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
