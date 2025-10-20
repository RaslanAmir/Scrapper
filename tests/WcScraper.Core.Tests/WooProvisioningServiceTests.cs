using System;
using System.Collections.Generic;
using System.Globalization;
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
            var root = doc.RootElement;
            Assert.Equal("variable", root.GetProperty("type").GetString());
            var attributes = root.GetProperty("attributes").EnumerateArray().ToList();
            Assert.Single(attributes);
            var attribute = attributes[0];
            Assert.True(attribute.GetProperty("variation").GetBoolean());
            Assert.True(attribute.GetProperty("visible").GetBoolean());
            Assert.Equal(0, attribute.GetProperty("position").GetInt32());
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
    public async Task ProvisionAsync_ExistingAttributeFoundOnLaterPage_UsesExistingAttributeAndTerm()
    {
        var handler = new RecordingHandler();
        handler.AttributePages[1] = new List<RecordingHandler.AttributeRecord>
        {
            new() { Id = 21, Name = "Material", Slug = "material" }
        };
        handler.AttributePages[2] = new List<RecordingHandler.AttributeRecord>
        {
            new() { Id = 42, Name = "Color", Slug = "color" }
        };
        handler.AttributeTermPages[(42, 1)] = new List<RecordingHandler.TermRecord>
        {
            new() { Id = 301, Name = "Green", Slug = "green" }
        };
        handler.AttributeTermPages[(42, 2)] = new List<RecordingHandler.TermRecord>
        {
            new() { Id = 302, Name = "Blue", Slug = "blue" }
        };

        using var httpClient = new HttpClient(handler);
        var service = new WooProvisioningService(httpClient);
        var settings = new WooProvisioningSettings("https://target.example", "ck", "cs");

        var parent = new StoreProduct
        {
            Id = 103,
            Name = "Existing Attribute Parent",
            Slug = "existing-attribute-parent",
            Sku = "EXISTING-ATTRIBUTE",
            Type = "variable",
            Attributes =
            {
                new VariationAttribute { AttributeKey = "pa_color", Option = "Blue" }
            }
        };

        var variation = new StoreProduct
        {
            Id = 402,
            ParentId = 103,
            Name = "Existing Attribute Parent - Blue",
            Slug = "existing-attribute-parent-blue",
            Sku = "EXISTING-ATTRIBUTE-BLU",
            Prices = new PriceInfo { RegularPrice = "24.99" },
            StockStatus = "instock",
            Attributes =
            {
                new VariationAttribute { AttributeKey = "pa_color", Option = "Blue" }
            }
        };

        await service.ProvisionAsync(
            settings,
            new[] { parent },
            variableProducts: new[] { new ProvisioningVariableProduct(parent, new[] { variation }) });

        Assert.Contains(
            handler.Calls,
            call => call.Method == HttpMethod.Get
                && call.Path == "/wp-json/wc/v3/products/attributes"
                && call.Query.Contains("page=2", StringComparison.Ordinal));
        Assert.Contains(
            handler.Calls,
            call => call.Method == HttpMethod.Get
                && call.Path == "/wp-json/wc/v3/products/attributes/42/terms"
                && call.Query.Contains("page=2", StringComparison.Ordinal));
        Assert.DoesNotContain(
            handler.Calls,
            call => call.Method == HttpMethod.Post && call.Path == "/wp-json/wc/v3/products/attributes");
        Assert.DoesNotContain(
            handler.Calls,
            call => call.Method == HttpMethod.Post
                && call.Path == "/wp-json/wc/v3/products/attributes/42/terms");

        var productCall = handler.Calls.Single(call => call.Method == HttpMethod.Post && call.Path == "/wp-json/wc/v3/products");
        using (var productDoc = JsonDocument.Parse(productCall.Content!))
        {
            var attributes = productDoc.RootElement.GetProperty("attributes").EnumerateArray().ToList();
            Assert.Single(attributes);
            var attribute = attributes[0];
            Assert.Equal(42, attribute.GetProperty("id").GetInt32());
        }

        var variationCall = handler.Calls.Single(call => call.Method == HttpMethod.Post && call.Path == "/wp-json/wc/v3/products/200/variations");
        using (var variationDoc = JsonDocument.Parse(variationCall.Content!))
        {
            var attributes = variationDoc.RootElement.GetProperty("attributes").EnumerateArray().ToList();
            Assert.Single(attributes);
            var attribute = attributes[0];
            Assert.Equal(42, attribute.GetProperty("id").GetInt32());
            Assert.Equal("Blue", attribute.GetProperty("option").GetString());
        }
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
    public async Task ProvisionAsync_CouponWithCategoryRestrictions_MapsCategoryIds()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var service = new WooProvisioningService(httpClient);
        var settings = new WooProvisioningSettings("https://target.example", "ck", "cs");

        var product = new StoreProduct
        {
            Id = 123,
            Name = "Coupon Product",
            Slug = "coupon-product",
            Sku = "COUPON-PROD",
            Categories =
            {
                new Category { Id = 300, Name = "Apparel", Slug = "apparel" },
                new Category { Id = 400, Name = "Clearance", Slug = "clearance" }
            }
        };

        var coupon = new WooCoupon
        {
            Id = 50,
            Code = "APPAREL10",
            Amount = "10",
            DiscountType = "percent",
            ProductCategories = { 300 },
            ExcludedProductCategories = { 400 }
        };

        await service.ProvisionAsync(
            settings,
            new[] { product },
            coupons: new[] { coupon });

        var couponCall = handler.Calls.Single(call => call.Method == HttpMethod.Post && call.Path == "/wp-json/wc/v3/coupons");
        Assert.NotNull(couponCall.Content);
        using var doc = JsonDocument.Parse(couponCall.Content!);
        var root = doc.RootElement;

        var included = root
            .GetProperty("product_categories")
            .EnumerateArray()
            .Select(element => element.GetInt32())
            .ToList();
        var excluded = root
            .GetProperty("excluded_product_categories")
            .EnumerateArray()
            .Select(element => element.GetInt32())
            .ToList();

        var includedId = handler.GetCreatedCategoryId("apparel");
        var excludedId = handler.GetCreatedCategoryId("clearance");
        Assert.NotNull(includedId);
        Assert.NotNull(excludedId);
        Assert.Single(included);
        Assert.Equal(includedId.Value, included[0]);
        Assert.Single(excluded);
        Assert.Equal(excludedId.Value, excluded[0]);
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
    public async Task ProvisionAsync_LocalAndRemoteImages_PreservesGalleryEntries()
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
                Id = 889,
                Name = "Mixed Image Product",
                Slug = "mixed-image-product",
                Sku = "MIXED-IMAGE-SKU",
                Images =
                {
                    new ProductImage { Src = "https://example.com/gallery1.jpg", Alt = "Gallery 1" },
                    new ProductImage { Src = "https://example.com/gallery2.jpg", Alt = "Gallery 2" }
                }
            };
            product.LocalImageFilePaths.Add(tempFile);

            await service.ProvisionAsync(settings, new[] { product });

            var productCall = handler.Calls.Single(call => call.Method == HttpMethod.Post && call.Path == "/wp-json/wc/v3/products");
            Assert.NotNull(productCall.Content);
            using var doc = JsonDocument.Parse(productCall.Content!);
            var images = doc.RootElement.GetProperty("images").EnumerateArray().ToList();
            Assert.Equal(3, images.Count);

            Assert.Equal(301, images[0].GetProperty("id").GetInt32());

            Assert.Equal("https://example.com/gallery1.jpg", images[1].GetProperty("src").GetString());
            Assert.Equal("Gallery 1", images[1].GetProperty("alt").GetString());

            Assert.Equal("https://example.com/gallery2.jpg", images[2].GetProperty("src").GetString());
            Assert.Equal("Gallery 2", images[2].GetProperty("alt").GetString());
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
    public async Task ProvisionAsync_MenuItem_UsesMappedObjectIds()
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

        var category = new Category { Id = 654, Name = "Menu Category", Slug = "menu-category" };

        var product = new StoreProduct
        {
            Id = 321,
            Name = "Menu Product",
            Slug = "menu-product",
            Sku = "MENU-PROD",
            Categories = { category }
        };

        var menu = new WordPressMenu
        {
            Id = 10,
            Name = "Primary Menu",
            Slug = "primary",
            Items =
            {
                new WordPressMenuItem
                {
                    Id = 100,
                    Order = 1,
                    Title = new WordPressRenderedText { Rendered = "Product Link" },
                    Object = "product",
                    ObjectId = product.Id
                },
                new WordPressMenuItem
                {
                    Id = 101,
                    Order = 2,
                    Title = new WordPressRenderedText { Rendered = "Category Link" },
                    Object = "product_cat",
                    ObjectId = category.Id
                }
            }
        };

        var siteContent = new WordPressSiteContent
        {
            Menus = new WordPressMenuCollection
            {
                Menus = { menu }
            }
        };

        await service.ProvisionAsync(
            settings,
            new[] { product },
            siteContent: siteContent);

        var menuItemCalls = handler.Calls
            .Where(call => call.Method == HttpMethod.Post && call.Path == "/wp-json/wp/v2/menu-items")
            .ToList();

        Assert.Equal(2, menuItemCalls.Count);
        Assert.All(menuItemCalls, call => Assert.False(string.IsNullOrWhiteSpace(call.Content)));

        using (var productItemDoc = JsonDocument.Parse(menuItemCalls[0].Content!))
        {
            Assert.Equal(200, productItemDoc.RootElement.GetProperty("object_id").GetInt32());
        }

        var createdCategoryId = handler.GetCreatedCategoryId("menu-category");
        Assert.True(createdCategoryId.HasValue, "Expected category to be created during provisioning.");

        using (var categoryItemDoc = JsonDocument.Parse(menuItemCalls[1].Content!))
        {
            Assert.Equal(createdCategoryId.Value, categoryItemDoc.RootElement.GetProperty("object_id").GetInt32());
        }
    }

    [Fact]
    public async Task ProvisionAsync_MenuCategoryWithoutProductAssociation_MapsCategoryId()
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

        var placeholderProduct = new StoreProduct
        {
            Id = 999,
            Name = "Placeholder",
            Slug = "placeholder",
            Sku = "PLACEHOLDER"
        };

        var menuCategoryItem = new WordPressMenuItem
        {
            Id = 201,
            Order = 1,
            Title = new WordPressRenderedText { Rendered = "Menu Only Category" },
            Object = "product_cat",
            ObjectId = 777,
            Url = "https://source.example/product-category/menu-only/"
        };

        var menuCollection = new WordPressMenuCollection
        {
            Menus =
            {
                new WordPressMenu
                {
                    Id = 22,
                    Name = "Primary",
                    Slug = "primary",
                    Items = { menuCategoryItem }
                }
            }
        };

        var siteContent = new WordPressSiteContent
        {
            Menus = menuCollection
        };

        await service.ProvisionAsync(
            settings,
            new[] { placeholderProduct },
            siteContent: siteContent);

        var createdCategoryId = handler.GetCreatedCategoryId("menu-only");
        Assert.True(createdCategoryId.HasValue, "Expected category to be created for the menu item.");

        var menuItemCall = handler.Calls.Single(call => call.Method == HttpMethod.Post && call.Path == "/wp-json/wp/v2/menu-items");
        Assert.False(string.IsNullOrWhiteSpace(menuItemCall.Content));

        using var doc = JsonDocument.Parse(menuItemCall.Content!);
        Assert.Equal(createdCategoryId.Value, doc.RootElement.GetProperty("object_id").GetInt32());
    }

    [Fact]
    public async Task ProvisionAsync_MenuCategoryWithoutProducts_MapsCategoryId()
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

        var menuCategoryItem = new WordPressMenuItem
        {
            Id = 305,
            Order = 1,
            Title = new WordPressRenderedText { Rendered = "Menu Only Category" },
            Object = "product_cat",
            ObjectId = 888,
            Url = "https://source.example/product-category/menu-only/"
        };

        var menuCollection = new WordPressMenuCollection
        {
            Menus =
            {
                new WordPressMenu
                {
                    Id = 44,
                    Name = "Navigation",
                    Slug = "navigation",
                    Items = { menuCategoryItem }
                }
            }
        };

        var siteContent = new WordPressSiteContent
        {
            Menus = menuCollection
        };

        await service.ProvisionAsync(
            settings,
            Array.Empty<StoreProduct>(),
            siteContent: siteContent);

        var createdCategoryId = handler.GetCreatedCategoryId("menu-only");
        Assert.True(
            createdCategoryId.HasValue,
            "Expected category to be created for the menu item when no products are provided.");

        var menuItemCalls = handler.Calls
            .Where(call => call.Method == HttpMethod.Post && call.Path == "/wp-json/wp/v2/menu-items")
            .ToList();

        Assert.Single(menuItemCalls);
        Assert.Contains(
            handler.Calls,
            call => call.Method == HttpMethod.Post && call.Path == "/wp-json/wc/v3/products/categories");

        using var doc = JsonDocument.Parse(menuItemCalls[0].Content!);
        Assert.Equal(createdCategoryId.Value, doc.RootElement.GetProperty("object_id").GetInt32());
    }

    [Fact]
    public async Task ProvisionAsync_MenuTagWithoutProducts_MapsTagId()
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

        var menuTagItem = new WordPressMenuItem
        {
            Id = 406,
            Order = 1,
            Title = new WordPressRenderedText { Rendered = "Menu Only Tag" },
            Object = "product_tag",
            ObjectId = 889,
            Url = "https://source.example/product-tag/menu-only-tag/"
        };

        var menuCollection = new WordPressMenuCollection
        {
            Menus =
            {
                new WordPressMenu
                {
                    Id = 55,
                    Name = "Navigation",
                    Slug = "navigation",
                    Items = { menuTagItem }
                }
            }
        };

        var siteContent = new WordPressSiteContent
        {
            Menus = menuCollection
        };

        await service.ProvisionAsync(
            settings,
            Array.Empty<StoreProduct>(),
            siteContent: siteContent);

        var createdTagId = handler.GetCreatedTagId("menu-only-tag");
        Assert.True(
            createdTagId.HasValue,
            "Expected tag to be created for the menu item when no products are provided.");

        Assert.Contains(
            handler.Calls,
            call => call.Method == HttpMethod.Post && call.Path == "/wp-json/wc/v3/products/tags");

        var menuItemCall = handler.Calls.Single(call => call.Method == HttpMethod.Post && call.Path == "/wp-json/wp/v2/menu-items");
        Assert.False(string.IsNullOrWhiteSpace(menuItemCall.Content));

        using var doc = JsonDocument.Parse(menuItemCall.Content!);
        Assert.Equal(createdTagId.Value, doc.RootElement.GetProperty("object_id").GetInt32());
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

    [Fact]
    public async Task ProvisionAsync_OnHoldOrderWithoutPayment_LeavesSetPaidFalse()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var service = new WooProvisioningService(httpClient);
        var settings = new WooProvisioningSettings("https://target.example", "ck", "cs");

        var order = new WooOrder
        {
            Id = 501,
            Status = "on-hold",
            LineItems =
            {
                new WooOrderLineItem
                {
                    Name = "Sample Item",
                    Quantity = 1,
                    Total = "9.99"
                }
            }
        };

        await service.ProvisionAsync(
            settings,
            Array.Empty<StoreProduct>(),
            orders: new[] { order });

        var orderCall = handler.Calls.Single(call => call.Method == HttpMethod.Post && call.Path == "/wp-json/wc/v3/orders");
        Assert.NotNull(orderCall.Content);
        using var doc = JsonDocument.Parse(orderCall.Content!);
        Assert.False(doc.RootElement.GetProperty("set_paid").GetBoolean());
    }

    [Fact]
    public async Task ProvisionAsync_CompletedOrder_DefaultsSetPaidTrue()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var service = new WooProvisioningService(httpClient);
        var settings = new WooProvisioningSettings("https://target.example", "ck", "cs");

        var order = new WooOrder
        {
            Id = 601,
            Status = "completed",
            LineItems =
            {
                new WooOrderLineItem
                {
                    Name = "Finished Item",
                    Quantity = 2,
                    Total = "19.98"
                }
            }
        };

        await service.ProvisionAsync(
            settings,
            Array.Empty<StoreProduct>(),
            orders: new[] { order });

        var orderCall = handler.Calls.Single(call => call.Method == HttpMethod.Post && call.Path == "/wp-json/wc/v3/orders");
        Assert.NotNull(orderCall.Content);
        using var doc = JsonDocument.Parse(orderCall.Content!);
        Assert.True(doc.RootElement.GetProperty("set_paid").GetBoolean());
    }

    [Fact]
    public async Task ProvisionAsync_OrderTaxLines_RemapsOrOmitsRateIdentifiers()
    {
        var handler = new RecordingHandler();
        handler.TaxRates.Add(new RecordingHandler.TaxRateRecord
        {
            Id = 987,
            RateCode = "US-CA-STATE TAX"
        });

        using var httpClient = new HttpClient(handler);
        var service = new WooProvisioningService(httpClient);
        var settings = new WooProvisioningSettings("https://target.example", "ck", "cs");

        var order = new WooOrder
        {
            Id = 701,
            LineItems =
            {
                new WooOrderLineItem
                {
                    Name = "Taxed Item",
                    Quantity = 1,
                    Total = "10.00"
                }
            },
            TaxLines =
            {
                new WooOrderTaxLine
                {
                    RateCode = "US-CA-STATE TAX",
                    RateId = 123,
                    Label = "CA State Tax",
                    TaxTotal = "0.80"
                },
                new WooOrderTaxLine
                {
                    RateCode = "MISSING-CODE",
                    RateId = 456,
                    Label = "Unknown Tax",
                    TaxTotal = "0.20"
                }
            }
        };

        await service.ProvisionAsync(
            settings,
            Array.Empty<StoreProduct>(),
            orders: new[] { order });

        var taxRequests = handler.Calls
            .Where(call => call.Method == HttpMethod.Get && call.Path == "/wp-json/wc/v3/taxes")
            .ToList();
        Assert.NotEmpty(taxRequests);

        var orderCall = handler.Calls.Single(call => call.Method == HttpMethod.Post && call.Path == "/wp-json/wc/v3/orders");
        Assert.NotNull(orderCall.Content);
        using var doc = JsonDocument.Parse(orderCall.Content!);
        var taxLines = doc.RootElement.GetProperty("tax_lines").EnumerateArray().ToList();
        Assert.Equal(2, taxLines.Count);

        var mappedLine = Assert.Single(taxLines.Where(element => element.GetProperty("rate_code").GetString() == "US-CA-STATE TAX"));
        Assert.Equal(987, mappedLine.GetProperty("rate_id").GetInt32());

        var missingLine = Assert.Single(taxLines.Where(element => element.GetProperty("rate_code").GetString() == "MISSING-CODE"));
        Assert.False(missingLine.TryGetProperty("rate_id", out _));
    }

    [Fact]
    public async Task ProvisionAsync_ShippingZoneWithEmptyLocations_CallsLocationsEndpointWithEmptyArray()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var service = new WooProvisioningService(httpClient);
        var settings = new WooProvisioningSettings("https://target.example", "ck", "cs");

        var configuration = new StoreConfiguration
        {
            ShippingZones =
            {
                new ShippingZoneSetting
                {
                    Id = 123,
                    Name = "Empty Zone",
                    Locations = new List<ShippingZoneLocation>()
                }
            }
        };

        await service.ProvisionAsync(
            settings,
            Array.Empty<StoreProduct>(),
            configuration: configuration);

        var locationCall = handler.Calls.Single(call => call.Method == HttpMethod.Put
            && call.Path == "/wp-json/wc/v3/shipping/zones/123/locations");
        Assert.Equal("[]", locationCall.Content);
    }

    [Fact]
    public async Task ProvisionAsync_DefaultShippingZone_UpdatesLocationsAndMethods()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var service = new WooProvisioningService(httpClient);
        var settings = new WooProvisioningSettings("https://target.example", "ck", "cs");

        var configuration = new StoreConfiguration
        {
            ShippingZones =
            {
                new ShippingZoneSetting
                {
                    Id = 0,
                    Name = "Rest of the World",
                    Order = 2,
                    Locations =
                    {
                        new ShippingZoneLocation { Code = "US:CA", Type = "state" }
                    },
                    Methods =
                    {
                        new ShippingZoneMethodSetting
                        {
                            InstanceId = 45,
                            MethodId = "flat_rate",
                            Title = "Flat Rate",
                            Enabled = true,
                            Order = 3
                        }
                    }
                }
            }
        };

        await service.ProvisionAsync(
            settings,
            Array.Empty<StoreProduct>(),
            configuration: configuration);

        Assert.DoesNotContain(
            handler.Calls,
            call => call.Method == HttpMethod.Post && call.Path == "/wp-json/wc/v3/shipping/zones");

        Assert.Contains(
            handler.Calls,
            call => call.Method == HttpMethod.Put && call.Path == "/wp-json/wc/v3/shipping/zones/0");

        var locationCall = handler.Calls.Single(call => call.Method == HttpMethod.Put
            && call.Path == "/wp-json/wc/v3/shipping/zones/0/locations");
        Assert.NotNull(locationCall.Content);
        using (var doc = JsonDocument.Parse(locationCall.Content!))
        {
            var locations = doc.RootElement.EnumerateArray().ToList();
            var location = Assert.Single(locations);
            Assert.Equal("state", location.GetProperty("type").GetString());
            Assert.Equal("US:CA", location.GetProperty("code").GetString());
        }

        var methodCall = handler.Calls.Single(call => call.Method == HttpMethod.Put
            && call.Path == "/wp-json/wc/v3/shipping/zones/0/methods/45");
        Assert.NotNull(methodCall.Content);
        using (var doc = JsonDocument.Parse(methodCall.Content!))
        {
            var root = doc.RootElement;
            Assert.Equal("Flat Rate", root.GetProperty("title").GetString());
            Assert.True(root.GetProperty("enabled").GetBoolean());
            Assert.Equal(3, root.GetProperty("order").GetInt32());
        }
    }

    [Fact]
    public async Task ProvisionAsync_MissingShippingZone_CreatesZoneAndUsesNewId()
    {
        var handler = new RecordingHandler
        {
            SimulateMissingShippingZone = true,
            MissingShippingZoneId = 321
        };
        using var httpClient = new HttpClient(handler);
        var service = new WooProvisioningService(httpClient);
        var settings = new WooProvisioningSettings("https://target.example", "ck", "cs");

        var configuration = new StoreConfiguration
        {
            ShippingZones =
            {
                new ShippingZoneSetting
                {
                    Id = handler.MissingShippingZoneId,
                    Name = "Sample Zone",
                    Order = 12,
                    Locations =
                    {
                        new ShippingZoneLocation { Code = "US", Type = "country" }
                    },
                    Methods =
                    {
                        new ShippingZoneMethodSetting
                        {
                            InstanceId = 45,
                            MethodId = "flat_rate",
                            Title = "Flat Rate",
                            Enabled = true,
                            Order = 3
                        }
                    }
                }
            }
        };

        await service.ProvisionAsync(
            settings,
            Array.Empty<StoreProduct>(),
            configuration: configuration);

        Assert.Contains(
            handler.Calls,
            call => call.Method == HttpMethod.Put
                && call.Path == $"/wp-json/wc/v3/shipping/zones/{handler.MissingShippingZoneId}");

        var createdZoneId = handler.LastCreatedShippingZoneId;
        Assert.NotNull(createdZoneId);

        var zonePost = handler.Calls.Single(call => call.Method == HttpMethod.Post && call.Path == "/wp-json/wc/v3/shipping/zones");
        Assert.NotNull(zonePost.Content);
        using (var doc = JsonDocument.Parse(zonePost.Content!))
        {
            var root = doc.RootElement;
            Assert.Equal("Sample Zone", root.GetProperty("name").GetString());
            Assert.Equal(12, root.GetProperty("order").GetInt32());
        }

        Assert.DoesNotContain(
            handler.Calls,
            call => call.Method == HttpMethod.Put
                && call.Path == $"/wp-json/wc/v3/shipping/zones/{handler.MissingShippingZoneId}/locations");

        Assert.Contains(
            handler.Calls,
            call => call.Method == HttpMethod.Put
                && call.Path == $"/wp-json/wc/v3/shipping/zones/{createdZoneId}/locations");

        Assert.Contains(
            handler.Calls,
            call => call.Method == HttpMethod.Put
                && call.Path == $"/wp-json/wc/v3/shipping/zones/{createdZoneId}/methods/45");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Query, string? Content, string? Authorization)> Calls { get; } = new();
        public List<TaxRateRecord> TaxRates { get; } = new();
        public Dictionary<int, List<AttributeRecord>> AttributePages { get; } = new();
        public Dictionary<(int AttributeId, int Page), List<TermRecord>> AttributeTermPages { get; } = new();

        private readonly Dictionary<string, int> _createdCategoryIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _createdTagIds = new(StringComparer.OrdinalIgnoreCase);
        private int _nextCategoryId = 500;
        private int _nextTagId = 1500;
        private int _nextMenuId = 4000;
        private int _nextMenuItemId = 5000;
        private int _nextShippingZoneId = 6000;
        private int _nextShippingMethodInstanceId = 7000;
        private int? _lastCreatedShippingZoneId;
        private bool _shippingZoneCreated;

        public bool SimulateMissingShippingZone { get; set; }

        public int MissingShippingZoneId { get; set; }

        public int? LastCreatedShippingZoneId => _lastCreatedShippingZoneId;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri!.Query;
            var content = request.Content is null ? null : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            var authorization = request.Headers.Authorization?.ToString();
            Calls.Add((request.Method, path, query, content, authorization));

            if (request.Method == HttpMethod.Get && path == "/wp-json/wc/v3/taxes")
            {
                var pageValue = GetQueryParameter(query, "page");
                if (!string.IsNullOrEmpty(pageValue) && !string.Equals(pageValue, "1", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(JsonResponse("[]"));
                }

                var payload = JsonSerializer.Serialize(TaxRates.Select(rate => new
                {
                    id = rate.Id,
                    rate_code = rate.RateCode,
                    rate = rate.Rate,
                    name = rate.Name,
                    @class = rate.Class
                }));
                return Task.FromResult(JsonResponse(payload));
            }

            if (request.Method == HttpMethod.Post && path == "/wp-json/wc/v3/shipping/zones")
            {
                var id = ++_nextShippingZoneId;
                _lastCreatedShippingZoneId = id;
                _shippingZoneCreated = true;
                var responsePayload = new
                {
                    id,
                    name = ExtractZoneName(content),
                    order = ExtractZoneOrder(content),
                    locations = Array.Empty<object>(),
                    methods = Array.Empty<object>()
                };
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(responsePayload)));
            }

            if (path.StartsWith("/wp-json/wc/v3/shipping/zones/", StringComparison.Ordinal))
            {
                var suffix = path.Substring("/wp-json/wc/v3/shipping/zones/".Length);
                if (request.Method == HttpMethod.Put && !suffix.Contains('/', StringComparison.Ordinal))
                {
                    if (SimulateMissingShippingZone
                        && !_shippingZoneCreated
                        && suffix == MissingShippingZoneId.ToString(CultureInfo.InvariantCulture))
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                        {
                            Content = new StringContent("{\"message\":\"Zone not found\"}", Encoding.UTF8, "application/json")
                        });
                    }

                    var zoneId = int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedZoneId)
                        ? parsedZoneId
                        : 0;
                    return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new { id = zoneId })));
                }

                if (request.Method == HttpMethod.Put && suffix.EndsWith("/locations", StringComparison.Ordinal))
                {
                    return Task.FromResult(JsonResponse(string.IsNullOrWhiteSpace(content)
                        ? JsonSerializer.Serialize(Array.Empty<object>())
                        : content!));
                }

                if (suffix.Contains("/methods", StringComparison.Ordinal))
                {
                    var segments = suffix.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (request.Method == HttpMethod.Put && segments.Length >= 3)
                    {
                        var instanceId = int.TryParse(segments[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInstanceId)
                            ? parsedInstanceId
                            : 0;
                        return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new { id = "method", instance_id = parsedInstanceId })));
                    }

                    if (request.Method == HttpMethod.Post && segments.Length >= 2)
                    {
                        var instanceId = ++_nextShippingMethodInstanceId;
                        return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new { id = "method", instance_id = instanceId })));
                    }
                }
            }

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

            if (request.Method == HttpMethod.Get && path == "/wp-json/wc/v3/products" && query.Contains("sku=MIXED-IMAGE-SKU", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("[]"));
            }

            if (request.Method == HttpMethod.Get && path == "/wp-json/wc/v3/products" && query.Contains("slug=mixed-image-product", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("[]"));
            }

            if (request.Method == HttpMethod.Get && path == "/wp-json/wc/v3/products" && query.Contains("sku=MENU-PROD", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("[]"));
            }

            if (request.Method == HttpMethod.Get && path == "/wp-json/wc/v3/products" && query.Contains("slug=menu-product", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("[]"));
            }

            if (request.Method == HttpMethod.Get && path == "/wp-json/wc/v3/products" && query.Contains("sku=COUPON-PROD", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("[]"));
            }

            if (request.Method == HttpMethod.Get && path == "/wp-json/wc/v3/products" && query.Contains("slug=coupon-product", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("[]"));
            }

            if (request.Method == HttpMethod.Get && path == "/wp-json/wp/v2/media")
            {
                return Task.FromResult(JsonResponse("[]"));
            }

            if (request.Method == HttpMethod.Get && path == "/wp-json/wc/v3/products/attributes")
            {
                var page = ParsePageNumber(query);
                if (AttributePages.TryGetValue(page, out var records))
                {
                    var payload = JsonSerializer.Serialize(records.Select(record => new
                    {
                        id = record.Id,
                        name = record.Name,
                        slug = record.Slug
                    }));
                    return Task.FromResult(JsonResponse(payload));
                }

                return Task.FromResult(JsonResponse("[]"));
            }

            if (request.Method == HttpMethod.Get
                && path.StartsWith("/wp-json/wc/v3/products/attributes/", StringComparison.Ordinal)
                && path.EndsWith("/terms", StringComparison.Ordinal))
            {
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length >= 7
                    && int.TryParse(segments[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var attributeId))
                {
                    var page = ParsePageNumber(query);
                    if (AttributeTermPages.TryGetValue((attributeId, page), out var records))
                    {
                        var payload = JsonSerializer.Serialize(records.Select(record => new
                        {
                            id = record.Id,
                            name = record.Name,
                            slug = record.Slug
                        }));
                        return Task.FromResult(JsonResponse(payload));
                    }
                }

                return Task.FromResult(JsonResponse("[]"));
            }

            if (request.Method == HttpMethod.Post && path == "/wp-json/wc/v3/products/attributes")
            {
                return Task.FromResult(JsonResponse("{\"id\":10,\"name\":\"Color\",\"slug\":\"color\"}"));
            }

            if (request.Method == HttpMethod.Post && path == "/wp-json/wc/v3/products/attributes/10/terms")
            {
                return Task.FromResult(JsonResponse("{\"id\":100,\"name\":\"Blue\",\"slug\":\"blue\"}"));
            }

            if (request.Method == HttpMethod.Get && path == "/wp-json/wc/v3/products/categories")
            {
                return Task.FromResult(JsonResponse("[]"));
            }

            if (request.Method == HttpMethod.Get && path == "/wp-json/wc/v3/products/tags")
            {
                return Task.FromResult(JsonResponse("[]"));
            }

            if (request.Method == HttpMethod.Get && path == "/wp-json/wc/v3/coupons")
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

            if (request.Method == HttpMethod.Post && path == "/wp-json/wc/v3/products/tags")
            {
                if (string.IsNullOrWhiteSpace(content))
                {
                    throw new InvalidOperationException("Tag payload was empty.");
                }

                using var doc = JsonDocument.Parse(content);
                var slug = doc.RootElement.GetProperty("slug").GetString();
                if (string.IsNullOrWhiteSpace(slug))
                {
                    throw new InvalidOperationException("Tag slug missing in payload.");
                }

                var id = ++_nextTagId;
                _createdTagIds[slug!] = id;
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

            if (request.Method == HttpMethod.Get && path == "/wp-json/wp/v2/menus")
            {
                return Task.FromResult(JsonResponse("[]"));
            }

            if (request.Method == HttpMethod.Post && path == "/wp-json/wp/v2/menus")
            {
                var id = ++_nextMenuId;
                return Task.FromResult(JsonResponse($"{{\"id\":{id}}}"));
            }

            if (request.Method == HttpMethod.Get && path == "/wp-json/wp/v2/menu-items")
            {
                return Task.FromResult(JsonResponse("[]"));
            }

            if (request.Method == HttpMethod.Post && path == "/wp-json/wp/v2/menu-items")
            {
                var id = ++_nextMenuItemId;
                return Task.FromResult(JsonResponse($"{{\"id\":{id}}}"));
            }

            if (request.Method == HttpMethod.Delete && path.StartsWith("/wp-json/wp/v2/menu-items/", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("{}"));
            }

            if (request.Method == HttpMethod.Post && path.StartsWith("/wp-json/wp/v2/menu-locations/", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("{}"));
            }

            if (request.Method == HttpMethod.Post && path == "/wp-json/wc/v3/coupons")
            {
                return Task.FromResult(JsonResponse("{\"id\":900,\"code\":\"APPAREL10\"}"));
            }

            if (request.Method == HttpMethod.Post && path == "/wp-json/wc/v3/orders")
            {
                if (string.IsNullOrWhiteSpace(content))
                {
                    throw new InvalidOperationException("Order payload was empty.");
                }

                return Task.FromResult(JsonResponse("{\"id\":1001}"));
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

        public int? GetCreatedTagId(string slug)
        {
            return _createdTagIds.TryGetValue(slug, out var id) ? id : null;
        }

        private static HttpResponseMessage JsonResponse(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }

        private static string ExtractZoneName(string? content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return string.Empty;
            }

            using var doc = JsonDocument.Parse(content);
            return doc.RootElement.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() ?? string.Empty : string.Empty;
        }

        private static int ExtractZoneOrder(string? content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return 0;
            }

            using var doc = JsonDocument.Parse(content);
            return doc.RootElement.TryGetProperty("order", out var orderProperty) && orderProperty.ValueKind == JsonValueKind.Number
                ? orderProperty.GetInt32()
                : 0;
        }

        private static int ParsePageNumber(string query)
        {
            var pageValue = GetQueryParameter(query, "page");
            return int.TryParse(pageValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var page) && page > 0
                ? page
                : 1;
        }

        private static string? GetQueryParameter(string query, string key)
        {
            if (string.IsNullOrEmpty(query))
            {
                return null;
            }

            var trimmed = query.TrimStart('?');
            foreach (var segment in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = segment.Split('=', 2);
                if (parts.Length == 2 && string.Equals(parts[0], key, StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(parts[1]);
                }
            }

            return null;
        }

        public sealed class AttributeRecord
        {
            public int Id { get; init; }
            public string? Name { get; init; }
            public string? Slug { get; init; }
        }

        public sealed class TermRecord
        {
            public int Id { get; init; }
            public string? Name { get; init; }
            public string? Slug { get; init; }
        }

        public sealed class TaxRateRecord
        {
            public int Id { get; init; }
            public string? RateCode { get; init; }
            public string? Rate { get; init; }
            public string? Name { get; init; }
            public string? Class { get; init; }
        }
    }
}
