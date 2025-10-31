using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using WcScraper.Core;
using WcScraper.Wpf.Models;
using WcScraper.Wpf.Services;
using WcScraper.Wpf.ViewModels;
using Xunit;

namespace WcScraper.Wpf.Tests;

public sealed class ChatAssistantViewModelTests : IDisposable
{
    private readonly List<ChatAssistantTestHarness> _harnesses = new();

    public void Dispose()
    {
        foreach (var harness in _harnesses)
        {
            harness.Dispose();
        }

        _harnesses.Clear();
    }

    [Fact]
    public void OnChatUsageReported_AccumulatesTotalsAndResetsAfterClear()
    {
        using var harness = CreateHarness();
        var viewModel = harness.ViewModel;

        viewModel.ChatPromptTokenUsdPerThousand = 2m;
        viewModel.ChatCompletionTokenUsdPerThousand = 3m;

        viewModel.OnChatUsageReported(new ChatUsageSnapshot(500, 250, 750));
        viewModel.OnChatUsageReported(new ChatUsageSnapshot(1_200, 800, 2_000));

        Assert.Equal(1_700, viewModel.ChatPromptTokenTotal);
        Assert.Equal(1_050, viewModel.ChatCompletionTokenTotal);
        Assert.Equal(2_750, viewModel.ChatTotalTokenTotal);
        Assert.Equal(1_700, viewModel.TotalPromptTokens);
        Assert.Equal(1_050, viewModel.TotalCompletionTokens);
        Assert.Equal(2_750, viewModel.TotalTokens);
        Assert.Equal(6.55m, viewModel.TotalCostUsd);

        viewModel.ChatMessages.Add(new ChatMessage(ChatMessageRole.User, "hello"));
        Assert.True(viewModel.ClearChatHistoryCommand.CanExecute(null));
        Assert.True(viewModel.SaveChatTranscriptCommand.CanExecute(null));

        viewModel.ClearChatHistoryCommand.Execute(null);

        Assert.Empty(viewModel.ChatMessages);
        Assert.Equal(0, viewModel.ChatPromptTokenTotal);
        Assert.Equal(0, viewModel.ChatCompletionTokenTotal);
        Assert.Equal(0, viewModel.ChatTotalTokenTotal);
        Assert.Equal(0, viewModel.TotalPromptTokens);
        Assert.Equal(0, viewModel.TotalCompletionTokens);
        Assert.Equal(0, viewModel.TotalTokens);
        Assert.Equal(0m, viewModel.TotalCostUsd);
    }

    [Fact]
    public void CommandAvailability_UpdatesWithChatState()
    {
        using var harness = CreateHarness();
        var viewModel = harness.ViewModel;

        viewModel.ChatInput = "Hello";

        Assert.False(viewModel.SendChatCommand.CanExecute(null));
        Assert.False(viewModel.CancelChatCommand.CanExecute(null));
        Assert.False(viewModel.ClearChatHistoryCommand.CanExecute(null));
        Assert.False(viewModel.SaveChatTranscriptCommand.CanExecute(null));

        viewModel.ChatApiEndpoint = "https://example.local/ai";
        viewModel.ChatModel = "gpt-test";
        viewModel.ChatApiKey = "key-123";

        Assert.True(viewModel.SendChatCommand.CanExecute(null));
        Assert.False(viewModel.CancelChatCommand.CanExecute(null));

        viewModel.ChatMessages.Add(new ChatMessage(ChatMessageRole.User, "hello"));
        Assert.True(viewModel.ClearChatHistoryCommand.CanExecute(null));
        Assert.True(viewModel.SaveChatTranscriptCommand.CanExecute(null));

        SetIsChatBusy(viewModel, value: true);
        Assert.False(viewModel.SendChatCommand.CanExecute(null));
        Assert.True(viewModel.CancelChatCommand.CanExecute(null));

        SetIsChatBusy(viewModel, value: false);
        Assert.True(viewModel.SendChatCommand.CanExecute(null));
        Assert.False(viewModel.CancelChatCommand.CanExecute(null));

        viewModel.ChatMessages.Clear();
        Assert.False(viewModel.ClearChatHistoryCommand.CanExecute(null));
        Assert.False(viewModel.SaveChatTranscriptCommand.CanExecute(null));
    }

