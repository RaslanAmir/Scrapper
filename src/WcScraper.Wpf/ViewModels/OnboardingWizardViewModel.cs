using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WcScraper.Wpf.Models;
using WcScraper.Wpf.Services;
using WcScraper.Wpf;

namespace WcScraper.Wpf.ViewModels;

public sealed class OnboardingWizardViewModel : INotifyPropertyChanged, IDisposable
{
    private sealed record WizardAnswer(string Question, string Answer);

    private sealed class WizardSettingsContract
    {
        public WizardExports? Exports { get; set; }
        public WizardCredentials? Credentials { get; set; }
        public WizardRetry? Retry { get; set; }
        public string? Summary { get; set; }
    }

    private sealed class WizardExports
    {
        public bool? Csv { get; set; }
        public bool? Shopify { get; set; }
        public bool? Woo { get; set; }
        public bool? Reviews { get; set; }
        public bool? Xlsx { get; set; }
        public bool? Jsonl { get; set; }
        public bool? PluginsCsv { get; set; }
        public bool? PluginsJsonl { get; set; }
        public bool? ThemesCsv { get; set; }
        public bool? ThemesJsonl { get; set; }
        public bool? PublicExtensionFootprints { get; set; }
        public bool? PublicDesignSnapshot { get; set; }
        public bool? PublicDesignScreenshots { get; set; }
        public bool? StoreConfiguration { get; set; }
        public bool? ImportStoreConfiguration { get; set; }
    }

    private sealed class WizardCredentials
    {
        public string? WordPressUsername { get; set; }
        public string? WordPressApplicationPassword { get; set; }
        public string? ShopifyStoreUrl { get; set; }
        public string? ShopifyAdminAccessToken { get; set; }
        public string? ShopifyStorefrontAccessToken { get; set; }
        public string? ShopifyApiKey { get; set; }
        public string? ShopifyApiSecret { get; set; }
    }

    private sealed class WizardRetry
    {
        public bool? Enable { get; set; }
        public int? Attempts { get; set; }
        public double? BaseDelaySeconds { get; set; }
        public double? MaxDelaySeconds { get; set; }
    }

    private readonly MainViewModel _mainViewModel;
    private readonly ChatAssistantService _chatAssistantService;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<ChatMessage> _history = new();
    private readonly List<WizardAnswer> _answers = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private bool _isBusy;
    private string _currentAnswer = string.Empty;
    private string _statusMessage = "Connect to the assistant to begin.";
    private bool _hasActiveQuestion;
    private string? _latestAssistantPrompt;
    private bool _initializeRequested;

    public OnboardingWizardViewModel(MainViewModel mainViewModel, ChatAssistantService chatAssistantService)
    {
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _chatAssistantService = chatAssistantService ?? throw new ArgumentNullException(nameof(chatAssistantService));

        Conversation = new ObservableCollection<ChatMessage>();

        SubmitAnswerCommand = new RelayCommand(() => _ = OnSubmitAnswerAsync(), CanSubmitAnswer);
        CompleteCommand = new RelayCommand(() => _ = OnCompleteAsync(), CanComplete);
        CancelCommand = new RelayCommand(OnCancel);
    }

    public ObservableCollection<ChatMessage> Conversation { get; }

    public RelayCommand SubmitAnswerCommand { get; }

    public RelayCommand CompleteCommand { get; }

    public RelayCommand CancelCommand { get; }

    public event EventHandler? Completed;

    public event EventHandler? Cancelled;

    public OnboardingWizardSettings? Result { get; private set; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value)
            {
                return;
            }

