using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
