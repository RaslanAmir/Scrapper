using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WcScraper.Core;
using WcScraper.Wpf.Models;
using WcScraper.Wpf.Reporting;
using WcScraper.Wpf.Services;

namespace WcScraper.Wpf.ViewModels;

public sealed class ChatAssistantViewModel : INotifyPropertyChanged
{
    private const string ApplyAssistantDirectivesCommand = "/apply-directives";
    private const string DiscardAssistantDirectivesCommand = "/discard-directives";

    private static readonly HashSet<string> s_riskyAssistantOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(MainViewModel.ExportPublicExtensionFootprints),
        nameof(MainViewModel.ExportPublicDesignSnapshot),
        nameof(MainViewModel.ExportPublicDesignScreenshots),
        nameof(MainViewModel.ExportStoreConfiguration),
        nameof(MainViewModel.ImportStoreConfiguration),
    };

    private readonly ChatAssistantService _chatAssistantService;
    private readonly ChatTranscriptStore _chatTranscriptStore;
    private readonly IArtifactIndexingService _artifactIndexingService;
    private readonly IDialogService _dialogs;
    private readonly Action<string> _log;
    private readonly IReadOnlyDictionary<string, (Func<bool> Getter, Action<bool> Setter)> _assistantToggleBindings;
    private readonly Func<HostSnapshot> _hostSnapshotProvider;
    private readonly Func<string> _resolveBaseOutputFolder;
    private readonly Func<string> _getOutputFolder;
    private readonly Func<IReadOnlyList<string>> _getLogsSnapshot;
    private readonly Func<bool> _canExecuteRunCommand;
    private readonly Action _executeRunCommand;
    private readonly Func<bool> _isRunInProgress;
    private readonly Action<RunPlan> _enqueueRunPlan;
    private readonly Func<string?> _getLatestStoreOutputFolder;
    private readonly Func<string?> _getLatestManualBundlePath;
    private readonly Func<string?> _getLatestManualReportPath;
    private readonly Func<string?> _getLatestRunDeltaPath;
    private readonly Func<string?> _getLatestRunAiBriefPath;
    private readonly Func<string?> _getLatestRunSnapshotJson;
    private readonly Action<Action> _invokeOnUiThread;
    private readonly Func<bool> _getEnableHttpRetries;
    private readonly Action<bool> _setEnableHttpRetries;
    private readonly Func<int> _getHttpRetryAttempts;
    private readonly Action<int> _setHttpRetryAttempts;
    private readonly Func<double> _getHttpRetryBaseDelaySeconds;
    private readonly Action<double> _setHttpRetryBaseDelaySeconds;
    private readonly Func<double> _getHttpRetryMaxDelaySeconds;
    private readonly Action<double> _setHttpRetryMaxDelaySeconds;
    private readonly string _chatKeyPath;

    private readonly IReadOnlyList<ChatModeOption> _chatModeOptions = new[]
    {
        new ChatModeOption(ChatInteractionMode.GeneralAssistant, "General assistant"),
        new ChatModeOption(ChatInteractionMode.DatasetQuestion, "Ask about exported data"),
    };

    private ChatInteractionMode _selectedChatMode = ChatInteractionMode.GeneralAssistant;
    private int _chatTranscriptLoadState;

    private string _chatApiEndpoint = string.Empty;
    private string _chatModel = string.Empty;
    private string _chatSystemPrompt = "You are a helpful assistant for WC Local Scraper exports.";
    private string _chatApiKey = string.Empty;
    private bool _hasChatApiKey;
    private bool _isChatBusy;
    private string _chatInput = string.Empty;
    private string _chatStatusMessage = "Enter API endpoint, model, and API key to enable the assistant.";
    private int _chatPromptTokenTotal;
    private int _chatCompletionTokenTotal;
    private int _chatTotalTokenTotal;
    private long _totalPromptTokens;
    private long _totalCompletionTokens;
    private decimal _totalCostUsd;
    private int? _chatMaxPromptTokens;
    private int? _chatMaxTotalTokens;
    private decimal? _chatMaxCostUsd;
    private decimal? _chatPromptTokenUsdPerThousand;
    private decimal? _chatCompletionTokenUsdPerThousand;
    private bool _isAssistantPanelExpanded;
    private AssistantDirectiveBatch? _pendingAssistantDirectives;
    private CancellationTokenSource? _chatCts;

    public ChatAssistantViewModel(
        ChatAssistantService chatAssistantService,
        ChatTranscriptStore chatTranscriptStore,
        IArtifactIndexingService artifactIndexingService,
        IDialogService dialogs,
        Action<string> log,
        IReadOnlyDictionary<string, (Func<bool> Getter, Action<bool> Setter)> assistantToggleBindings,
        Func<HostSnapshot> hostSnapshotProvider,
        Func<string> resolveBaseOutputFolder,
        Func<string> getOutputFolder,
        Func<IReadOnlyList<string>> getLogsSnapshot,
        Func<bool> canExecuteRunCommand,
        Action executeRunCommand,
        Func<bool> isRunInProgress,
        Action<RunPlan> enqueueRunPlan,
        Func<string?> getLatestStoreOutputFolder,
        Func<string?> getLatestManualBundlePath,
        Func<string?> getLatestManualReportPath,
        Func<string?> getLatestRunDeltaPath,
        Func<string?> getLatestRunAiBriefPath,
        Func<string?> getLatestRunSnapshotJson,
        Action<Action> invokeOnUiThread,
        Func<bool> getEnableHttpRetries,
        Action<bool> setEnableHttpRetries,
        Func<int> getHttpRetryAttempts,
        Action<int> setHttpRetryAttempts,
        Func<double> getHttpRetryBaseDelaySeconds,
        Action<double> setHttpRetryBaseDelaySeconds,
        Func<double> getHttpRetryMaxDelaySeconds,
        Action<double> setHttpRetryMaxDelaySeconds,
        string chatKeyPath)
    {
        _chatAssistantService = chatAssistantService ?? throw new ArgumentNullException(nameof(chatAssistantService));
        _chatTranscriptStore = chatTranscriptStore ?? throw new ArgumentNullException(nameof(chatTranscriptStore));
        _artifactIndexingService = artifactIndexingService ?? throw new ArgumentNullException(nameof(artifactIndexingService));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _assistantToggleBindings = assistantToggleBindings ?? throw new ArgumentNullException(nameof(assistantToggleBindings));
        _hostSnapshotProvider = hostSnapshotProvider ?? throw new ArgumentNullException(nameof(hostSnapshotProvider));
        _resolveBaseOutputFolder = resolveBaseOutputFolder ?? throw new ArgumentNullException(nameof(resolveBaseOutputFolder));
        _getOutputFolder = getOutputFolder ?? throw new ArgumentNullException(nameof(getOutputFolder));
        _getLogsSnapshot = getLogsSnapshot ?? throw new ArgumentNullException(nameof(getLogsSnapshot));
        _canExecuteRunCommand = canExecuteRunCommand ?? throw new ArgumentNullException(nameof(canExecuteRunCommand));
        _executeRunCommand = executeRunCommand ?? throw new ArgumentNullException(nameof(executeRunCommand));
        _isRunInProgress = isRunInProgress ?? throw new ArgumentNullException(nameof(isRunInProgress));
        _enqueueRunPlan = enqueueRunPlan ?? throw new ArgumentNullException(nameof(enqueueRunPlan));
        _getLatestStoreOutputFolder = getLatestStoreOutputFolder ?? throw new ArgumentNullException(nameof(getLatestStoreOutputFolder));
        _getLatestManualBundlePath = getLatestManualBundlePath ?? throw new ArgumentNullException(nameof(getLatestManualBundlePath));
        _getLatestManualReportPath = getLatestManualReportPath ?? throw new ArgumentNullException(nameof(getLatestManualReportPath));
        _getLatestRunDeltaPath = getLatestRunDeltaPath ?? throw new ArgumentNullException(nameof(getLatestRunDeltaPath));
        _getLatestRunAiBriefPath = getLatestRunAiBriefPath ?? throw new ArgumentNullException(nameof(getLatestRunAiBriefPath));
        _getLatestRunSnapshotJson = getLatestRunSnapshotJson ?? throw new ArgumentNullException(nameof(getLatestRunSnapshotJson));
        _invokeOnUiThread = invokeOnUiThread ?? throw new ArgumentNullException(nameof(invokeOnUiThread));
        _getEnableHttpRetries = getEnableHttpRetries ?? throw new ArgumentNullException(nameof(getEnableHttpRetries));
        _setEnableHttpRetries = setEnableHttpRetries ?? throw new ArgumentNullException(nameof(setEnableHttpRetries));
        _getHttpRetryAttempts = getHttpRetryAttempts ?? throw new ArgumentNullException(nameof(getHttpRetryAttempts));
        _setHttpRetryAttempts = setHttpRetryAttempts ?? throw new ArgumentNullException(nameof(setHttpRetryAttempts));
        _getHttpRetryBaseDelaySeconds = getHttpRetryBaseDelaySeconds ?? throw new ArgumentNullException(nameof(getHttpRetryBaseDelaySeconds));
        _setHttpRetryBaseDelaySeconds = setHttpRetryBaseDelaySeconds ?? throw new ArgumentNullException(nameof(setHttpRetryBaseDelaySeconds));
        _getHttpRetryMaxDelaySeconds = getHttpRetryMaxDelaySeconds ?? throw new ArgumentNullException(nameof(getHttpRetryMaxDelaySeconds));
        _setHttpRetryMaxDelaySeconds = setHttpRetryMaxDelaySeconds ?? throw new ArgumentNullException(nameof(setHttpRetryMaxDelaySeconds));
        _chatKeyPath = chatKeyPath ?? throw new ArgumentNullException(nameof(chatKeyPath));

        ChatMessages = new ObservableCollection<ChatMessage>();
        ChatMessages.CollectionChanged += OnChatMessagesCollectionChanged;

        SendChatCommand = new RelayCommand(async () => await OnSendChatAsync(), CanSendChat);
        CancelChatCommand = new RelayCommand(OnCancelChat, CanCancelChat);
        SaveChatTranscriptCommand = new RelayCommand(async () => await OnSaveChatTranscriptAsync(), CanSaveChatTranscript);
        ClearChatHistoryCommand = new RelayCommand(async () => await OnClearChatHistoryAsync(), CanClearChatHistory);
        UseAiRecommendationCommand = new RelayCommand<string?>(OnUseAiRecommendation);

        LoadChatApiKey();
        UpdateChatConfigurationStatus();
        _ = EnsureChatTranscriptLoadedAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ChatMessage> ChatMessages { get; }

    public bool HasChatMessages => ChatMessages.Count > 0;

    public int ChatPromptTokenTotal
    {
        get => _chatPromptTokenTotal;
        private set
        {
            if (_chatPromptTokenTotal == value)
            {
                return;
            }

            _chatPromptTokenTotal = value;
            OnPropertyChanged();
        }
    }

    public int ChatCompletionTokenTotal
    {
        get => _chatCompletionTokenTotal;
        private set
        {
            if (_chatCompletionTokenTotal == value)
            {
                return;
            }

            _chatCompletionTokenTotal = value;
            OnPropertyChanged();
        }
    }

    public int ChatTotalTokenTotal
    {
        get => _chatTotalTokenTotal;
        private set
        {
            if (_chatTotalTokenTotal == value)
            {
                return;
            }

            _chatTotalTokenTotal = value;
            OnPropertyChanged();
        }
    }

    public long TotalPromptTokens
    {
        get => _totalPromptTokens;
        private set
        {
            if (_totalPromptTokens == value)
            {
                return;
            }

            _totalPromptTokens = value;
            OnPropertyChanged();
        }
    }

    public long TotalCompletionTokens
    {
        get => _totalCompletionTokens;
        private set
        {
            if (_totalCompletionTokens == value)
            {
                return;
            }

            _totalCompletionTokens = value;
            OnPropertyChanged();
        }
    }

    public decimal TotalCostUsd
    {
        get => _totalCostUsd;
        private set
        {
            if (_totalCostUsd == value)
            {
                return;
            }

            _totalCostUsd = value;
            OnPropertyChanged();
        }
    }

    public RelayCommand SendChatCommand { get; }

    public RelayCommand CancelChatCommand { get; }

    public RelayCommand SaveChatTranscriptCommand { get; }

    public RelayCommand ClearChatHistoryCommand { get; }

    public RelayCommand<string?> UseAiRecommendationCommand { get; }

    public string CurrentTranscriptPath => _chatTranscriptStore.CurrentJsonlPath;

    public string ChatApiEndpoint
    {
        get => _chatApiEndpoint;
        set
        {
            var newValue = value ?? string.Empty;
            if (_chatApiEndpoint == newValue)
            {
                return;
            }

            _chatApiEndpoint = newValue;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasChatConfiguration));
            SendChatCommand.RaiseCanExecuteChanged();
            UpdateChatConfigurationStatus();
        }
    }

    public string ChatModel
    {
        get => _chatModel;
        set
        {
            var newValue = value ?? string.Empty;
            if (_chatModel == newValue)
            {
                return;
            }

            _chatModel = newValue;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasChatConfiguration));
            SendChatCommand.RaiseCanExecuteChanged();
            UpdateChatConfigurationStatus();
        }
    }

    public string ChatSystemPrompt
    {
        get => _chatSystemPrompt;
        set
        {
            var newValue = string.IsNullOrWhiteSpace(value) ? _chatSystemPrompt : value;
            if (_chatSystemPrompt == newValue)
            {
                return;
            }

            _chatSystemPrompt = newValue;
            OnPropertyChanged();
        }
    }

    public string ChatApiKey
    {
        get => _chatApiKey;
        set
        {
            var newValue = value ?? string.Empty;
            if (_chatApiKey == newValue)
            {
                return;
            }

            _chatApiKey = newValue;
            OnPropertyChanged();

            if (!TryPersistChatApiKey(newValue) && !string.IsNullOrWhiteSpace(newValue))
            {
                ChatStatusMessage = "Unable to persist API key securely. The key will be kept for this session only.";
            }

            HasChatApiKey = !string.IsNullOrWhiteSpace(newValue);
        }
    }

    public bool HasChatApiKey
    {
        get => _hasChatApiKey;
        private set
        {
            if (_hasChatApiKey == value)
            {
                return;
            }

            _hasChatApiKey = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasChatConfiguration));
            SendChatCommand.RaiseCanExecuteChanged();
            UpdateChatConfigurationStatus();
        }
    }

    public bool HasChatConfiguration => HasChatApiKey
        && !string.IsNullOrWhiteSpace(ChatApiEndpoint)
        && !string.IsNullOrWhiteSpace(ChatModel);

    public IReadOnlyList<ChatModeOption> ChatModeOptions => _chatModeOptions;

    public ChatInteractionMode SelectedChatMode
    {
        get => _selectedChatMode;
        set
        {
            if (_selectedChatMode == value)
            {
                return;
            }

            _selectedChatMode = value;
            OnPropertyChanged();
            ResetChatUsageTotals();
            UpdateChatConfigurationStatus();
        }
    }

    public bool IsChatBusy
    {
        get => _isChatBusy;
        private set
        {
            if (_isChatBusy == value)
            {
                return;
            }

            _isChatBusy = value;
            OnPropertyChanged();
            SendChatCommand.RaiseCanExecuteChanged();
            CancelChatCommand.RaiseCanExecuteChanged();
        }
    }

    public string ChatInput
    {
        get => _chatInput;
        set
        {
            var newValue = value ?? string.Empty;
            if (_chatInput == newValue)
            {
                return;
            }

            _chatInput = newValue;
            OnPropertyChanged();
            SendChatCommand.RaiseCanExecuteChanged();
        }
    }

    public string ChatStatusMessage
    {
        get => _chatStatusMessage;
        private set
        {
            if (_chatStatusMessage == value)
            {
                return;
            }

            _chatStatusMessage = value;
            OnPropertyChanged();
        }
    }

    public void SetStatusMessage(string message)
        => ChatStatusMessage = message ?? string.Empty;

    public int? ChatMaxPromptTokens
    {
        get => _chatMaxPromptTokens;
        set
        {
            if (_chatMaxPromptTokens == value)
            {
                return;
            }

            _chatMaxPromptTokens = value;
            OnPropertyChanged();
            UpdateChatConfigurationStatus();
        }
    }

    public int? ChatMaxTotalTokens
    {
        get => _chatMaxTotalTokens;
        set
        {
            if (_chatMaxTotalTokens == value)
            {
                return;
            }

            _chatMaxTotalTokens = value;
            OnPropertyChanged();
            UpdateChatConfigurationStatus();
        }
    }

    public decimal? ChatMaxCostUsd
    {
        get => _chatMaxCostUsd;
        set
        {
            if (_chatMaxCostUsd == value)
            {
                return;
            }

            _chatMaxCostUsd = value;
            OnPropertyChanged();
            UpdateChatConfigurationStatus();
        }
    }

    public decimal? ChatPromptTokenUsdPerThousand
    {
        get => _chatPromptTokenUsdPerThousand;
        set
        {
            if (_chatPromptTokenUsdPerThousand == value)
            {
                return;
            }

            _chatPromptTokenUsdPerThousand = value;
            OnPropertyChanged();
        }
    }

    public decimal? ChatCompletionTokenUsdPerThousand
    {
        get => _chatCompletionTokenUsdPerThousand;
        set
        {
            if (_chatCompletionTokenUsdPerThousand == value)
            {
                return;
            }

            _chatCompletionTokenUsdPerThousand = value;
            OnPropertyChanged();
        }
    }

    public bool IsAssistantPanelExpanded
    {
        get => _isAssistantPanelExpanded;
        set
        {
            if (_isAssistantPanelExpanded == value)
            {
                return;
            }

            _isAssistantPanelExpanded = value;
            OnPropertyChanged();
            if (value)
            {
                _ = EnsureChatTranscriptLoadedAsync();
            }
        }
    }

    public bool CanSendChat()
        => !IsChatBusy
            && HasChatConfiguration
            && !string.IsNullOrWhiteSpace(ChatInput);

    public bool CanCancelChat()
        => IsChatBusy;

    public bool CanSaveChatTranscript()
        => HasChatMessages;

    public bool CanClearChatHistory()
        => HasChatMessages;

    public void OnChatUsageReported(ChatUsageSnapshot usage)
    {
        if (usage is null)
        {
            return;
        }

        _invokeOnUiThread(() =>
        {
            var incrementalCost = CalculateUsageCost(usage);

            ChatPromptTokenTotal = AddWithoutOverflow(ChatPromptTokenTotal, usage.PromptTokens);
            ChatCompletionTokenTotal = AddWithoutOverflow(ChatCompletionTokenTotal, usage.CompletionTokens);
            ChatTotalTokenTotal = AddWithoutOverflow(ChatTotalTokenTotal, usage.TotalTokens);

            TotalPromptTokens = AddWithoutOverflow(TotalPromptTokens, usage.PromptTokens);
            TotalCompletionTokens = AddWithoutOverflow(TotalCompletionTokens, usage.CompletionTokens);
            TotalCostUsd = TotalCostUsd + incrementalCost;
        });
    }

    public Task EnsureChatTranscriptLoadedAsync()
    {
        if (Interlocked.CompareExchange(ref _chatTranscriptLoadState, 1, 0) != 0)
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            try
            {
                var session = await _chatTranscriptStore.LoadMostRecentTranscriptAsync().ConfigureAwait(false);
                if (session is null || session.Messages.Count == 0)
                {
                    return;
                }

                _invokeOnUiThread(() =>
                {
                    if (ChatMessages.Count > 0)
                    {
                        return;
                    }

                    ChatMessages.Clear();
                    ResetChatUsageTotals();
                    foreach (var message in session.Messages)
                    {
                        ChatMessages.Add(message);
                    }

                    var resumedAt = session.CreatedAtUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                    ChatStatusMessage = AppendBudgetReminder($"Resumed chat transcript from {resumedAt} UTC.");
                });
            }
            catch (Exception ex)
            {
                _log($"Unable to load chat transcript: {ex.Message}");
            }
        });
    }

    public void PrepareForRunFollowUp(string snapshotJson, string? narrative, string? goals, string? aiBriefPath)
    {
        _invokeOnUiThread(() =>
        {
            IsAssistantPanelExpanded = true;
            ChatMessages.Clear();
            ResetChatUsageTotals();
            _chatTranscriptStore.StartNewSession();

            var contextBuilder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(goals))
            {
                contextBuilder.AppendLine("Operator goals:");
                contextBuilder.AppendLine(goals!.Trim());
            }

            if (!string.IsNullOrWhiteSpace(snapshotJson))
            {
                if (contextBuilder.Length > 0)
                {
                    contextBuilder.AppendLine();
                }

                contextBuilder.AppendLine("Run snapshot (JSON):");
                contextBuilder.AppendLine(snapshotJson);
            }

            var contextText = contextBuilder.ToString().Trim();
            if (contextText.Length > 0)
            {
                var systemMessage = new ChatMessage(ChatMessageRole.System, contextText);
                ChatMessages.Add(systemMessage);
                _ = TryAppendTranscriptAsync(systemMessage);
            }

            if (!string.IsNullOrWhiteSpace(narrative))
            {
                var messageBuilder = new StringBuilder(narrative.Trim());
                if (!string.IsNullOrWhiteSpace(aiBriefPath))
                {
                    messageBuilder.AppendLine();
                    messageBuilder.AppendLine();
                    messageBuilder.Append("AI brief saved at: ");
                    messageBuilder.Append(aiBriefPath);
                }

                var assistantSummary = new ChatMessage(ChatMessageRole.Assistant, messageBuilder.ToString());
                ChatMessages.Add(assistantSummary);
                _ = TryAppendTranscriptAsync(assistantSummary);
            }

            ChatInput = string.Empty;
            ChatStatusMessage = AppendBudgetReminder("Assistant loaded with the latest run summary. Ask a follow-up question.");
        });
    }
    private async Task OnSendChatAsync()
    {
        var trimmed = ChatInput?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            ChatStatusMessage = "Enter a prompt before sending.";
            return;
        }

        if (TryHandleAssistantDirectiveCommand(trimmed))
        {
            return;
        }

        if (!HasChatConfiguration)
        {
            UpdateChatConfigurationStatus();
            return;
        }

        var userMessage = new ChatMessage(ChatMessageRole.User, trimmed);
        ChatMessages.Add(userMessage);
        ChatInput = string.Empty;
        await TryAppendTranscriptAsync(userMessage);

        var history = ChatMessages.ToList();
        var assistantMessage = new ChatMessage(ChatMessageRole.Assistant, string.Empty);
        ChatMessages.Add(assistantMessage);

        var chatCts = new CancellationTokenSource();
        _chatCts = chatCts;
        var cancellationToken = chatCts.Token;

        IsChatBusy = true;
        ChatStatusMessage = "Requesting assistant response…";

        var wasCancelled = false;

        ChatSessionSettings? sessionState = null;

        try
        {
            var context = BuildChatContext();
            var session = new ChatSessionSettings(
                ChatApiEndpoint,
                ChatApiKey,
                ChatModel,
                ChatSystemPrompt,
                MaxPromptTokens: ChatMaxPromptTokens,
                MaxTotalTokens: ChatMaxTotalTokens,
                MaxCostUsd: ChatMaxCostUsd,
                PromptTokenCostPerThousandUsd: ChatPromptTokenUsdPerThousand,
                CompletionTokenCostPerThousandUsd: ChatCompletionTokenUsdPerThousand,
                DiagnosticLogger: _log,
                UsageReported: OnChatUsageReported);
            sessionState = session;

            if (SelectedChatMode == ChatInteractionMode.DatasetQuestion)
            {
                await foreach (var token in _chatAssistantService.StreamDatasetAnswerAsync(session, history, trimmed, cancellationToken))
                {
                    assistantMessage.Append(token);
                }
            }
            else
            {
                var contextSummary = _chatAssistantService.BuildContextualPrompt(context);

                var toolbox = new ChatAssistantToolbox(
                    _getLatestRunSnapshotJson,
                    limit =>
                    {
                        try
                        {
                            return CollectRecentExportFiles(limit);
                        }
                        catch (Exception ex)
                        {
                            _log($"Assistant export file lookup failed: {ex.Message}");
                            throw;
                        }
                    },
                    limit =>
                    {
                        try
                        {
                            return CollectRecentLogs(limit);
                        }
                        catch (Exception ex)
                        {
                            _log($"Assistant log retrieval failed: {ex.Message}");
                            throw;
                        }
                    });

                await foreach (var token in _chatAssistantService.StreamChatCompletionAsync(session, history, contextSummary, toolbox, cancellationToken))
                {
                    assistantMessage.Append(token);
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                wasCancelled = true;
            }

            if (string.IsNullOrWhiteSpace(assistantMessage.Content))
            {
                assistantMessage.Content = cancellationToken.IsCancellationRequested
                    ? "(Response cancelled.)"
                    : "(No response received.)";
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                ChatStatusMessage = AppendBudgetReminder("Assistant ready for another prompt.");
            }
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
            assistantMessage.Content = "(Response cancelled.)";
            ChatStatusMessage = "Assistant response cancelled.";
        }
        catch (Exception ex)
        {
            assistantMessage.Content = $"(Error: {ex.Message})";
            ChatStatusMessage = AppendBudgetReminder("Assistant encountered an error. Try again when ready.");
            _log($"Assistant request failed: {ex.Message}");
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _chatCts, null, chatCts) == chatCts)
            {
                _chatCts = null;
            }
            chatCts.Dispose();
            IsChatBusy = false;
            if (wasCancelled)
            {
                ChatStatusMessage = "Assistant response cancelled.";
            }

            UpdateChatUsageTotals(sessionState);
        }

        await TryAppendTranscriptAsync(assistantMessage);
    }

    private void OnUseAiRecommendation(string? prompt)
    {
        var trimmed = prompt?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return;
        }

        _invokeOnUiThread(() =>
        {
            ChatInput = trimmed;
            IsAssistantPanelExpanded = true;
        });
    }

    private void OnCancelChat()
    {
        var cts = Interlocked.Exchange(ref _chatCts, null);
        if (cts is null || !IsChatBusy)
        {
            return;
        }

        try
        {
            ChatStatusMessage = "Cancelling assistant response…";
            cts.Cancel();
        }
        catch (Exception ex)
        {
            _log($"Unable to cancel assistant response: {ex.Message}");
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async Task OnSaveChatTranscriptAsync()
    {
        if (!HasChatMessages)
        {
            return;
        }

        try
        {
            var filter = new FileDialogFilter("Chat transcript", "*.md", "*.jsonl");
            var defaultFileName = Path.GetFileName(_chatTranscriptStore.CurrentMarkdownPath);
            var target = _dialogs.SaveFile(filter, defaultFileName, _chatTranscriptStore.TranscriptDirectory);
            if (string.IsNullOrWhiteSpace(target))
            {
                return;
            }

            var format = string.Equals(Path.GetExtension(target), ".jsonl", StringComparison.OrdinalIgnoreCase)
                ? ChatTranscriptFormat.Jsonl
                : ChatTranscriptFormat.Markdown;

            await _chatTranscriptStore.SaveTranscriptAsync(target, format).ConfigureAwait(false);
            _log($"Saved chat transcript to {target}");
        }
        catch (Exception ex)
        {
            _log($"Unable to save chat transcript: {ex.Message}");
        }
    }

    private Task OnClearChatHistoryAsync()
    {
        try
        {
            ChatMessages.Clear();
            ResetChatUsageTotals();
            _chatTranscriptStore.StartNewSession();
            ChatStatusMessage = AppendBudgetReminder("Chat history cleared. Start a new conversation.");
        }
        catch (Exception ex)
        {
            _log($"Unable to reset chat transcript: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private void ResetChatUsageTotals()
    {
        ChatPromptTokenTotal = 0;
        ChatCompletionTokenTotal = 0;
        ChatTotalTokenTotal = 0;
        TotalPromptTokens = 0;
        TotalCompletionTokens = 0;
        TotalCostUsd = 0m;
    }

    private void UpdateChatUsageTotals(ChatSessionSettings? session)
    {
        if (session is null)
        {
            return;
        }

        _invokeOnUiThread(() =>
        {
            TotalPromptTokens = session.ConsumedPromptTokens;
            TotalCompletionTokens = session.ConsumedCompletionTokens;
            TotalCostUsd = session.ConsumedCostUsd;

            ChatPromptTokenTotal = ClampToInt(session.ConsumedPromptTokens);
            ChatCompletionTokenTotal = ClampToInt(session.ConsumedCompletionTokens);
            ChatTotalTokenTotal = ClampToInt(session.ConsumedTotalTokens);
        });
    }

    private decimal CalculateUsageCost(ChatUsageSnapshot usage)
    {
        decimal cost = 0m;

        if (usage.PromptTokens > 0 && ChatPromptTokenUsdPerThousand is { } promptRate && promptRate > 0m)
        {
            cost += (usage.PromptTokens / 1000m) * promptRate;
        }

        if (usage.CompletionTokens > 0 && ChatCompletionTokenUsdPerThousand is { } completionRate && completionRate > 0m)
        {
            cost += (usage.CompletionTokens / 1000m) * completionRate;
        }

        return cost;
    }

    private static int AddWithoutOverflow(int current, int delta)
    {
        if (delta <= 0)
        {
            return current;
        }

        var sum = (long)current + delta;
        return sum > int.MaxValue ? int.MaxValue : (int)sum;
    }

    private static long AddWithoutOverflow(long current, int delta)
    {
        if (delta <= 0)
        {
            return current;
        }

        var sum = current + delta;
        return sum < current ? long.MaxValue : sum;
    }

    private static int ClampToInt(long value)
    {
        if (value > int.MaxValue)
        {
            return int.MaxValue;
        }

        if (value < int.MinValue)
        {
            return int.MinValue;
        }

        return (int)value;
    }

    private async Task TryAppendTranscriptAsync(ChatMessage message)
    {
        try
        {
            await _chatTranscriptStore.AppendAsync(message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log($"Unable to write chat transcript: {ex.Message}");
        }
    }
    private bool TryHandleAssistantDirectiveCommand(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        if (string.Equals(input, ApplyAssistantDirectivesCommand, StringComparison.OrdinalIgnoreCase))
        {
            ChatInput = string.Empty;
            if (_pendingAssistantDirectives is null)
            {
                _log("No pending assistant directives to apply.");
                ChatStatusMessage = AppendBudgetReminder("Assistant ready for another prompt.");
                return true;
            }

            var pending = _pendingAssistantDirectives;
            _pendingAssistantDirectives = null;
            ProcessAssistantDirectives(pending, confirmed: true);
            return true;
        }

        if (string.Equals(input, DiscardAssistantDirectivesCommand, StringComparison.OrdinalIgnoreCase))
        {
            ChatInput = string.Empty;
            if (_pendingAssistantDirectives is null)
            {
                _log("No pending assistant directives to discard.");
            }
            else
            {
                _pendingAssistantDirectives = null;
                _log("Pending assistant directives discarded.");
            }

            ChatStatusMessage = AppendBudgetReminder("Assistant ready for another prompt.");
            return true;
        }

        return false;
    }

    private void ProcessAssistantDirectives(AssistantDirectiveBatch directives, bool confirmed)
    {
        if (directives is null)
        {
            return;
        }

        _log($"Assistant directive summary: {directives.Summary}");

        if (!string.IsNullOrWhiteSpace(directives.RiskNote))
        {
            _log($"Risk note: {directives.RiskNote}");
        }

        foreach (var reminder in directives.CredentialReminders)
        {
            _log($"Assistant credential reminder ({reminder.Credential}): {reminder.Message}");
        }

        var requiresConfirmation = !confirmed && ShouldDeferAssistantDirective(directives);
        if (requiresConfirmation)
        {
            _pendingAssistantDirectives = directives;
            _log("Assistant directives require confirmation before applying.");
            foreach (var toggle in directives.Toggles)
            {
                _log($"  Preview toggle {toggle.Name} -> {(toggle.Value ? "enabled" : "disabled")} ({FormatTogglePreviewMetadata(toggle)})");
                if (!string.IsNullOrWhiteSpace(toggle.Justification))
                {
                    _log($"    Reason: {toggle.Justification}");
                }
            }

            if (directives.Retry is not null)
            {
                _log($"  Preview retry: {DescribeRetryDirective(directives.Retry)}");
                if (!string.IsNullOrWhiteSpace(directives.Retry.Justification))
                {
                    _log($"    Reason: {directives.Retry.Justification}");
                }
            }

            foreach (var action in directives.Actions)
            {
                _log($"  Pending action {action.Name} ({FormatActionPreviewMetadata(action)})");
                if (!string.IsNullOrWhiteSpace(action.Justification))
                {
                    _log($"    Reason: {action.Justification}");
                }
            }

            _log($"Type {ApplyAssistantDirectivesCommand} to apply or {DiscardAssistantDirectivesCommand} to cancel.");
            ChatStatusMessage = "Assistant directives pending confirmation.";
            return;
        }

        _pendingAssistantDirectives = null;

        foreach (var toggle in directives.Toggles)
        {
            ApplyAssistantToggle(toggle);
        }

        if (directives.Retry is not null)
        {
            ApplyAssistantRetryDirective(directives.Retry);
        }

        foreach (var action in directives.Actions)
        {
            ApplyAssistantAction(action);
        }

        if (directives.Toggles.Count == 0 && directives.Retry is null && directives.Actions.Count == 0 && directives.CredentialReminders.Count > 0)
        {
            _log("No configuration changes were applied.");
        }

        ChatStatusMessage = AppendBudgetReminder(confirmed ? "Assistant directives applied." : "Assistant ready for another prompt.");
    }

    private static bool ShouldDeferAssistantDirective(AssistantDirectiveBatch directives)
    {
        if (directives.RequiresConfirmation)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(directives.RiskNote))
        {
            return true;
        }

        if (directives.Toggles.Any(IsRiskyToggle))
        {
            return true;
        }

        if (directives.Retry is not null && IsRiskyRetry(directives.Retry))
        {
            return true;
        }

        if (directives.Actions.Any(IsRiskyAction))
        {
            return true;
        }

        return false;
    }

    private static bool IsRiskyToggle(AssistantToggleDirective toggle)
    {
        if (toggle.RequiresConfirmation)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(toggle.RiskLevel) && string.Equals(toggle.RiskLevel, "high", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return toggle.Value && s_riskyAssistantOptions.Contains(toggle.Name);
    }

    private static bool IsRiskyRetry(AssistantRetryDirective directive)
    {
        if (directive.Attempts is int attempts && attempts > 6)
        {
            return true;
        }

        if (directive.BaseDelaySeconds is double baseDelay && baseDelay > 30)
        {
            return true;
        }

        if (directive.MaxDelaySeconds is double maxDelay && maxDelay > 300)
        {
            return true;
        }

        return false;
    }

    private static bool IsRiskyAction(AssistantActionDirective action)
    {
        if (action.RequiresConfirmation)
        {
            return true;
        }

        return string.Equals(action.Name, "start_run", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatTogglePreviewMetadata(AssistantToggleDirective toggle)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(toggle.RiskLevel))
        {
            parts.Add($"risk {toggle.RiskLevel}");
        }

        if (toggle.Confidence is double confidence)
        {
            parts.Add($"confidence {confidence:0.##}");
        }

        if (toggle.RequiresConfirmation)
        {
            parts.Add("confirmation requested");
        }

        if (parts.Count == 0)
        {
            return "no metadata";
        }

        return string.Join(", ", parts);
    }

    private static string FormatActionPreviewMetadata(AssistantActionDirective action)
    {
        var parts = new List<string>();
        if (action.RequiresConfirmation)
        {
            parts.Add("confirmation requested");
        }

        if (action.Plan is RunPlan plan)
        {
            string descriptor;
            if (plan.ExecutionMode == RunPlanExecutionMode.Scheduled && plan.ScheduledForUtc is { } scheduled)
            {
                descriptor = $"scheduled for {scheduled:u}";
            }
            else
            {
                descriptor = "awaiting approval";
            }

            if (plan.Settings.Count > 0)
            {
                descriptor += $", {plan.Settings.Count} override(s)";
            }

            parts.Add($"run plan ({descriptor})");
        }

        return parts.Count == 0 ? "no metadata" : string.Join(", ", parts);
    }

    private void ApplyAssistantToggle(AssistantToggleDirective toggle)
    {
        if (!_assistantToggleBindings.TryGetValue(toggle.Name, out var binding))
        {
            _log($"Assistant directive skipped unknown toggle \"{toggle.Name}\".");
            return;
        }

        var desired = toggle.Value;
        var current = binding.Getter();
        var label = desired ? "enabled" : "disabled";

        if (current == desired)
        {
            _log($"Assistant directive left {toggle.Name} unchanged (already {label}).");
            return;
        }

        binding.Setter(desired);

        var messageBuilder = new StringBuilder($"Assistant {label} {toggle.Name}.");
        if (toggle.Confidence is double confidence)
        {
            messageBuilder.Append($" (confidence {confidence:0.##})");
        }

        if (!string.IsNullOrWhiteSpace(toggle.RiskLevel))
        {
            messageBuilder.Append($" [risk {toggle.RiskLevel}]");
        }

        _log(messageBuilder.ToString());

        if (!string.IsNullOrWhiteSpace(toggle.Justification))
        {
            _log($"Reason: {toggle.Justification}");
        }
    }

    private void ApplyAssistantRetryDirective(AssistantRetryDirective directive)
    {
        var updates = new List<string>();
        var baseDelaySeconds = directive.BaseDelaySeconds;

        if (directive.Enable is bool enable)
        {
            if (_getEnableHttpRetries() != enable)
            {
                _setEnableHttpRetries(enable);
                updates.Add($"enable={enable.ToString().ToLowerInvariant()}");
            }
            else
            {
                _log($"Assistant directive left HTTP retries {(enable ? "enabled" : "disabled")} (already set).");
            }
        }

        if (directive.Attempts is int attempts)
        {
            if (attempts < 0 || attempts > 10)
            {
                _log($"Assistant directive skipped invalid retry attempt count ({attempts}). Expected 0-10.");
            }
            else if (_getHttpRetryAttempts() != attempts)
            {
                _setHttpRetryAttempts(attempts);
                updates.Add($"attempts={attempts}");
            }
            else
            {
                _log($"Assistant directive left HTTP retry attempts at {attempts} (already set).");
            }
        }

        if (baseDelaySeconds is double baseDelay)
        {
            if (!IsDelayWithinRange(baseDelay))
            {
                _log($"Assistant directive skipped invalid retry base delay ({FormatSeconds(baseDelay)}). Expected between 0 and 600 seconds.");
            }
            else if (Math.Abs(_getHttpRetryBaseDelaySeconds() - baseDelay) > 0.0001)
            {
                _setHttpRetryBaseDelaySeconds(baseDelay);
                updates.Add($"base_delay={FormatSeconds(baseDelay)}");
            }
            else
            {
                _log($"Assistant directive left HTTP retry base delay at {FormatSeconds(baseDelay)} (already set).");
            }
        }

        if (directive.MaxDelaySeconds is double maxDelay)
        {
            if (!IsDelayWithinRange(maxDelay))
            {
                _log($"Assistant directive skipped invalid retry max delay ({FormatSeconds(maxDelay)}). Expected between 0 and 600 seconds.");
            }
            else if (baseDelaySeconds is double baseDelayForComparison && maxDelay < baseDelayForComparison)
            {
                _log($"Assistant directive skipped retry max delay ({FormatSeconds(maxDelay)}) because it is less than the base delay ({FormatSeconds(baseDelayForComparison)}).");
            }
            else if (Math.Abs(_getHttpRetryMaxDelaySeconds() - maxDelay) > 0.0001)
            {
                _setHttpRetryMaxDelaySeconds(maxDelay);
                updates.Add($"max_delay={FormatSeconds(maxDelay)}");
            }
            else
            {
                _log($"Assistant directive left HTTP retry max delay at {FormatSeconds(maxDelay)} (already set).");
            }
        }

        if (updates.Count > 0)
        {
            _log("Assistant updated HTTP retry settings: " + string.Join(", ", updates));
        }

        if (!string.IsNullOrWhiteSpace(directive.Justification))
        {
            _log("Retry directive justification: " + directive.Justification);
        }
    }

    private void ApplyAssistantAction(AssistantActionDirective action)
    {
        if (string.IsNullOrWhiteSpace(action.Name))
        {
            _log("Assistant action skipped: action name was not provided.");
            return;
        }

        var normalizedName = action.Name.Trim();
        _log($"Assistant action requested: {normalizedName}.");
        if (!string.IsNullOrWhiteSpace(action.Justification))
        {
            _log($"Reason: {action.Justification}");
        }

        var loweredName = normalizedName.ToLowerInvariant();

        switch (loweredName)
        {
            case "start_run":
                if (_isRunInProgress())
                {
                    _log("Assistant action start_run skipped: a run is already in progress.");
                    return;
                }

                if (!_canExecuteRunCommand())
                {
                    _log("Assistant action start_run skipped: run command is unavailable.");
                    return;
                }

                if (!ConfirmAssistantAction("The assistant wants to start a new migration run. Start the run now?", action.Justification))
                {
                    _log("Assistant action start_run canceled by operator.");
                    return;
                }

                _log("Assistant action start_run executed: run command invoked.");
                _executeRunCommand();
                return;

            case "open_output_folder":
                if (action.RequiresConfirmation && !ConfirmAssistantAction("Open the configured output folder?", action.Justification))
                {
                    _log("Assistant action open_output_folder canceled by operator.");
                    return;
                }

                TryOpenAssistantPath(normalizedName, _resolveBaseOutputFolder());
                return;

            case "open_run_folder":
                if (action.RequiresConfirmation && !ConfirmAssistantAction("Open the most recent run folder?", action.Justification))
                {
                    _log("Assistant action open_run_folder canceled by operator.");
                    return;
                }

                TryOpenAssistantPath(normalizedName, _getLatestStoreOutputFolder());
                return;

            case "open_manual_bundle":
                if (action.RequiresConfirmation && !ConfirmAssistantAction("Open the latest manual bundle archive?", action.Justification))
                {
                    _log("Assistant action open_manual_bundle canceled by operator.");
                    return;
                }

                TryOpenAssistantPath(normalizedName, _getLatestManualBundlePath());
                return;

            case "open_manual_report":
                if (action.RequiresConfirmation && !ConfirmAssistantAction("Open the latest manual migration report?", action.Justification))
                {
                    _log("Assistant action open_manual_report canceled by operator.");
                    return;
                }

                TryOpenAssistantPath(normalizedName, _getLatestManualReportPath());
                return;

            case "open_run_delta":
                if (action.RequiresConfirmation && !ConfirmAssistantAction("Open the latest run delta summary?", action.Justification))
                {
                    _log("Assistant action open_run_delta canceled by operator.");
                    return;
                }

                TryOpenAssistantPath(normalizedName, _getLatestRunDeltaPath());
                return;

            case "open_ai_brief":
                if (action.RequiresConfirmation && !ConfirmAssistantAction("Open the latest AI migration brief?", action.Justification))
                {
                    _log("Assistant action open_ai_brief canceled by operator.");
                    return;
                }

                TryOpenAssistantPath(normalizedName, _getLatestRunAiBriefPath());
                return;

            case "schedule_run":
                if (action.Plan is not RunPlan plan)
                {
                    _log("Assistant action schedule_run skipped: plan details were missing.");
                    return;
                }

                if (action.RequiresConfirmation && !ConfirmAssistantAction(
                        ExportPlanningViewModel.BuildRunPlanConfirmationPrompt(plan),
                        action.Justification))
                {
                    _log("Assistant action schedule_run canceled by operator.");
                    return;
                }

                _enqueueRunPlan(plan);
                _log($"Assistant action schedule_run executed: plan \"{plan.Name}\" queued.");
                return;
        }

        if (action.RequiresConfirmation && !ConfirmAssistantAction($"The assistant requested action \"{normalizedName}\". Continue?", action.Justification))
        {
            _log($"Assistant action {normalizedName} canceled by operator.");
            return;
        }

        _log($"Assistant action {normalizedName} is not recognized. No changes applied.");
    }

    private void TryOpenAssistantPath(string actionName, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _log($"Assistant action {actionName} skipped: no path available.");
            return;
        }

        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                _log($"Assistant action {actionName} skipped: {path} was not found.");
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            };
            Process.Start(startInfo);
            _log($"Assistant opened {path}.");
        }
        catch (Exception ex)
        {
            _log($"Assistant action {actionName} failed: {ex.Message}");
        }
    }

    private static bool ConfirmAssistantAction(string prompt, string? justification)
    {
        var builder = new StringBuilder();
        builder.Append(prompt);
        if (!string.IsNullOrWhiteSpace(justification))
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.Append("Reason: ");
            builder.Append(justification.Trim());
        }

        var text = builder.ToString();

        if (Application.Current?.Dispatcher is System.Windows.Threading.Dispatcher dispatcher)
        {
            if (dispatcher.CheckAccess())
            {
                return MessageBox.Show(text, "Confirm assistant action", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            }

            return dispatcher.Invoke(() => MessageBox.Show(text, "Confirm assistant action", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
        }

        return MessageBox.Show(text, "Confirm assistant action", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private static bool IsDelayWithinRange(double value)
        => value >= 0 && value <= 600;

    private static string FormatSeconds(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture) + "s";

    private static string DescribeRetryDirective(AssistantRetryDirective directive)
    {
        var parts = new List<string>();

        if (directive.Enable is bool enable)
        {
            parts.Add($"enable={enable.ToString().ToLowerInvariant()}");
        }

        if (directive.Attempts is int attempts)
        {
            parts.Add($"attempts={attempts}");
        }

        if (directive.BaseDelaySeconds is double baseDelay)
        {
            parts.Add($"base_delay={FormatSeconds(baseDelay)}");
        }

        if (directive.MaxDelaySeconds is double maxDelay)
        {
            parts.Add($"max_delay={FormatSeconds(maxDelay)}");
        }

        return parts.Count == 0 ? "no retry changes" : string.Join(", ", parts);
    }

    private static string DescribeActionDirective(AssistantActionDirective action)
    {
        var builder = new StringBuilder();
        builder.Append(action.Name);

        if (action.Plan is RunPlan plan)
        {
            builder.Append(" plan=");
            builder.Append(plan.Name);
            if (plan.Settings.Count > 0)
            {
                builder.Append(" (");
                builder.Append(string.Join(", ", plan.Settings.Select(DescribeSettingsOverride)));
                builder.Append(')');
            }
        }

        return builder.ToString();
    }

    private static string DescribeSettingsOverride(RunPlanSetting setting)
        => $"{setting.Name}={setting.Value}";
    private ChatSessionContext BuildChatContext()
    {
        var hostSnapshot = _hostSnapshotProvider();
        var datasets = _artifactIndexingService.GetIndexedDatasets();
        var datasetNames = datasets.Select(d => d.Name).ToList();

        return new ChatSessionContext(
            hostSnapshot.SelectedPlatform,
            hostSnapshot.ExportCsv,
            hostSnapshot.ExportShopify,
            hostSnapshot.ExportWoo,
            hostSnapshot.ExportReviews,
            hostSnapshot.ExportXlsx,
            hostSnapshot.ExportJsonl,
            hostSnapshot.ExportPluginsCsv,
            hostSnapshot.ExportPluginsJsonl,
            hostSnapshot.ExportThemesCsv,
            hostSnapshot.ExportThemesJsonl,
            hostSnapshot.ExportPublicExtensionFootprints,
            hostSnapshot.ExportPublicDesignSnapshot,
            hostSnapshot.ExportPublicDesignScreenshots,
            hostSnapshot.ExportStoreConfiguration,
            hostSnapshot.ImportStoreConfiguration,
            hostSnapshot.HasWordPressCredentials,
            hostSnapshot.HasShopifyCredentials,
            hostSnapshot.HasTargetCredentials,
            hostSnapshot.EnableHttpRetries,
            hostSnapshot.HttpRetryAttempts,
            hostSnapshot.AdditionalPublicExtensionPages,
            hostSnapshot.AdditionalDesignSnapshotPages,
            datasets.Count,
            datasetNames);
    }

    private IReadOnlyList<ChatAssistantToolbox.ExportFileSummary> CollectRecentExportFiles(int limit)
    {
        if (limit <= 0)
        {
            return Array.Empty<ChatAssistantToolbox.ExportFileSummary>();
        }

        try
        {
            var outputFolder = _getOutputFolder();
            if (string.IsNullOrWhiteSpace(outputFolder) || !Directory.Exists(outputFolder))
            {
                return Array.Empty<ChatAssistantToolbox.ExportFileSummary>();
            }

            var files = Directory
                .EnumerateFiles(outputFolder, "*", SearchOption.AllDirectories)
                .Select(path =>
                {
                    try
                    {
                        return new FileInfo(path);
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                })
                .Where(info => info is { Exists: true } candidate && IsLikelyExportFile(candidate))
                .Select(info => info!)
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Take(limit)
                .Select(info => new ChatAssistantToolbox.ExportFileSummary(
                    info.Name,
                    info.FullName,
                    info.Length,
                    info.LastWriteTimeUtc))
                .ToList();

            return files;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{ex.Message}", ex);
        }
    }

    private IReadOnlyList<string> CollectRecentLogs(int limit)
    {
        if (limit <= 0)
        {
            return Array.Empty<string>();
        }

        var logs = _getLogsSnapshot();
        if (logs is null || logs.Count == 0)
        {
            return Array.Empty<string>();
        }

        var start = Math.Max(0, logs.Count - limit);
        return logs.Skip(start).Take(limit).ToArray();
    }

    private static bool IsLikelyExportFile(FileInfo info)
    {
        var extension = info.Extension;
        if (string.IsNullOrEmpty(extension))
        {
            return false;
        }

        return extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".zip", StringComparison.OrdinalIgnoreCase);
    }

    private string AppendBudgetReminder(string message)
    {
        var reminder = BuildChatBudgetReminder();
        return reminder is null ? message : $"{message} {reminder}";
    }

    private string? BuildChatBudgetReminder()
    {
        if (ChatMaxPromptTokens is null && ChatMaxTotalTokens is null && ChatMaxCostUsd is null)
        {
            return null;
        }

        var segments = new List<string>();
        if (ChatMaxPromptTokens is { } promptTokens)
        {
            segments.Add($"prompt ≤ {promptTokens:N0} tokens");
        }

        if (ChatMaxTotalTokens is { } totalTokens)
        {
            segments.Add($"total ≤ {totalTokens:N0} tokens");
        }

        if (ChatMaxCostUsd is { } costLimit)
        {
            segments.Add($"cost ≤ ${costLimit:F2}");
        }

        if (segments.Count == 0)
        {
            return "Session budgets are active to prevent accidental overages.";
        }

        return $"Session budgets active ({string.Join(", ", segments)}). Requests stop automatically to prevent accidental overages.";
    }

    private void UpdateChatConfigurationStatus()
    {
        if (IsChatBusy)
        {
            return;
        }

        if (!HasChatConfiguration)
        {
            ChatStatusMessage = "Enter API endpoint, model, and API key to enable the assistant.";
            return;
        }

        if (SelectedChatMode == ChatInteractionMode.DatasetQuestion)
        {
            var baseMessage = _artifactIndexingService.HasAnyIndexedArtifacts
                ? "Dataset Q&A ready. Ask about the exported CSV or JSONL files."
                : "Run a scrape with CSV or JSONL exports to enable dataset Q&A.";
            ChatStatusMessage = _artifactIndexingService.HasAnyIndexedArtifacts
                ? AppendBudgetReminder(baseMessage)
                : baseMessage;
            return;
        }

        ChatStatusMessage = AppendBudgetReminder("Assistant ready for your next prompt.");
    }

    private void OnChatMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SaveChatTranscriptCommand.RaiseCanExecuteChanged();
        ClearChatHistoryCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(HasChatMessages));

        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is { Count: > 0 })
        {
            UpdateChatConfigurationStatus();
        }
    }

    private void LoadChatApiKey()
    {
        try
        {
            if (!File.Exists(_chatKeyPath))
            {
                ChatApiKey = string.Empty;
                return;
            }

            var key = ProtectedDataStore.Read(_chatKeyPath);
            if (key is null)
            {
                ChatApiKey = string.Empty;
                return;
            }

            ChatApiKey = key;
            HasChatApiKey = !string.IsNullOrWhiteSpace(key);
        }
        catch (Exception ex)
        {
            _log($"Unable to load chat API key: {ex.Message}");
            ChatApiKey = string.Empty;
            HasChatApiKey = false;
        }
    }

    private bool TryPersistChatApiKey(string value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                if (File.Exists(_chatKeyPath))
                {
                    File.Delete(_chatKeyPath);
                }

                return true;
            }

            ProtectedDataStore.Write(_chatKeyPath, value);
            return true;
        }
        catch (Exception ex)
        {
            _log($"Unable to persist chat API key: {ex.Message}");
            return false;
        }
    }

    private void OnPropertyChanged(string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public sealed record HostSnapshot(
        PlatformMode SelectedPlatform,
        bool ExportCsv,
        bool ExportShopify,
        bool ExportWoo,
        bool ExportReviews,
        bool ExportXlsx,
        bool ExportJsonl,
        bool ExportPluginsCsv,
        bool ExportPluginsJsonl,
        bool ExportThemesCsv,
        bool ExportThemesJsonl,
        bool ExportPublicExtensionFootprints,
        bool ExportPublicDesignSnapshot,
        bool ExportPublicDesignScreenshots,
        bool ExportStoreConfiguration,
        bool ImportStoreConfiguration,
        bool HasWordPressCredentials,
        bool HasShopifyCredentials,
        bool HasTargetCredentials,
        bool EnableHttpRetries,
        int HttpRetryAttempts,
        string AdditionalPublicExtensionPages,
        string AdditionalDesignSnapshotPages);
}
