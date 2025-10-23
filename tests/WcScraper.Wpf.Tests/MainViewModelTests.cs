using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ClosedXML.Excel;
using WcScraper.Core;
using WcScraper.Core.Shopify;
using WcScraper.Wpf.Services;
using WcScraper.Wpf.ViewModels;
using Xunit;

namespace WcScraper.Wpf.Tests;

public class MainViewModelTests
{
    [Fact]
    [SuppressMessage("xUnit.Analyzers", "xUnit1031", Justification = "Test drives an STA thread and must block until the async operation completes.")]
    public async Task LoadFiltersForStoreAsync_AnonymousShopify_LoadsPublicCollections()
    {
        const string pageOne = """
        {
          "collections": [
            {
              "id": 1001,
              "handle": "frontpage",
              "title": "Frontpage",
              "body_html": "<p>Front page</p>",
              "products_count": "4",
              "image": {
                "id": "9001",
                "src": "https://cdn.example.com/frontpage.png",
                "alt": "Frontpage hero"
              }
            },
            {
              "id": 1002,
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

        var completion = new TaskCompletionSource<object?>();
        var thread = new Thread(() =>
        {
            try
            {
                var createdApp = false;
                var app = Application.Current;
                if (app is null)
                {
                    app = new Application();
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    createdApp = true;
                }

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
                using var shopifyScraper = new ShopifyScraper(httpClient);
                using var wooScraper = new WooScraper();

                var ctor = typeof(MainViewModel).GetConstructor(
                    BindingFlags.NonPublic | BindingFlags.Instance,
                    binder: null,
                    new[] { typeof(IDialogService), typeof(WooScraper), typeof(ShopifyScraper), typeof(HttpClient) },
                    modifiers: null);
                Assert.NotNull(ctor);

                var viewModel = (MainViewModel)ctor!.Invoke(new object[]
                {
                    new StubDialogService(),
                    wooScraper,
                    shopifyScraper,
                    httpClient
                });
                viewModel.SelectedPlatform = PlatformMode.Shopify;

                var messages = new List<string>();
                var logger = new Progress<string>(messages.Add);

                var method = typeof(MainViewModel)
                    .GetMethod("LoadFiltersForStoreAsync", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(method);

                var task = (Task?)method!.Invoke(viewModel, new object[]
                {
                    "https://example.myshopify.com",
                    logger
                });
                Assert.NotNull(task);
                task!.GetAwaiter().GetResult();

                app.Dispatcher.Invoke(() =>
                {
                    Assert.Equal(2, viewModel.CategoryChoices.Count);
                    Assert.Equal("Frontpage", viewModel.CategoryChoices[0].Name);
                    Assert.NotNull(viewModel.CategoryChoices[0].ShopifyCollection);
                    Assert.Equal(4, viewModel.CategoryChoices[0].ShopifyCollection!.ProductsCount);
                    Assert.Equal("https://cdn.example.com/frontpage.png", viewModel.CategoryChoices[0].ShopifyCollection!.Image?.Src);
                    Assert.Equal("Second Collection", viewModel.CategoryChoices[1].Name);
                    Assert.Equal("second-collection", viewModel.CategoryChoices[1].Term.Slug);
                    Assert.Empty(viewModel.TagChoices);
                });

                Assert.Equal(2, calls);
                Assert.DoesNotContain(messages, m => m.Contains("Skipping filter fetch", StringComparison.OrdinalIgnoreCase));

                if (createdApp)
                {
                    app.Shutdown();
                }

                completion.SetResult(null);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task;
    }

    [Fact]
    public void SelectAllAndClearCategories_TogglesEveryEntry()
    {
        using var wooScraper = new WooScraper();
        using var shopifyScraper = new ShopifyScraper();
        using var httpClient = new HttpClient();

        var ctor = typeof(MainViewModel).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            new[] { typeof(IDialogService), typeof(WooScraper), typeof(ShopifyScraper), typeof(HttpClient) },
            modifiers: null);
        Assert.NotNull(ctor);

        var viewModel = (MainViewModel)ctor!.Invoke(new object[]
        {
            new StubDialogService(),
            wooScraper,
            shopifyScraper,
            httpClient
        });

        viewModel.CategoryChoices.Add(new SelectableTerm(new TermItem { Id = 1, Name = "One" }));
        viewModel.CategoryChoices.Add(new SelectableTerm(new TermItem { Id = 2, Name = "Two" }));
        viewModel.CategoryChoices.Add(new SelectableTerm(new TermItem { Id = 3, Name = "Three" }));

        viewModel.SelectAllCategoriesCommand.Execute(null);
        Assert.All(viewModel.CategoryChoices, term => Assert.True(term.IsSelected));

        viewModel.ClearCategoriesCommand.Execute(null);
        Assert.All(viewModel.CategoryChoices, term => Assert.False(term.IsSelected));
    }

    [Fact]
    public void SelectAllAndClearTags_TogglesEveryEntry()
    {
        using var wooScraper = new WooScraper();
        using var shopifyScraper = new ShopifyScraper();
        using var httpClient = new HttpClient();

        var ctor = typeof(MainViewModel).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            new[] { typeof(IDialogService), typeof(WooScraper), typeof(ShopifyScraper), typeof(HttpClient) },
            modifiers: null);
        Assert.NotNull(ctor);

        var viewModel = (MainViewModel)ctor!.Invoke(new object[]
        {
            new StubDialogService(),
            wooScraper,
            shopifyScraper,
            httpClient
        });

        viewModel.TagChoices.Add(new SelectableTerm(new TermItem { Id = 1, Name = "Alpha" }) { IsSelected = true });
        viewModel.TagChoices.Add(new SelectableTerm(new TermItem { Id = 2, Name = "Beta" }) { IsSelected = false });

        viewModel.SelectAllTagsCommand.Execute(null);
        Assert.All(viewModel.TagChoices, term => Assert.True(term.IsSelected));

        viewModel.ClearTagsCommand.Execute(null);
        Assert.All(viewModel.TagChoices, term => Assert.False(term.IsSelected));
    }

    [Fact]
    public void ExportCollectionsCommand_WritesCollectionsWorkbook()
    {
        using var wooScraper = new WooScraper();
        using var shopifyScraper = new ShopifyScraper();
        using var httpClient = new HttpClient();

        var ctor = typeof(MainViewModel).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            new[] { typeof(IDialogService), typeof(WooScraper), typeof(ShopifyScraper), typeof(HttpClient) },
            modifiers: null);
        Assert.NotNull(ctor);

        var viewModel = (MainViewModel)ctor!.Invoke(new object[]
        {
            new StubDialogService(),
            wooScraper,
            shopifyScraper,
            httpClient
        });

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            viewModel.OutputFolder = tempDir;
            viewModel.SelectedPlatform = PlatformMode.Shopify;
            viewModel.ShopifyStoreUrl = "https://example.myshopify.com";
            const string collectionJson = """
            {
              "id": 1001,
              "handle": "frontpage",
              "title": "Frontpage",
              "body_html": "<p>Hero</p>",
              "published_at": "2023-05-01T00:00:00Z",
              "updated_at": "2023-05-02T00:00:00Z",
              "sort_order": "manual",
              "template_suffix": "custom",
              "published_scope": "web",
              "products_count": 5,
              "admin_graphql_api_id": "gid://shopify/Collection/1001",
              "published": true,
              "disjunctive": false,
              "rules": [
                { "column": "tag", "relation": "equals", "condition": "featured" }
              ],
              "image": {
                "id": 4321,
                "src": "https://cdn.example.com/frontpage.png",
                "alt": "Hero image",
                "width": 1024,
                "height": 512,
                "created_at": "2023-04-30T00:00:00Z"
              }
            }
            """;

            var detail = JsonSerializer.Deserialize<ShopifyCollectionDetails>(collectionJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            Assert.NotNull(detail);

            viewModel.CategoryChoices.Add(new SelectableTerm(detail!.ToTermItem(), detail));
            viewModel.CategoryChoices.Add(new SelectableTerm(new TermItem { Id = 2, Name = "Blog", Slug = "blog" }));
            viewModel.TagChoices.Add(new SelectableTerm(new TermItem { Id = 3, Name = "Featured", Slug = "featured" }));

            viewModel.ExportCollectionsCommand.Execute(null);

            var storeIdMethod = typeof(MainViewModel)
                .GetMethod("BuildStoreIdentifier", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(storeIdMethod);

            var storeId = (string)storeIdMethod!.Invoke(null, new object[] { viewModel.ShopifyStoreUrl })!;
            var storeFolder = Path.Combine(tempDir, storeId);
            Assert.True(Directory.Exists(storeFolder));

            var collectionsFiles = Directory.GetFiles(storeFolder, $"{storeId}_*_collections.xlsx");
            Assert.Single(collectionsFiles);
            var collectionsPath = collectionsFiles[0];

            using (var workbook = new XLWorkbook(collectionsPath))
            {
                var worksheet = workbook.Worksheet(1);
                var expectedHeaders = new[]
                {
                    "term_id",
                    "term_name",
                    "term_slug",
                    "collection_id",
                    "handle",
                    "title",
                    "body_html",
                    "published_at",
                    "updated_at",
                    "sort_order",
                    "template_suffix",
                    "published_scope",
                    "products_count",
                    "admin_graphql_api_id",
                    "published",
                    "disjunctive",
                    "rules",
                    "image_id",
                    "image_src",
                    "image_alt",
                    "image_width",
                    "image_height",
                    "image_created_at"
                };

                for (var i = 0; i < expectedHeaders.Length; i++)
                {
                    Assert.Equal(expectedHeaders[i], worksheet.Cell(1, i + 1).GetString());
                }

                Assert.Equal(1001, worksheet.Cell(2, 1).GetValue<int>());
                Assert.Equal("Frontpage", worksheet.Cell(2, 2).GetString());
                Assert.Equal("frontpage", worksheet.Cell(2, 3).GetString());
                Assert.Equal(1001L, worksheet.Cell(2, 4).GetValue<long>());
                Assert.Equal("frontpage", worksheet.Cell(2, 5).GetString());
                Assert.Equal("Frontpage", worksheet.Cell(2, 6).GetString());
                Assert.Equal("<p>Hero</p>", worksheet.Cell(2, 7).GetString());
                Assert.Equal("2023-05-01T00:00:00Z", worksheet.Cell(2, 8).GetString());
                Assert.Equal("2023-05-02T00:00:00Z", worksheet.Cell(2, 9).GetString());
                Assert.Equal("manual", worksheet.Cell(2, 10).GetString());
                Assert.Equal("custom", worksheet.Cell(2, 11).GetString());
                Assert.Equal("web", worksheet.Cell(2, 12).GetString());
                Assert.Equal(5, worksheet.Cell(2, 13).GetValue<int>());
                Assert.Equal("gid://shopify/Collection/1001", worksheet.Cell(2, 14).GetString());
                Assert.True(worksheet.Cell(2, 15).GetBoolean());
                Assert.False(worksheet.Cell(2, 16).GetBoolean());
                Assert.Equal("tag:equals:featured", worksheet.Cell(2, 17).GetString());
                Assert.Equal(4321L, worksheet.Cell(2, 18).GetValue<long>());
                Assert.Equal("https://cdn.example.com/frontpage.png", worksheet.Cell(2, 19).GetString());
                Assert.Equal("Hero image", worksheet.Cell(2, 20).GetString());
                Assert.Equal(1024, worksheet.Cell(2, 21).GetValue<int>());
                Assert.Equal(512, worksheet.Cell(2, 22).GetValue<int>());
                Assert.Equal("2023-04-30T00:00:00Z", worksheet.Cell(2, 23).GetString());

                Assert.Equal(2, worksheet.Cell(3, 1).GetValue<int>());
                Assert.Equal("Blog", worksheet.Cell(3, 2).GetString());
                Assert.Equal("blog", worksheet.Cell(3, 3).GetString());
                Assert.True(string.IsNullOrEmpty(worksheet.Cell(3, 4).GetString()));
                Assert.Equal("blog", worksheet.Cell(3, 5).GetString());
                Assert.Equal("Blog", worksheet.Cell(3, 6).GetString());
            }

            var tagsFiles = Directory.GetFiles(storeFolder, $"{storeId}_*_tags.xlsx");
            Assert.Single(tagsFiles);
            var tagsPath = tagsFiles[0];

            using var tagsWorkbook = new XLWorkbook(tagsPath);
            var tagsWorksheet = tagsWorkbook.Worksheet(1);
            var lastTagRow = tagsWorksheet.LastRowUsed();
            Assert.NotNull(lastTagRow);
            Assert.Equal(2, lastTagRow!.RowNumber());
            Assert.Equal("Featured", tagsWorksheet.Cell(2, 2).GetString());
            Assert.Equal("featured", tagsWorksheet.Cell(2, 3).GetString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    [SuppressMessage("xUnit.Analyzers", "xUnit1031", Justification = "Test drives an STA thread and must block until completion.")]
    public async Task OnRunAsync_InvalidStoreUrl_ReportsFriendlyMessage()
    {
        var completion = new TaskCompletionSource<object?>();
        var thread = new Thread(() =>
        {
            try
            {
                var createdApp = false;
                var app = Application.Current;
                if (app is null)
                {
                    app = new Application
                    {
                        ShutdownMode = ShutdownMode.OnExplicitShutdown
                    };
                    createdApp = true;
                }

                using var wooHandler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("HTTP should not be called for invalid URLs."));
                using var wooHttp = new HttpClient(wooHandler);
                using var wooScraper = new WooScraper(wooHttp);
                using var shopifyHandler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Shopify should not be called."));
                using var shopifyHttp = new HttpClient(shopifyHandler);
                using var shopifyScraper = new ShopifyScraper(shopifyHttp);
                using var httpClient = new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("HTTP should not be called.")));

                var ctor = typeof(MainViewModel).GetConstructor(
                    BindingFlags.NonPublic | BindingFlags.Instance,
                    binder: null,
                    new[] { typeof(IDialogService), typeof(WooScraper), typeof(ShopifyScraper), typeof(HttpClient) },
                    modifiers: null);
                Assert.NotNull(ctor);

                var viewModel = (MainViewModel)ctor!.Invoke(new object[]
                {
                    new StubDialogService(),
                    wooScraper,
                    shopifyScraper,
                    httpClient
                });

                viewModel.StoreUrl = "not a url";
                viewModel.SelectedPlatform = PlatformMode.WooCommerce;

                var onRun = typeof(MainViewModel)
                    .GetMethod("OnRunAsync", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(onRun);

                var task = (Task)onRun!.Invoke(viewModel, Array.Empty<object>())!;
                task.GetAwaiter().GetResult();

                app.Dispatcher.Invoke(() =>
                {
                    Assert.Contains(viewModel.Logs, message => message.Contains("Invalid store URL", StringComparison.Ordinal));
                });

                if (createdApp)
                {
                    app.Shutdown();
                }

                completion.SetResult(null);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.ConfigureAwait(false);
    }

    [Fact]
    public async Task OnRunAsync_WooCommerce_WritesFilesToStoreFolder()
    {
        const string productResponse = """
        [
          {
            "id": 101,
            "name": "Sample",
            "slug": "sample",
            "permalink": "https://example.com/store/product/sample",
            "sku": "SKU-1",
            "type": "variable",
            "meta_title": "Sample Title",
            "meta_description": "Sample Description",
            "meta_keywords": "sample,keywords",
            "prices": {
              "currency_code": "USD",
              "price": "19.99",
              "regular_price": "19.99"
            },
            "is_in_stock": true,
            "stock_status": "instock",
            "average_rating": 4.5,
            "review_count": 3,
            "has_options": true,
            "categories": [
              { "id": 1, "name": "Cat", "slug": "cat" }
            ],
            "tags": [
              { "id": 2, "name": "Tag", "slug": "tag" }
            ],
            "images": [
              { "id": 10, "src": "https://example.com/assets/sample-one.png", "alt": "Front" },
              { "id": 11, "src": "https://example.com/assets/sample-two.jpg?size=large", "alt": "Back" }
            ]
          }
        ]
        """;

        const string variationResponse = """
        [
          {
            "id": 201,
            "name": "Sample - Blue",
            "slug": "sample-blue",
            "sku": "SKU-1-BLU",
            "type": "variation",
            "parent": 101,
            "prices": {
              "currency_code": "USD",
              "price": "21.99",
              "regular_price": "21.99"
            },
            "is_in_stock": true,
            "stock_status": "instock",
            "attributes": [
              { "name": "Color", "taxonomy": "pa_color", "option": "Blue" }
            ],
            "images": [
              { "id": 12, "src": "https://example.com/assets/sample-blue.png", "alt": "Blue" }
            ]
          }
        ]
        """;

        using var wooHandler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/wp-json/wc/store/v1/products/categories", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json")
                };
            }

            if (path.EndsWith("/wp-json/wc/store/v1/products/tags", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json")
                };
            }

            if (path.EndsWith("/wp-json/wc/store/v1/products", StringComparison.Ordinal) &&
                request.RequestUri!.Query.Contains("type=variation", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(variationResponse, Encoding.UTF8, "application/json")
                };
            }

            if (path.EndsWith("/wp-json/wc/store/v1/products", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(productResponse, Encoding.UTF8, "application/json")
                };
            }

            if (path.EndsWith("sample-one.png", StringComparison.Ordinal) ||
                path.EndsWith("sample-two.jpg", StringComparison.Ordinal) ||
                path.EndsWith("sample-blue.png", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 })
                };
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        using var wooHttp = new HttpClient(wooHandler);
        using var wooScraper = new WooScraper(wooHttp);
        using var shopifyScraper = new ShopifyScraper();

        var ctor = typeof(MainViewModel).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            new[] { typeof(IDialogService), typeof(WooScraper), typeof(ShopifyScraper), typeof(HttpClient) },
            modifiers: null);
        Assert.NotNull(ctor);

        var viewModel = (MainViewModel)ctor!.Invoke(new object[]
        {
            new StubDialogService(),
            wooScraper,
            shopifyScraper,
            wooHttp
        });

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            viewModel.OutputFolder = tempDir;
            viewModel.StoreUrl = "https://example.com/store";
            viewModel.SelectedPlatform = PlatformMode.WooCommerce;
            viewModel.ExportCsv = true;
            viewModel.ExportJsonl = true;
            viewModel.ExportShopify = true;
            viewModel.ExportWoo = true;
            viewModel.ExportReviews = false;
            viewModel.ExportXlsx = false;

            var onRun = typeof(MainViewModel)
                .GetMethod("OnRunAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(onRun);

            var task = (Task)onRun!.Invoke(viewModel, Array.Empty<object>())!;
            await task;

            var storeIdMethod = typeof(MainViewModel)
                .GetMethod("BuildStoreIdentifier", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(storeIdMethod);

            var storeId = (string)storeIdMethod!.Invoke(null, new object[] { viewModel.StoreUrl })!;
            var storeFolder = Path.Combine(tempDir, storeId);
            Assert.True(Directory.Exists(storeFolder));

            var topLevelFiles = Directory.GetFiles(tempDir, "*", SearchOption.TopDirectoryOnly);
            Assert.DoesNotContain(topLevelFiles, f => Path.GetFileName(f).StartsWith(storeId, StringComparison.Ordinal));

            var productsCsv = Directory.GetFiles(storeFolder, $"{storeId}_*_products.csv");
            var productsJsonl = Directory.GetFiles(storeFolder, $"{storeId}_*_products.jsonl");
            var shopifyCsv = Directory.GetFiles(storeFolder, $"{storeId}_*_shopify_products.csv");
            var wooCsv = Directory.GetFiles(storeFolder, $"{storeId}_*_woocommerce_products.csv");

            Assert.Single(productsCsv);
            Assert.Single(productsJsonl);
            Assert.Single(shopifyCsv);
            Assert.Single(wooCsv);

            var imagesFolder = Path.Combine(storeFolder, "images");
            Assert.True(Directory.Exists(imagesFolder));
            var imageFiles = Directory.GetFiles(imagesFolder)
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            Assert.Equal(new[] { "Sample-1.png", "Sample-2.jpg" }, imageFiles);

            var jsonLines = File.ReadAllLines(productsJsonl[0]);
            Assert.Single(jsonLines);
            using (var doc = JsonDocument.Parse(jsonLines[0]))
            {
                Assert.True(doc.RootElement.TryGetProperty("image_file_paths", out var pathProp));
                Assert.Equal("images/Sample-1.png, images/Sample-2.jpg", pathProp.GetString());
            }

            var csvLines = File.ReadAllLines(productsCsv[0]);
            Assert.Contains("image_file_paths", csvLines[0]);
            Assert.Contains("images/Sample-1.png, images/Sample-2.jpg", csvLines[1]);

            var shopifyCsvText = File.ReadAllText(shopifyCsv[0]);
            Assert.Contains("Image Src Local", shopifyCsvText);
            Assert.Contains("images/Sample-1.png, images/Sample-2.jpg", shopifyCsvText);

            var wooCsvText = File.ReadAllText(wooCsv[0]);
            Assert.Contains("Image File Paths", wooCsvText);
            Assert.Contains("images/Sample-1.png, images/Sample-2.jpg", wooCsvText);

            var timestamps = new HashSet<string>(StringComparer.Ordinal);

            static string ExtractTimestamp(string fileName, string storeId, string suffix)
            {
                var prefix = storeId + "_";
                var start = prefix.Length;
                var index = fileName.IndexOf(suffix, start, StringComparison.Ordinal);
                Assert.True(index > start, $"Suffix '{suffix}' not found in '{fileName}'");
                return fileName[start..index];
            }

            void CaptureTimestamp(string filePath, string suffix)
            {
                var fileName = Path.GetFileName(filePath);
                timestamps.Add(ExtractTimestamp(fileName, storeId, suffix));
            }

            CaptureTimestamp(productsCsv[0], "_products");
            CaptureTimestamp(productsJsonl[0], "_products");
            CaptureTimestamp(shopifyCsv[0], "_shopify_products");
            CaptureTimestamp(wooCsv[0], "_woocommerce_products");

            Assert.Single(timestamps);

            var contextField = typeof(MainViewModel)
                .GetField("_lastProvisioningContext", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(contextField);
            var context = contextField!.GetValue(viewModel);
            Assert.NotNull(context);
            var variationsProperty = context!.GetType().GetProperty("Variations", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(variationsProperty);
            var storedVariations = (IEnumerable<StoreProduct>)variationsProperty!.GetValue(context)!;
            var storedVariation = Assert.Single(storedVariations);
            Assert.Equal("SKU-1-BLU", storedVariation.Sku);
            Assert.False(string.IsNullOrWhiteSpace(storedVariation.ImageFilePaths));
            Assert.StartsWith("images/", storedVariation.ImageFilePaths, StringComparison.Ordinal);
            Assert.Single(storedVariation.LocalImageFilePaths);
            var variationImageFull = Path.Combine(storeFolder, storedVariation.ImageFilePaths!.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(variationImageFull));
            Assert.Equal(Path.GetFullPath(variationImageFull), Path.GetFullPath(storedVariation.LocalImageFilePaths[0]));

            var variableProductsProperty = context.GetType().GetProperty("VariableProducts", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(variableProductsProperty);
            var storedVariableProducts = (IEnumerable<ProvisioningVariableProduct>)variableProductsProperty!.GetValue(context)!;
            var variableProduct = Assert.Single(storedVariableProducts);
            Assert.Same(storedVariation, Assert.Single(variableProduct.Variations));
            Assert.Equal(storedVariation.ParentId, variableProduct.Parent.Id);
            Assert.Contains(viewModel.Logs, message => message.Contains("Found 1 variations", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task OnRunAsync_Shopify_ExportsVendorFromStoreDomain()
    {
        const string baseUrl = "https://example.myshopify.com";
        const string collectionsResponse = """
        {
          "collections": []
        }
        """;

        const string productsPageOne = """
        {
          "products": [
            {
              "id": 501,
              "title": "Shopify Sample",
              "body_html": "<p>Sample</p>",
              "product_type": "Apparel",
              "handle": "shopify-sample",
              "status": "active",
              "variants": [
                {
                  "id": 601,
                  "sku": "SKU-500",
                  "price": "10.00",
                  "inventory_quantity": 3
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
                  "id": 701,
                  "src": "https://example.myshopify.com/cdn/sample.png",
                  "alt": "Sample"
                }
              ]
            }
          ]
        }
        """;

        const string productsPageTwo = """
        {
          "products": []
        }
        """;

        using var handler = new StubHttpMessageHandler(request =>
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing URI");

            if (uri.AbsoluteUri.Equals("https://example.myshopify.com/cdn/sample.png", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
                };
            }

            Assert.Equal(HttpMethod.Get, request.Method);

            if (uri.AbsolutePath.EndsWith("/collections.json", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(collectionsResponse, Encoding.UTF8, "application/json")
                };
            }

            if (uri.AbsolutePath.EndsWith("/products.json", StringComparison.Ordinal))
            {
                var payload = uri.Query.Contains("page=1", StringComparison.Ordinal)
                    ? productsPageOne
                    : productsPageTwo;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        using var httpClient = new HttpClient(handler);
        using var wooScraper = new WooScraper();
        using var shopifyScraper = new ShopifyScraper(httpClient);

        var ctor = typeof(MainViewModel).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            new[] { typeof(IDialogService), typeof(WooScraper), typeof(ShopifyScraper), typeof(HttpClient) },
            modifiers: null);
        Assert.NotNull(ctor);

        var viewModel = (MainViewModel)ctor!.Invoke(new object[]
        {
            new StubDialogService(),
            wooScraper,
            shopifyScraper,
            httpClient
        });

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            viewModel.OutputFolder = tempDir;
            viewModel.ShopifyStoreUrl = baseUrl;
            viewModel.SelectedPlatform = PlatformMode.Shopify;
            viewModel.ExportCsv = false;
            viewModel.ExportJsonl = false;
            viewModel.ExportShopify = true;
            viewModel.ExportWoo = false;
            viewModel.ExportReviews = false;
            viewModel.ExportXlsx = false;

            var onRun = typeof(MainViewModel)
                .GetMethod("OnRunAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(onRun);

            var task = (Task)onRun!.Invoke(viewModel, Array.Empty<object>())!;
            await task;

            var storeIdMethod = typeof(MainViewModel)
                .GetMethod("BuildStoreIdentifier", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(storeIdMethod);

            var storeId = (string)storeIdMethod!.Invoke(null, new object[] { baseUrl })!;
            var storeFolder = Path.Combine(tempDir, storeId);
            Assert.True(Directory.Exists(storeFolder));

            var shopifyCsv = Directory.GetFiles(storeFolder, $"{storeId}_*_shopify_products.csv");
            var shopifyPath = Assert.Single(shopifyCsv);

            var lines = File.ReadAllLines(shopifyPath);
            Assert.True(lines.Length >= 2, "Shopify CSV should contain header and at least one row.");

            var headers = lines[0].Split(',');
            var vendorIndex = Array.FindIndex(headers, h => string.Equals(h.Trim('"'), "Vendor", StringComparison.Ordinal));
            Assert.True(vendorIndex >= 0, "Vendor column was not found in Shopify CSV header.");

            var values = lines[1].Split(',');
            Assert.InRange(vendorIndex, 0, values.Length - 1);
            Assert.Equal("example.myshopify.com", values[vendorIndex].Trim('"'));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class StubDialogService : IDialogService
    {
        public string? BrowseForFolder(string? initial = null) => null;
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
