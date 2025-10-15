using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using WcScraper.Wpf.Services;
using WcScraper.Wpf.ViewModels;
using Xunit;

namespace WcScraper.Wpf.Tests;

public class MainViewModelTests
{
    [Fact]
    public async Task LoadFiltersForStoreAsync_AnonymousShopify_SkipsFilterFetch()
    {
        var viewModel = new MainViewModel(new StubDialogService())
        {
            SelectedPlatform = PlatformMode.Shopify
        };

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
        await task!;

        Assert.Empty(viewModel.CategoryChoices);
        Assert.Empty(viewModel.TagChoices);
        Assert.Contains(messages, m => m.Contains("Skipping filter fetch", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(messages, m => m.StartsWith("Filter load failed", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class StubDialogService : IDialogService
    {
        public string? BrowseForFolder(string? initial = null) => null;
    }
}