            _isBusy = value;
            OnPropertyChanged();
            SubmitAnswerCommand.RaiseCanExecuteChanged();
            CompleteCommand.RaiseCanExecuteChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage == value)
            {
                return;
            }

            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public string CurrentAnswer
    {
        get => _currentAnswer;
        set
        {
            var newValue = value ?? string.Empty;
            if (_currentAnswer == newValue)
            {
                return;
            }

            _currentAnswer = newValue;
            OnPropertyChanged();
            SubmitAnswerCommand.RaiseCanExecuteChanged();
        }
    }

    public async Task InitializeAsync()
    {
        if (_initializeRequested)
        {
            return;
        }

        _initializeRequested = true;

        Conversation.Clear();
        _history.Clear();
        _answers.Clear();
        _hasActiveQuestion = false;
        _latestAssistantPrompt = null;

        if (!_mainViewModel.HasChatConfiguration)
        {
            StatusMessage = "Enter an AI endpoint, model, and API key before starting the wizard.";
            return;
        }

        var systemMessage = new ChatMessage(ChatMessageRole.System, BuildSystemPrompt());
        _history.Add(systemMessage);

        StatusMessage = "Requesting opening question…";
        await RequestNextQuestionAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }

        _cts.Dispose();
    }

    private bool CanSubmitAnswer()
        => !_isBusy && _hasActiveQuestion && !string.IsNullOrWhiteSpace(_currentAnswer);

    private bool CanComplete()
        => !_isBusy && _answers.Count > 0;

    private void OnCancel()
    {
        _cts.Cancel();
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    private async Task OnSubmitAnswerAsync()
    {
        if (!CanSubmitAnswer())
        {
            return;
        }

        var trimmed = _currentAnswer.Trim();
        CurrentAnswer = string.Empty;

        var question = _latestAssistantPrompt ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(trimmed))
        {
            _answers.Add(new WizardAnswer(question, trimmed));
        }

        var userMessage = new ChatMessage(ChatMessageRole.User, trimmed);
        _history.Add(userMessage);
        Conversation.Add(userMessage);

        await RequestNextQuestionAsync().ConfigureAwait(false);
    }

    private async Task RequestNextQuestionAsync()
    {
        if (!_mainViewModel.HasChatConfiguration)
        {
            StatusMessage = "Assistant configuration missing.";
            return;
        }

        var session = CreateSession();
        if (session is null)
        {
            StatusMessage = "Assistant configuration missing.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Waiting for assistant…";

        ChatMessage? assistantMessage = null;

        try
        {
            var history = _history.ToList();
            assistantMessage = new ChatMessage(ChatMessageRole.Assistant, string.Empty);
            _history.Add(assistantMessage);
            Conversation.Add(assistantMessage);

            var context = BuildContextSummary();
            await foreach (var token in _chatAssistantService.StreamChatCompletionAsync(session, history, context, null, _cts.Token).ConfigureAwait(false))
            {
                assistantMessage.Append(token);
            }

            var content = assistantMessage.Content?.Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                assistantMessage.Content = "(No response received.)";
                StatusMessage = "The assistant did not provide a question.";
                _hasActiveQuestion = false;
            }
            else
            {
                _latestAssistantPrompt = content;
                _hasActiveQuestion = true;
                StatusMessage = "Answer the assistant to continue.";
            }
        }
        catch (OperationCanceledException)
        {
            _hasActiveQuestion = false;
            if (assistantMessage is not null)
            {
                assistantMessage.Content = "(Cancelled)";
            }
            _latestAssistantPrompt = null;
        }
        catch (Exception ex)
        {
            if (assistantMessage is not null)
            {
                assistantMessage.Content = $"Assistant error: {ex.Message}";
            }
            else
            {
                Conversation.Add(new ChatMessage(ChatMessageRole.Assistant, $"Assistant error: {ex.Message}"));
            }
            StatusMessage = "Assistant request failed.";
            _hasActiveQuestion = false;
            _latestAssistantPrompt = null;
        }
        finally
        {
            IsBusy = false;
            SubmitAnswerCommand.RaiseCanExecuteChanged();
            CompleteCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task OnCompleteAsync()
    {
        if (!CanComplete())
        {
            return;
        }

        var session = CreateSession();
        if (session is null)
        {
            StatusMessage = "Assistant configuration missing.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Requesting configuration summary…";

        ChatMessage? assistantMessage = null;

        try
        {
            var completionPrompt = BuildCompletionPrompt();
            var completionMessage = new ChatMessage(ChatMessageRole.User, completionPrompt);
            _history.Add(completionMessage);
            Conversation.Add(new ChatMessage(ChatMessageRole.User, "(Requested configuration summary.)"));

            var history = _history.ToList();
            assistantMessage = new ChatMessage(ChatMessageRole.Assistant, string.Empty);
            _history.Add(assistantMessage);
            Conversation.Add(assistantMessage);

            var context = BuildContextSummary();
            await foreach (var token in _chatAssistantService.StreamChatCompletionAsync(session, history, context, null, _cts.Token).ConfigureAwait(false))
            {
                assistantMessage.Append(token);
            }

            var response = assistantMessage.Content?.Trim() ?? string.Empty;
            if (!TryParseSettings(response, out var settings, out var summary))
            {
                StatusMessage = "Unable to parse assistant response. Adjust answers or try again.";
                return;
            }

            Result = settings.EnsureValid() with { Summary = summary };
            StatusMessage = "Configuration captured. Close to apply settings.";
            Completed?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Wizard cancelled.";
            if (assistantMessage is not null)
            {
                assistantMessage.Content = "(Cancelled)";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Assistant summary failed: {ex.Message}";
            if (assistantMessage is not null)
            {
                assistantMessage.Content = $"Assistant error: {ex.Message}";
            }
            else
            {
                Conversation.Add(new ChatMessage(ChatMessageRole.Assistant, $"Assistant error: {ex.Message}"));
            }
        }
        finally
        {
            IsBusy = false;
            SubmitAnswerCommand.RaiseCanExecuteChanged();
            CompleteCommand.RaiseCanExecuteChanged();
        }
    }

    private ChatSessionSettings? CreateSession()
    {
        var endpoint = _mainViewModel.ChatApiEndpoint;
        var apiKey = _mainViewModel.ChatApiKey;
        var model = _mainViewModel.ChatModel;
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        return new ChatSessionSettings(
            endpoint,
            apiKey,
            model,
            _mainViewModel.ChatSystemPrompt,
            UsageReported: _mainViewModel.OnChatUsageReported);
    }

    private string BuildSystemPrompt()
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are the WC Local Scraper onboarding guide. Ask the operator one question at a time to collect export preferences, credential placeholders, and HTTP retry configuration.");
        builder.AppendLine("Use prior answers to avoid repeating yourself. Keep questions short and focused.");
        builder.AppendLine("When you are asked for a configuration summary you must return JSON following the provided schema using placeholder text for any credentials that still require operator input.");
        return builder.ToString();
    }

    private string BuildContextSummary()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Configuration options available in WC Local Scraper:");
        builder.AppendLine("- exportCsv: generic product CSV export (default: " + _mainViewModel.ExportCsv + ")");
        builder.AppendLine("- exportShopify: Shopify CSV export (default: " + _mainViewModel.ExportShopify + ")");
        builder.AppendLine("- exportWoo: WooCommerce CSV export (default: " + _mainViewModel.ExportWoo + ")");
        builder.AppendLine("- exportReviews: Reviews CSV export (default: " + _mainViewModel.ExportReviews + ")");
        builder.AppendLine("- exportXlsx: XLSX export (default: " + _mainViewModel.ExportXlsx + ")");
        builder.AppendLine("- exportJsonl: JSONL export (default: " + _mainViewModel.ExportJsonl + ")");
        builder.AppendLine("- exportPluginsCsv / exportPluginsJsonl / exportThemesCsv / exportThemesJsonl: WooCommerce plugin and theme inventory exports.");
        builder.AppendLine("- exportPublicExtensionFootprints: lightweight plugin/theme slug crawl.");
        builder.AppendLine("- exportPublicDesignSnapshot / exportPublicDesignScreenshots: homepage capture options.");
        builder.AppendLine("- exportStoreConfiguration / importStoreConfiguration: transfer WooCommerce settings between stores.");
        builder.AppendLine("- enableHttpRetries / httpRetryAttempts / httpRetryBaseDelaySeconds / httpRetryMaxDelaySeconds: network retry strategy.");
        builder.AppendLine();
        builder.AppendLine("Current operator answers:");
        if (_answers.Count == 0)
        {
            builder.AppendLine("- (none yet)");
        }
        else
        {
            foreach (var answer in _answers)
            {
                builder.AppendLine("- Q: " + answer.Question.Replace('\n', ' '));
                builder.AppendLine("  A: " + answer.Answer.Replace('\n', ' '));
            }
        }

        builder.AppendLine();
        builder.AppendLine("Existing application defaults:");
        builder.AppendLine("- HTTP retries enabled: " + _mainViewModel.EnableHttpRetries);
        builder.AppendLine("- HTTP retry attempts: " + _mainViewModel.HttpRetryAttempts);
        builder.AppendLine("- HTTP retry base delay (s): " + _mainViewModel.HttpRetryBaseDelaySeconds.ToString("0.###"));
        builder.AppendLine("- HTTP retry max delay (s): " + _mainViewModel.HttpRetryMaxDelaySeconds.ToString("0.###"));
        builder.AppendLine("- WordPress username placeholder: " + (_mainViewModel.WordPressUsername ?? string.Empty));
        builder.AppendLine("- Shopify store URL placeholder: " + (_mainViewModel.ShopifyStoreUrl ?? string.Empty));
        builder.AppendLine();
        builder.AppendLine("Use this information when crafting your next question.");
        return builder.ToString();
    }

    private string BuildCompletionPrompt()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Summarize the configuration we agreed on as JSON with this schema:");
        builder.AppendLine("{");
        builder.AppendLine("  \"exports\": {");
        builder.AppendLine("    \"csv\": bool, \"shopify\": bool, \"woo\": bool, \"reviews\": bool, \"xlsx\": bool, \"jsonl\": bool,");
        builder.AppendLine("    \"pluginsCsv\": bool, \"pluginsJsonl\": bool, \"themesCsv\": bool, \"themesJsonl\": bool,");
        builder.AppendLine("    \"publicExtensionFootprints\": bool, \"publicDesignSnapshot\": bool, \"publicDesignScreenshots\": bool,");
        builder.AppendLine("    \"storeConfiguration\": bool, \"importStoreConfiguration\": bool");
        builder.AppendLine("  },");
        builder.AppendLine("  \"credentials\": {");
        builder.AppendLine("    \"wordpressUsername\": string or null,");
        builder.AppendLine("    \"wordpressApplicationPassword\": string or null,");
        builder.AppendLine("    \"shopifyStoreUrl\": string or null,");
        builder.AppendLine("    \"shopifyAdminAccessToken\": string or null,");
        builder.AppendLine("    \"shopifyStorefrontAccessToken\": string or null,");
        builder.AppendLine("    \"shopifyApiKey\": string or null,");
        builder.AppendLine("    \"shopifyApiSecret\": string or null");
        builder.AppendLine("  },");
        builder.AppendLine("  \"retry\": {");
        builder.AppendLine("    \"enable\": bool, \"attempts\": number, \"baseDelaySeconds\": number, \"maxDelaySeconds\": number");
        builder.AppendLine("  },");
        builder.AppendLine("  \"summary\": string");
        builder.AppendLine("}");
        builder.AppendLine("Use placeholder text instead of actual secrets when credentials are still pending.");
        builder.AppendLine("Respond with JSON only (no markdown).");
        return builder.ToString();
    }

    private bool TryParseSettings(string response, out OnboardingWizardSettings settings, out string? summary)
    {
        settings = default!;
        summary = null;

        var json = ExtractJson(response);
        if (json is null)
        {
            return false;
        }

        try
        {
            var contract = JsonSerializer.Deserialize<WizardSettingsContract>(json, _jsonOptions);
            if (contract is null)
            {
                return false;
            }

            settings = CreateSettings(contract);
            summary = contract.Summary ?? BuildFallbackSummary(settings);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private string BuildFallbackSummary(OnboardingWizardSettings settings)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Exports:");
        builder.AppendLine($"- CSV: {settings.ExportCsv}");
        builder.AppendLine($"- Shopify: {settings.ExportShopify}");
        builder.AppendLine($"- WooCommerce: {settings.ExportWoo}");
        builder.AppendLine($"- Reviews: {settings.ExportReviews}");
        builder.AppendLine($"- XLSX: {settings.ExportXlsx}");
        builder.AppendLine($"- JSONL: {settings.ExportJsonl}");
        builder.AppendLine($"- Plugins CSV: {settings.ExportPluginsCsv}");
        builder.AppendLine($"- Plugins JSONL: {settings.ExportPluginsJsonl}");
        builder.AppendLine($"- Themes CSV: {settings.ExportThemesCsv}");
        builder.AppendLine($"- Themes JSONL: {settings.ExportThemesJsonl}");
        builder.AppendLine($"- Public extension footprints: {settings.ExportPublicExtensionFootprints}");
        builder.AppendLine($"- Public design snapshot: {settings.ExportPublicDesignSnapshot}");
        builder.AppendLine($"- Public design screenshots: {settings.ExportPublicDesignScreenshots}");
        builder.AppendLine($"- Store configuration export: {settings.ExportStoreConfiguration}");
        builder.AppendLine($"- Store configuration import: {settings.ImportStoreConfiguration}");
        builder.AppendLine("Retry:");
        builder.AppendLine($"- Enabled: {settings.EnableHttpRetries}");
        builder.AppendLine($"- Attempts: {settings.HttpRetryAttempts}");
        builder.AppendLine($"- Base delay: {settings.HttpRetryBaseDelaySeconds:0.###} s");
        builder.AppendLine($"- Max delay: {settings.HttpRetryMaxDelaySeconds:0.###} s");
        return builder.ToString();
    }

    private OnboardingWizardSettings CreateSettings(WizardSettingsContract contract)
    {
        var exports = contract.Exports ?? new WizardExports();
        var credentials = contract.Credentials ?? new WizardCredentials();
        var retry = contract.Retry ?? new WizardRetry();

        return new OnboardingWizardSettings(
            exports.Csv ?? _mainViewModel.ExportCsv,
            exports.Shopify ?? _mainViewModel.ExportShopify,
            exports.Woo ?? _mainViewModel.ExportWoo,
            exports.Reviews ?? _mainViewModel.ExportReviews,
            exports.Xlsx ?? _mainViewModel.ExportXlsx,
            exports.Jsonl ?? _mainViewModel.ExportJsonl,
            exports.PluginsCsv ?? _mainViewModel.ExportPluginsCsv,
            exports.PluginsJsonl ?? _mainViewModel.ExportPluginsJsonl,
            exports.ThemesCsv ?? _mainViewModel.ExportThemesCsv,
            exports.ThemesJsonl ?? _mainViewModel.ExportThemesJsonl,
            exports.PublicExtensionFootprints ?? _mainViewModel.ExportPublicExtensionFootprints,
            exports.PublicDesignSnapshot ?? _mainViewModel.ExportPublicDesignSnapshot,
            exports.PublicDesignScreenshots ?? _mainViewModel.ExportPublicDesignScreenshots,
            exports.StoreConfiguration ?? _mainViewModel.ExportStoreConfiguration,
            exports.ImportStoreConfiguration ?? _mainViewModel.ImportStoreConfiguration,
            retry.Enable ?? _mainViewModel.EnableHttpRetries,
            retry.Attempts ?? _mainViewModel.HttpRetryAttempts,
            retry.BaseDelaySeconds ?? _mainViewModel.HttpRetryBaseDelaySeconds,
            retry.MaxDelaySeconds ?? _mainViewModel.HttpRetryMaxDelaySeconds,
            NormalizePlaceholder(credentials.WordPressUsername),
            NormalizePlaceholder(credentials.WordPressApplicationPassword),
            NormalizePlaceholder(credentials.ShopifyStoreUrl),
            NormalizePlaceholder(credentials.ShopifyAdminAccessToken),
            NormalizePlaceholder(credentials.ShopifyStorefrontAccessToken),
            NormalizePlaceholder(credentials.ShopifyApiKey),
            NormalizePlaceholder(credentials.ShopifyApiSecret),
            contract.Summary);
    }

    private static string? NormalizePlaceholder(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string? ExtractJson(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        var trimmed = response.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBrace = trimmed.IndexOf('{');
            var lastBrace = trimmed.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace >= firstBrace)
            {
                return trimmed.Substring(firstBrace, lastBrace - firstBrace + 1);
            }

            return null;
        }

        if (trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end >= start)
        {
            return trimmed.Substring(start, end - start + 1);
        }

        return null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
