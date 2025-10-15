using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
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
    public async Task LoadFiltersForStoreAsync_AnonymousShopify_LoadsPublicCollections()
    {
        const string pageOne = """
        {
          "collections": [
            {
              "id": 1001,
              "handle": "frontpage",
              "title": "Frontpage"
            },
            {
              "id": 1002,
              "title": "Second Collection"
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
                    new[] { typeof(IDialogService), typeof(WooScraper), typeof(ShopifyScraper) },
                    modifiers: null);
                Assert.NotNull(ctor);

                var viewModel = (MainViewModel)ctor!.Invoke(new object[]
                {
                    new StubDialogService(),
                    wooScraper,
                    shopifyScraper
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
                    Assert.Equal("Second Collection", viewModel.CategoryChoices[1].Name);
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

        var ctor = typeof(MainViewModel).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            new[] { typeof(IDialogService), typeof(WooScraper), typeof(ShopifyScraper) },
            modifiers: null);
        Assert.NotNull(ctor);

        var viewModel = (MainViewModel)ctor!.Invoke(new object[]
        {
            new StubDialogService(),
            wooScraper,
            shopifyScraper
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

        var ctor = typeof(MainViewModel).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            new[] { typeof(IDialogService), typeof(WooScraper), typeof(ShopifyScraper) },
            modifiers: null);
        Assert.NotNull(ctor);

        var viewModel = (MainViewModel)ctor!.Invoke(new object[]
        {
            new StubDialogService(),
            wooScraper,
            shopifyScraper
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

        var ctor = typeof(MainViewModel).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            new[] { typeof(IDialogService), typeof(WooScraper), typeof(ShopifyScraper) },
            modifiers: null);
        Assert.NotNull(ctor);

        var viewModel = (MainViewModel)ctor!.Invoke(new object[]
        {
            new StubDialogService(),
            wooScraper,
            shopifyScraper
        });

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            viewModel.OutputFolder = tempDir;
            viewModel.CategoryChoices.Add(new SelectableTerm(new TermItem { Id = 1, Name = "Frontpage", Slug = "frontpage" }));
            viewModel.CategoryChoices.Add(new SelectableTerm(new TermItem { Id = 2, Name = "Blog", Slug = "blog" }));
            viewModel.TagChoices.Add(new SelectableTerm(new TermItem { Id = 3, Name = "Featured", Slug = "featured" }));

            viewModel.ExportCollectionsCommand.Execute(null);

            var collectionsPath = Path.Combine(tempDir, "collections.xlsx");
            Assert.True(File.Exists(collectionsPath));

            using (var workbook = new XLWorkbook(collectionsPath))
            {
                var worksheet = workbook.Worksheet(1);
                Assert.Equal("id", worksheet.Cell(1, 1).GetString());
                Assert.Equal("name", worksheet.Cell(1, 2).GetString());
                Assert.Equal("slug", worksheet.Cell(1, 3).GetString());

                Assert.Equal(1, worksheet.Cell(2, 1).GetValue<int>());
                Assert.Equal("Frontpage", worksheet.Cell(2, 2).GetString());
                Assert.Equal("frontpage", worksheet.Cell(2, 3).GetString());

                Assert.Equal(2, worksheet.Cell(3, 1).GetValue<int>());
                Assert.Equal("Blog", worksheet.Cell(3, 2).GetString());
                Assert.Equal("blog", worksheet.Cell(3, 3).GetString());
            }

            var tagsPath = Path.Combine(tempDir, "tags.xlsx");
            Assert.True(File.Exists(tagsPath));

            using var tagsWorkbook = new XLWorkbook(tagsPath);
            var tagsWorksheet = tagsWorkbook.Worksheet(1);
            Assert.Equal(2, tagsWorksheet.LastRowUsed().RowNumber());
            Assert.Equal("Featured", tagsWorksheet.Cell(2, 2).GetString());
            Assert.Equal("featured", tagsWorksheet.Cell(2, 3).GetString());
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