    [Fact]
    public async Task EnsureChatTranscriptLoadedAsync_LoadsMostRecentSession()
    {
        using var harness = CreateHarness(store =>
        {
            store.StartNewSession();
            store.AppendAsync(new ChatMessage(ChatMessageRole.User, "previous"))
                .GetAwaiter()
                .GetResult();
            store.AppendAsync(new ChatMessage(ChatMessageRole.Assistant, "response"))
                .GetAwaiter()
                .GetResult();
        });

        var viewModel = harness.ViewModel;

        await viewModel.EnsureChatTranscriptLoadedAsync();
        await viewModel.EnsureChatTranscriptLoadedAsync();

        Assert.True(viewModel.HasChatMessages);
        Assert.Equal(2, viewModel.ChatMessages.Count);
        Assert.Equal("previous", viewModel.ChatMessages[0].Content);
        Assert.Equal("response", viewModel.ChatMessages[1].Content);
        Assert.Contains("Resumed chat transcript", viewModel.ChatStatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessAssistantDirectives_DefersRiskyAndAppliesWhenConfirmed()
    {
        using var harness = CreateHarness(enableHttpRetries: false);
        var viewModel = harness.ViewModel;

        var directives = new AssistantDirectiveBatch(
            "Enable exports",
            new[]
            {
                new AssistantToggleDirective("ExportJsonl", true, "Export structured data", riskLevel: null, confidence: 0.9, RequiresConfirmation: false),
                new AssistantToggleDirective("ExportPublicDesignSnapshot", true, "Capture storefront", riskLevel: "medium", confidence: null, RequiresConfirmation: false),
            },
            new AssistantRetryDirective(true, 5, 2, 10, "Improve resiliency"),
            Array.Empty<AssistantActionDirective>(),
            Array.Empty<AssistantCredentialReminder>(),
            RequiresConfirmation: false,
            RiskNote: null);

        InvokeProcessAssistantDirectives(viewModel, directives, confirmed: false);

        Assert.False(harness.ExportJsonl);
        Assert.False(harness.ExportPublicDesignSnapshot);
        Assert.NotNull(GetPendingAssistantDirectives(viewModel));
        Assert.Contains(harness.Logs, log => log.Contains("directives require confirmation", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("pending confirmation", viewModel.ChatStatusMessage, StringComparison.OrdinalIgnoreCase);

        viewModel.ChatInput = "/apply-directives";
        await InvokeSendChatAsync(viewModel);

        Assert.True(harness.ExportJsonl);
        Assert.True(harness.ExportPublicDesignSnapshot);
        Assert.True(harness.EnableHttpRetries);
        Assert.Equal(5, harness.HttpRetryAttempts);
        Assert.Equal(2, harness.HttpRetryBaseDelaySeconds);
        Assert.Equal(10, harness.HttpRetryMaxDelaySeconds);
        Assert.Null(GetPendingAssistantDirectives(viewModel));
        Assert.Contains("directives applied", viewModel.ChatStatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(harness.Logs, log => log.Contains("Assistant enabled ExportJsonl", StringComparison.OrdinalIgnoreCase));
    }

    private ChatAssistantTestHarness CreateHarness(
        Action<ChatTranscriptStore>? transcriptInitializer = null,
        bool enableHttpRetries = true)
    {
        var harness = new ChatAssistantTestHarness(transcriptInitializer, enableHttpRetries);
        _harnesses.Add(harness);
        return harness;
    }

    private static void SetIsChatBusy(ChatAssistantViewModel viewModel, bool value)
    {
        var property = typeof(ChatAssistantViewModel)
            .GetProperty(nameof(ChatAssistantViewModel.IsChatBusy), BindingFlags.Instance | BindingFlags.Public);
        var setter = property?.GetSetMethod(nonPublic: true);
        setter?.Invoke(viewModel, new object[] { value });
    }

    private static void InvokeProcessAssistantDirectives(ChatAssistantViewModel viewModel, AssistantDirectiveBatch directives, bool confirmed)
    {
        var method = typeof(ChatAssistantViewModel)
            .GetMethod("ProcessAssistantDirectives", BindingFlags.Instance | BindingFlags.NonPublic);
        method?.Invoke(viewModel, new object[] { directives, confirmed });
    }

    private static AssistantDirectiveBatch? GetPendingAssistantDirectives(ChatAssistantViewModel viewModel)
    {
        var field = typeof(ChatAssistantViewModel)
            .GetField("_pendingAssistantDirectives", BindingFlags.Instance | BindingFlags.NonPublic);
        return (AssistantDirectiveBatch?)field?.GetValue(viewModel);
    }

    private static Task InvokeSendChatAsync(ChatAssistantViewModel viewModel)
    {
        var method = typeof(ChatAssistantViewModel)
            .GetMethod("OnSendChatAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        var task = (Task?)method?.Invoke(viewModel, Array.Empty<object>());
        return task ?? Task.CompletedTask;
    }

    private sealed class ChatAssistantTestHarness : IDisposable
    {
        private readonly ChatAssistantService _chatAssistantService;
        private bool _exportJsonl;
        private bool _exportPublicDesignSnapshot;
        private bool _enableHttpRetries;
        private int _httpRetryAttempts = 3;
        private double _httpRetryBaseDelaySeconds = 1;
        private double _httpRetryMaxDelaySeconds = 30;

        public ChatAssistantTestHarness(Action<ChatTranscriptStore>? transcriptInitializer, bool enableHttpRetries)
        {
            _enableHttpRetries = enableHttpRetries;

            SettingsDirectory = Path.Combine(Path.GetTempPath(), "ChatAssistantTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(SettingsDirectory);

            ArtifactIndexing = new FakeArtifactIndexingService();
            Dialogs = new TestDialogService();
            TranscriptStore = new ChatTranscriptStore(SettingsDirectory);
            transcriptInitializer?.Invoke(TranscriptStore);

            _chatAssistantService = new ChatAssistantService(ArtifactIndexing);
            Logs = new List<string>();

            var toggleBindings = new Dictionary<string, (Func<bool>, Action<bool>)>(StringComparer.OrdinalIgnoreCase)
            {
                [nameof(MainViewModel.ExportJsonl)] = (() => _exportJsonl, value => _exportJsonl = value),
                [nameof(MainViewModel.ExportPublicDesignSnapshot)] = (() => _exportPublicDesignSnapshot, value => _exportPublicDesignSnapshot = value),
            };

            ViewModel = new ChatAssistantViewModel(
                _chatAssistantService,
                TranscriptStore,
                ArtifactIndexing,
                Dialogs,
                Path.Combine(SettingsDirectory, "chat.key"));

            ViewModel.ConfigureHost(new ChatAssistantViewModel.HostConfiguration(
                Logs.Add,
                toggleBindings,
                BuildHostSnapshot,
                () => SettingsDirectory,
                () => SettingsDirectory,
                () => Array.Empty<string>(),
                () => true,
                () => { },
                () => false,
                _ => { },
                () => LatestStoreOutputFolder,
                () => LatestManualBundlePath,
                () => LatestManualReportPath,
                () => LatestRunDeltaPath,
                () => LatestRunAiBriefPath,
                () => LatestRunSnapshotJson,
                action => action(),
                () => _enableHttpRetries,
                value => _enableHttpRetries = value,
                () => _httpRetryAttempts,
                value => _httpRetryAttempts = value,
                () => _httpRetryBaseDelaySeconds,
                value => _httpRetryBaseDelaySeconds = value,
                () => _httpRetryMaxDelaySeconds,
                value => _httpRetryMaxDelaySeconds = value));
        }

        public ChatAssistantViewModel ViewModel { get; }
        public ChatTranscriptStore TranscriptStore { get; }
        public FakeArtifactIndexingService ArtifactIndexing { get; }
        public TestDialogService Dialogs { get; }
        public string SettingsDirectory { get; }
        public List<string> Logs { get; }
        public string? LatestStoreOutputFolder { get; set; }
        public string? LatestManualBundlePath { get; set; }
        public string? LatestManualReportPath { get; set; }
        public string? LatestRunDeltaPath { get; set; }
        public string? LatestRunAiBriefPath { get; set; }
        public string? LatestRunSnapshotJson { get; set; }

        public bool ExportJsonl => _exportJsonl;
        public bool ExportPublicDesignSnapshot => _exportPublicDesignSnapshot;
        public bool EnableHttpRetries => _enableHttpRetries;
        public int HttpRetryAttempts => _httpRetryAttempts;
        public double HttpRetryBaseDelaySeconds => _httpRetryBaseDelaySeconds;
        public double HttpRetryMaxDelaySeconds => _httpRetryMaxDelaySeconds;

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(SettingsDirectory))
                {
                    Directory.Delete(SettingsDirectory, recursive: true);
                }
            }
            catch
            {
            }
        }

        private ChatAssistantViewModel.HostSnapshot BuildHostSnapshot()
        {
            return new ChatAssistantViewModel.HostSnapshot(
                PlatformMode.WooCommerce,
                ExportCsv: true,
                ExportShopify: false,
                ExportWoo: true,
                ExportReviews: false,
                ExportXlsx: false,
                ExportJsonl: _exportJsonl,
                ExportPluginsCsv: false,
                ExportPluginsJsonl: false,
                ExportThemesCsv: false,
                ExportThemesJsonl: false,
                ExportPublicExtensionFootprints: false,
                ExportPublicDesignSnapshot: _exportPublicDesignSnapshot,
                ExportPublicDesignScreenshots: false,
                ExportStoreConfiguration: false,
                ImportStoreConfiguration: false,
                HasWordPressCredentials: false,
                HasShopifyCredentials: false,
                HasTargetCredentials: false,
                EnableHttpRetries: _enableHttpRetries,
                HttpRetryAttempts: _httpRetryAttempts,
                AdditionalPublicExtensionPages: string.Empty,
                AdditionalDesignSnapshotPages: string.Empty);
        }
    }

    private sealed class FakeArtifactIndexingService : IArtifactIndexingService
    {
        public event EventHandler? IndexChanged;

        public bool HasAnyIndexedArtifacts { get; set; }

        public int IndexedDatasetCount { get; set; }

        public Action<string>? DiagnosticLogger { get; set; }

        public void ResetForRun(string storeIdentifier, string runIdentifier)
        {
        }

        public Task IndexArtifactAsync(string filePath, System.Threading.CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<ArtifactSearchResult>> SearchAsync(string query, int take, System.Threading.CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ArtifactSearchResult>>(Array.Empty<ArtifactSearchResult>());

        public IReadOnlyList<AiIndexedDatasetReference> GetIndexedDatasets()
            => Array.Empty<AiIndexedDatasetReference>();

        public void RaiseIndexChanged() => IndexChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class TestDialogService : IDialogService
    {
        public string? BrowseForFolder(string? initial = null) => null;

        public void ShowLogWindow(MainViewModel viewModel)
        {
        }

        public void ShowRunCompletionDialog(ManualRunCompletionInfo info)
        {
        }

        public OnboardingWizardSettings? ShowOnboardingWizard(MainViewModel viewModel, ChatAssistantService chatAssistantService)
            => null;

        public string? SaveFile(string filter, string defaultFileName, string? initialDirectory = null) => null;
    }
}
