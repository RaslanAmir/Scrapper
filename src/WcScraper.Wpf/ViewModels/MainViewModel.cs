
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WcScraper.Core;
using WcScraper.Core.Exporters;
using WcScraper.Core.Shopify;
using WcScraper.Core.Telemetry;
using WcScraper.Wpf.Models;
using WcScraper.Wpf.Reporting;
using WcScraper.Wpf.Services;
using WcScraper.Wpf.Extensions;

namespace WcScraper.Wpf.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IDialogService _dialogs;
    private readonly WooScraper _wooScraper;
    private readonly ShopifyScraper _shopifyScraper;
    private readonly HttpClient _httpClient;
    private readonly WooProvisioningService _wooProvisioningService;
    private readonly HeadlessBrowserScreenshotService _designScreenshotService;
    private readonly WordPressDirectoryClient _wpDirectoryClient;
    private readonly IScraperInstrumentation _uiInstrumentation;
    private readonly ChatAssistantService _chatAssistantService;
    private readonly ChatTranscriptStore _chatTranscriptStore;
    private readonly IArtifactIndexingService _artifactIndexingService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MainViewModel> _logger;
    private readonly string _settingsDirectory;
    private readonly string _preferencesPath;
    private readonly string _chatKeyPath;
    private readonly RunPlanner _runPlanner;
    private readonly ReadOnlyObservableCollection<RunPlan> _runPlans;
    private static readonly TimeSpan DirectoryLookupDelay = TimeSpan.FromMilliseconds(400);
    private const string ApplyAssistantDirectivesCommand = "/apply-directives";
    private const string DiscardAssistantDirectivesCommand = "/discard-directives";
    private const int RunSnapshotHistoryLimit = 6;
    private const string RunSnapshotHistoryFolderName = ".run-history";
    private const string RunDeltaNarrativeFileName = "manual-migration-report.delta.md";
    private const string RunDeltaDataFileName = "manual-migration-report.delta.json";
    private const string ExportVerificationFileName = "export-verification.ai.md";
    private static readonly HashSet<string> s_riskyAssistantOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(ExportPublicExtensionFootprints),
        nameof(ExportPublicDesignSnapshot),
        nameof(ExportPublicDesignScreenshots),
        nameof(ExportStoreConfiguration),
        nameof(ImportStoreConfiguration),
    };
    private bool _isRunning;
    private string _storeUrl = "";
    private string _outputFolder = Path.GetFullPath("output");
    private bool _expCsv = true;
    private bool _expShopify = true;
    private bool _expWoo = true;
    private bool _expReviews = true;
    private bool _expXlsx = false;
    private bool _expJsonl = false;
    private bool _expPluginsCsv = false;
    private bool _expPluginsJsonl = false;
    private bool _expThemesCsv = false;
    private bool _expThemesJsonl = false;
    private bool _expPublicExtensionFootprints = false;
    private string _publicExtensionMaxPages = "75";
    private string _publicExtensionMaxBytes = "3145728";
    private string _additionalPublicExtensionPages = string.Empty;
    private string _additionalDesignSnapshotPages = string.Empty;
    private string _designScreenshotBreakpointsText = string.Empty;
    private bool _expPublicDesignSnapshot = false;
    private bool _expPublicDesignScreenshots = false;
    private bool _expStoreConfiguration = false;
    private bool _importStoreConfiguration = false;
    private bool _enableHttpRetries = true;
    private int _httpRetryAttempts = 3;
    private double _httpRetryBaseDelaySeconds = 1;
    private double _httpRetryMaxDelaySeconds = 30;
    private PlatformMode _selectedPlatform = PlatformMode.WooCommerce;
    private string _shopifyStoreUrl = "";
    private string _shopifyAdminAccessToken = "";
    private string _shopifyStorefrontAccessToken = "";
    private string _shopifyApiKey = "";
    private string _shopifyApiSecret = "";
    private string _wordPressUsername = "";
    private string _wordPressApplicationPassword = "";
    private string _targetStoreUrl = "";
    private string _targetConsumerKey = "";
    private string _targetConsumerSecret = "";
    private ProvisioningContext? _lastProvisioningContext;
    private readonly JsonSerializerOptions _configurationWriteOptions = new() { WriteIndented = true };
    private readonly JsonSerializerOptions _artifactWriteOptions = new() { WriteIndented = true };
    private readonly JsonSerializerOptions _preferencesWriteOptions = new() { WriteIndented = true };
    private readonly JsonSerializerOptions _runHistoryWriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
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
    private readonly TimeSpan _logSummaryDebounce = TimeSpan.FromSeconds(6);
    private readonly object _logSummarySync = new();
    private System.Threading.Timer? _logSummaryTimer;
    private int _pendingLogSummaryCount;
    private bool _isLogSummaryBusy;
    private LogTriageResult? _latestLogSummary;
    private string _latestLogSummaryOverview = string.Empty;
    private string _manualRunGoals = string.Empty;
    private string _latestRunGoalsSnapshot = string.Empty;
    private string _latestRunSnapshotJson = string.Empty;
    private string _latestRunAiNarrative = string.Empty;
    private string? _latestRunAiBriefPath;
    private string? _latestStoreOutputFolder;
    private string? _latestManualReportPath;
    private string? _latestManualBundlePath;
    private string? _latestRunDeltaPath;
    private bool _isAssistantPanelExpanded;
    private string _latestRunAiRecommendationSummary = string.Empty;
    private AiArtifactAnnotation? _latestRunAiAnnotation;
    private AssistantDirectiveBatch? _pendingAssistantDirectives;
    private RunPlan? _activeRunPlan;
    private RunPlanExecutionOutcome? _activeRunPlanOutcome;
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _chatCts;
    private readonly Dictionary<string, (Func<bool> Getter, Action<bool> Setter)> _assistantToggleBindings;
    private readonly ObservableCollection<AutomationScriptDisplay> _latestAutomationScripts = new();
    private readonly ObservableCollection<string> _latestAutomationScriptWarnings = new();
    private string _latestAutomationScriptSummary = string.Empty;
    private string _latestAutomationScriptError = string.Empty;
    private readonly IReadOnlyList<ChatModeOption> _chatModeOptions = new[]
    {
        new ChatModeOption(ChatInteractionMode.GeneralAssistant, "General assistant"),
        new ChatModeOption(ChatInteractionMode.DatasetQuestion, "Ask about exported data"),
    };
    private ChatInteractionMode _selectedChatMode = ChatInteractionMode.GeneralAssistant;
    private int _chatTranscriptLoadState;

    public MainViewModel(IDialogService dialogs, ILoggerFactory loggerFactory)
        : this(
            dialogs,
            CreateDefaultWooScraper(loggerFactory),
            CreateDefaultShopifyScraper(loggerFactory),
            new HttpClient(),
            loggerFactory)
    {
    }

    public MainViewModel()
        : this(new WcScraper.Wpf.Services.DialogService(), NullLoggerFactory.Instance)
    {
    }

    internal MainViewModel(IDialogService dialogs, WooScraper wooScraper, ShopifyScraper shopifyScraper, ILoggerFactory loggerFactory)
        : this(dialogs, wooScraper, shopifyScraper, new HttpClient(), loggerFactory)
    {
    }

    internal MainViewModel(
        IDialogService dialogs,
        WooScraper wooScraper,
        ShopifyScraper shopifyScraper,
        HttpClient httpClient,
        ILoggerFactory loggerFactory)
        : this(
            dialogs,
            wooScraper,
            shopifyScraper,
            httpClient,
            new HeadlessBrowserScreenshotService(),
            CreateDefaultArtifactIndexingService(out var artifactIndexingService),
            new ChatAssistantService(artifactIndexingService),
            loggerFactory)
    {
    }

    internal MainViewModel(
        IDialogService dialogs,
        WooScraper wooScraper,
        ShopifyScraper shopifyScraper,
        HttpClient httpClient,
        HeadlessBrowserScreenshotService designScreenshotService,
        IArtifactIndexingService artifactIndexingService,
        ChatAssistantService chatAssistantService,
        ILoggerFactory loggerFactory,
        ILogger<MainViewModel>? logger = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = logger ?? _loggerFactory.CreateLogger<MainViewModel>();
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _wooScraper = wooScraper ?? throw new ArgumentNullException(nameof(wooScraper));
        _shopifyScraper = shopifyScraper ?? throw new ArgumentNullException(nameof(shopifyScraper));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _wooProvisioningService = new WooProvisioningService();
        _uiInstrumentation = new UiScraperInstrumentation(Append, _loggerFactory);
        _wpDirectoryClient = new WordPressDirectoryClient(
            _httpClient,
            instrumentation: _uiInstrumentation,
            logger: _loggerFactory.CreateLogger<WordPressDirectoryClient>(),
            loggerFactory: _loggerFactory);
        _designScreenshotService = designScreenshotService ?? throw new ArgumentNullException(nameof(designScreenshotService));
        _artifactIndexingService = artifactIndexingService ?? throw new ArgumentNullException(nameof(artifactIndexingService));
        _chatAssistantService = chatAssistantService ?? throw new ArgumentNullException(nameof(chatAssistantService));
        _artifactIndexingService.DiagnosticLogger = Append;
        _settingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WcScraper");
        _preferencesPath = Path.Combine(_settingsDirectory, "preferences.json");
        _chatKeyPath = Path.Combine(_settingsDirectory, "chat.key");
        _chatTranscriptStore = new ChatTranscriptStore(_settingsDirectory);
        var dispatcher = System.Windows.Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _runPlanner = new RunPlanner(ExecuteRunPlanAsync, dispatcher);
        _runPlans = _runPlanner.Plans;
        ((INotifyCollectionChanged)_runPlans).CollectionChanged += OnRunPlansChanged;
        _runPlanner.PlanQueued += OnRunPlanQueued;
        _runPlanner.PlanExecutionCompleted += OnRunPlanExecutionCompleted;
        BrowseCommand = new RelayCommand(OnBrowse);
        RunCommand = new RelayCommand(
            async () =>
            {
                var cancellationToken = PrepareRunCancellationToken();
                await OnRunAsync(cancellationToken);
            },
            () => !IsRunning);
        CancelRunCommand = new RelayCommand(OnCancelRun, () => _runCts is not null);
        SelectAllCategoriesCommand = new RelayCommand(() => SetSelection(CategoryChoices, true));
        ClearCategoriesCommand = new RelayCommand(() => SetSelection(CategoryChoices, false));
        SelectAllTagsCommand = new RelayCommand(() => SetSelection(TagChoices, true));
        ClearTagsCommand = new RelayCommand(() => SetSelection(TagChoices, false));
        ExportCollectionsCommand = new RelayCommand(OnExportCollections);
        ReplicateCommand = new RelayCommand(
            async () =>
            {
                var cancellationToken = PrepareRunCancellationToken();
                await OnReplicateStoreAsync(cancellationToken);
            },
            () => !IsRunning && CanReplicate);
        OpenLogCommand = new RelayCommand(OnOpenLog);
        ExplainLogsCommand = new RelayCommand(async () => await OnExplainLatestLogsAsync(), CanExplainLogs);
        LaunchWizardCommand = new RelayCommand(OnLaunchWizard, CanLaunchWizard);
        SendChatCommand = new RelayCommand(async () => await OnSendChatAsync(), CanSendChat);
        CancelChatCommand = new RelayCommand(OnCancelChat, CanCancelChat);
        SaveChatTranscriptCommand = new RelayCommand(async () => await OnSaveChatTranscriptAsync(), CanSaveChatTranscript);
        ClearChatHistoryCommand = new RelayCommand(async () => await OnClearChatHistoryAsync(), CanClearChatHistory);
        UseAiRecommendationCommand = new RelayCommand<string?>(OnUseAiRecommendation);
        CopyAutomationScriptCommand = new RelayCommand<AutomationScriptDisplay>(OnCopyAutomationScript);
        ApproveRunPlanCommand = new RelayCommand<RunPlan>(OnApproveRunPlan, CanApproveRunPlan);
        DismissRunPlanCommand = new RelayCommand<RunPlan>(OnDismissRunPlan, CanDismissRunPlan);
        ChatMessages = new ObservableCollection<ChatMessage>();
        ChatMessages.CollectionChanged += OnChatMessagesCollectionChanged;
        LatestRunAiRecommendations.CollectionChanged += (_, __) => OnPropertyChanged(nameof(HasAiRecommendations));
        _latestAutomationScripts.CollectionChanged += (_, __) => OnPropertyChanged(nameof(HasAutomationScripts));
        _latestAutomationScriptWarnings.CollectionChanged += (_, __) => OnPropertyChanged(nameof(HasAutomationScriptWarnings));
        Logs.CollectionChanged += OnLogsCollectionChanged;
        LoadPreferences();
        LoadChatApiKey();
        UpdateChatConfigurationStatus();
        _artifactIndexingService.IndexChanged += OnArtifactIndexChanged;
        _ = EnsureChatTranscriptLoadedAsync();

        _assistantToggleBindings = new Dictionary<string, (Func<bool>, Action<bool>)>(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(ExportCsv)] = (() => ExportCsv, value => ExportCsv = value),
            [nameof(ExportShopify)] = (() => ExportShopify, value => ExportShopify = value),
            [nameof(ExportWoo)] = (() => ExportWoo, value => ExportWoo = value),
            [nameof(ExportReviews)] = (() => ExportReviews, value => ExportReviews = value),
            [nameof(ExportXlsx)] = (() => ExportXlsx, value => ExportXlsx = value),
            [nameof(ExportJsonl)] = (() => ExportJsonl, value => ExportJsonl = value),
            [nameof(ExportPluginsCsv)] = (() => ExportPluginsCsv, value => ExportPluginsCsv = value),
            [nameof(ExportPluginsJsonl)] = (() => ExportPluginsJsonl, value => ExportPluginsJsonl = value),
            [nameof(ExportThemesCsv)] = (() => ExportThemesCsv, value => ExportThemesCsv = value),
            [nameof(ExportThemesJsonl)] = (() => ExportThemesJsonl, value => ExportThemesJsonl = value),
            [nameof(ExportPublicExtensionFootprints)] = (() => ExportPublicExtensionFootprints, value => ExportPublicExtensionFootprints = value),
            [nameof(ExportPublicDesignSnapshot)] = (() => ExportPublicDesignSnapshot, value => ExportPublicDesignSnapshot = value),
            [nameof(ExportPublicDesignScreenshots)] = (() => ExportPublicDesignScreenshots, value => ExportPublicDesignScreenshots = value),
            [nameof(ExportStoreConfiguration)] = (() => ExportStoreConfiguration, value => ExportStoreConfiguration = value),
            [nameof(ImportStoreConfiguration)] = (() => ImportStoreConfiguration, value => ImportStoreConfiguration = value),
            [nameof(EnableHttpRetries)] = (() => EnableHttpRetries, value => EnableHttpRetries = value),
        };
    }

    private static WooScraper CreateDefaultWooScraper(ILoggerFactory loggerFactory)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        return new WooScraper(
            logger: loggerFactory.CreateLogger<WooScraper>(),
            loggerFactory: loggerFactory);
    }

    private static ShopifyScraper CreateDefaultShopifyScraper(ILoggerFactory loggerFactory)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        return new ShopifyScraper(
            logger: loggerFactory.CreateLogger<ShopifyScraper>(),
            loggerFactory: loggerFactory);
    }

    private static IArtifactIndexingService CreateDefaultArtifactIndexingService(out IArtifactIndexingService service)
    {
        service = new ArtifactIndexingService();
        return service;
    }

    // XAML-friendly default constructor + Dialogs setter
    public MainViewModel() : this(new WcScraper.Wpf.Services.DialogService()) { }
    public IDialogService Dialogs { set { /* for XAML object element */ } }

    public string StoreUrl { get => _storeUrl; set { _storeUrl = value; OnPropertyChanged(); } }
    public string OutputFolder { get => _outputFolder; set { _outputFolder = value; OnPropertyChanged(); } }
    public bool ExportCsv
    {
        get => _expCsv;
        set
        {
            if (_expCsv == value)
            {
                return;
            }

            _expCsv = value;
            OnPropertyChanged();
            SavePreferences();
        }
    }

    public bool ExportShopify
    {
        get => _expShopify;
        set
        {
            if (_expShopify == value)
            {
                return;
            }

            _expShopify = value;
            OnPropertyChanged();
            SavePreferences();
        }
    }

    public bool ExportWoo
    {
        get => _expWoo;
        set
        {
            if (_expWoo == value)
            {
                return;
            }

            _expWoo = value;
            OnPropertyChanged();
            SavePreferences();
        }
    }

    public bool ExportReviews
    {
        get => _expReviews;
        set
        {
            if (_expReviews == value)
            {
                return;
            }

            _expReviews = value;
            OnPropertyChanged();
            SavePreferences();
        }
    }

    public bool ExportXlsx
    {
        get => _expXlsx;
        set
        {
            if (_expXlsx == value)
            {
                return;
            }

            _expXlsx = value;
            OnPropertyChanged();
            SavePreferences();
        }
    }

    public bool ExportJsonl
    {
        get => _expJsonl;
        set
        {
            if (_expJsonl == value)
            {
                return;
            }

            _expJsonl = value;
            OnPropertyChanged();
            SavePreferences();
        }
    }

    public bool ExportPluginsCsv
    {
        get => _expPluginsCsv;
        set
        {
            if (_expPluginsCsv == value)
            {
                return;
            }

            _expPluginsCsv = value;
            OnPropertyChanged();
            SavePreferences();
        }
    }

    public bool ExportPluginsJsonl
    {
        get => _expPluginsJsonl;
        set
        {
            if (_expPluginsJsonl == value)
            {
                return;
            }

            _expPluginsJsonl = value;
            OnPropertyChanged();
            SavePreferences();
        }
    }

    public bool ExportThemesCsv
    {
        get => _expThemesCsv;
        set
        {
            if (_expThemesCsv == value)
            {
                return;
            }

            _expThemesCsv = value;
            OnPropertyChanged();
            SavePreferences();
        }
    }

    public bool ExportThemesJsonl
    {
        get => _expThemesJsonl;
        set
        {
            if (_expThemesJsonl == value)
            {
                return;
            }

            _expThemesJsonl = value;
            OnPropertyChanged();
            SavePreferences();
        }
    }

    public bool ExportPublicExtensionFootprints
    {
        get => _expPublicExtensionFootprints;
        set
        {
            if (_expPublicExtensionFootprints == value)
            {
                return;
            }

            _expPublicExtensionFootprints = value;
            OnPropertyChanged();
            SavePreferences();
        }
    }
    public string AdditionalPublicExtensionPages
    {
        get => _additionalPublicExtensionPages;
        set
        {
            if (_additionalPublicExtensionPages == value)
            {
                return;
            }

            _additionalPublicExtensionPages = value ?? string.Empty;
            OnPropertyChanged();
            SavePreferences();
        }
    }
    public string PublicExtensionMaxPages
    {
        get => _publicExtensionMaxPages;
        set
        {
            var newValue = value ?? string.Empty;
            if (_publicExtensionMaxPages == newValue)
            {
                return;
            }

            _publicExtensionMaxPages = newValue;
            OnPropertyChanged();
            SavePreferences();
        }
    }

    public string PublicExtensionMaxBytes
    {
        get => _publicExtensionMaxBytes;
        set
        {
            var newValue = value ?? string.Empty;
            if (_publicExtensionMaxBytes == newValue)
            {
                return;
            }

            _publicExtensionMaxBytes = newValue;
            OnPropertyChanged();
            SavePreferences();
        }
    }
    public string AdditionalDesignSnapshotPages
    {
        get => _additionalDesignSnapshotPages;
        set
        {
            if (_additionalDesignSnapshotPages == value)
            {
                return;
            }

            _additionalDesignSnapshotPages = value ?? string.Empty;
            OnPropertyChanged();
            SavePreferences();
        }
    }
    public string DesignScreenshotBreakpointsText
    {
        get => _designScreenshotBreakpointsText;
        set
        {
            if (_designScreenshotBreakpointsText == value)
            {
                return;
            }

            _designScreenshotBreakpointsText = value ?? string.Empty;
            OnPropertyChanged();
            SavePreferences();
        }
    }
    public bool ExportPublicDesignSnapshot
    {
        get => _expPublicDesignSnapshot;
        set
        {
            if (_expPublicDesignSnapshot == value)
            {
                return;
            }

            _expPublicDesignSnapshot = value;
            OnPropertyChanged();
            SavePreferences();
        }
    }

    public bool ExportPublicDesignScreenshots
    {
        get => _expPublicDesignScreenshots;
        set
        {
            if (_expPublicDesignScreenshots == value)
            {
                return;
            }

            _expPublicDesignScreenshots = value;
            OnPropertyChanged();
            SavePreferences();
        }
    }

    public bool ExportStoreConfiguration
    {
        get => _expStoreConfiguration;
        set
        {
            if (_expStoreConfiguration == value)
            {
                return;
            }

            _expStoreConfiguration = value;
            OnPropertyChanged();
            SavePreferences();
        }
    }
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (_isRunning == value)
            {
                return;
            }

            _isRunning = value;
            OnPropertyChanged();
            RaiseRunCommandStates();
        }
    }

    public bool EnableHttpRetries
    {
        get => _enableHttpRetries;
        set
        {
            if (_enableHttpRetries == value)
            {
                return;
            }

            _enableHttpRetries = value;
            OnPropertyChanged();
            SavePreferences();
        }
    }

    public int HttpRetryAttempts
    {
        get => _httpRetryAttempts;
        set
        {
            var newValue = Math.Max(0, value);
            if (_httpRetryAttempts == newValue)
            {
                return;
            }

            _httpRetryAttempts = newValue;
            OnPropertyChanged();
            SavePreferences();
        }
    }

    public double HttpRetryBaseDelaySeconds
    {
        get => _httpRetryBaseDelaySeconds;
        set
        {
            double newValue;
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                newValue = _httpRetryBaseDelaySeconds;
            }
            else
            {
                newValue = Math.Max(0, value);
            }
            if (Math.Abs(_httpRetryBaseDelaySeconds - newValue) < 0.0001)
            {
                return;
            }

            _httpRetryBaseDelaySeconds = newValue;
            OnPropertyChanged();
            SavePreferences();
        }
    }

    public double HttpRetryMaxDelaySeconds
    {
        get => _httpRetryMaxDelaySeconds;
        set
        {
            double newValue;
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                newValue = _httpRetryMaxDelaySeconds;
            }
            else
            {
                newValue = Math.Max(0, value);
            }
            if (Math.Abs(_httpRetryMaxDelaySeconds - newValue) < 0.0001)
            {
                return;
            }

            _httpRetryMaxDelaySeconds = newValue;
            OnPropertyChanged();
            SavePreferences();
        }
    }

    public PlatformMode SelectedPlatform
    {
        get => _selectedPlatform;
        set
        {
            if (_selectedPlatform == value) return;
            _selectedPlatform = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsWooCommerce));
            OnPropertyChanged(nameof(IsShopify));
            OnPropertyChanged(nameof(StoreUrlLabel));
            ClearFilters();
        }
    }

    public bool IsWooCommerce
    {
        get => SelectedPlatform == PlatformMode.WooCommerce;
        set
        {
            if (value)
            {
                SelectedPlatform = PlatformMode.WooCommerce;
            }
        }
    }

    public bool IsShopify
    {
        get => SelectedPlatform == PlatformMode.Shopify;
        set
        {
            if (value)
            {
                SelectedPlatform = PlatformMode.Shopify;
            }
        }
    }

    public string StoreUrlLabel => SelectedPlatform == PlatformMode.WooCommerce
        ? "Store URL:"
        : "Shopify Storefront URL:";

    public string ShopifyStoreUrl { get => _shopifyStoreUrl; set { _shopifyStoreUrl = value; OnPropertyChanged(); } }
    public string ShopifyAdminAccessToken { get => _shopifyAdminAccessToken; set { _shopifyAdminAccessToken = value; OnPropertyChanged(); } }
    public string ShopifyStorefrontAccessToken { get => _shopifyStorefrontAccessToken; set { _shopifyStorefrontAccessToken = value; OnPropertyChanged(); } }
    public string ShopifyApiKey { get => _shopifyApiKey; set { _shopifyApiKey = value; OnPropertyChanged(); } }
    public string ShopifyApiSecret { get => _shopifyApiSecret; set { _shopifyApiSecret = value; OnPropertyChanged(); } }
    public string WordPressUsername
    {
        get => _wordPressUsername;
        set
        {
            _wordPressUsername = value;
            OnPropertyChanged();
            OnWordPressCredentialsChanged();
        }
    }

    public string WordPressApplicationPassword
    {
        get => _wordPressApplicationPassword;
        set
        {
            _wordPressApplicationPassword = value;
            OnPropertyChanged();
            OnWordPressCredentialsChanged();
        }
    }

    public bool HasWordPressCredentials =>
        !string.IsNullOrWhiteSpace(_wordPressUsername) &&
        !string.IsNullOrWhiteSpace(_wordPressApplicationPassword);

    public bool CanExportExtensions => HasWordPressCredentials;

    public bool CanExportPublicExtensionFootprints => !CanExportExtensions;

    private IReadOnlyList<string> GetAdditionalPublicExtensionEntryUrls()
    {
        if (string.IsNullOrWhiteSpace(AdditionalPublicExtensionPages))
        {
            return Array.Empty<string>();
        }

        var separators = new[] { '\r', '\n', ',', ';' };
        var tokens = AdditionalPublicExtensionPages
            .Split(separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim())
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return tokens;
    }

    private (int? PageLimit, long? ByteLimit) GetPublicExtensionLimits(ILogger? log)
    {
        var pageLimit = ParsePositiveInt(PublicExtensionMaxPages, out var pageInvalid);
        if (pageInvalid)
        {
            log?.Report($"Invalid public extension page limit '{PublicExtensionMaxPages}'. Treating as unlimited.");
        }

        var byteLimit = ParsePositiveLong(PublicExtensionMaxBytes, out var byteInvalid);
        if (byteInvalid)
        {
            log?.Report($"Invalid public extension byte limit '{PublicExtensionMaxBytes}'. Treating as unlimited.");
        }

        return (pageLimit, byteLimit);
    }

    private static int? ParsePositiveInt(string? text, out bool invalid)
    {
        invalid = false;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (TryParseIntWithCultures(text, out var value))
        {
            if (value < 0)
            {
                invalid = true;
                return null;
            }

            return value == 0 ? null : value;
        }

        invalid = true;
        return null;
    }

    private static long? ParsePositiveLong(string? text, out bool invalid)
    {
        invalid = false;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (TryParseLongWithCultures(text, out var value))
        {
            if (value < 0)
            {
                invalid = true;
                return null;
            }

            return value == 0 ? null : value;
        }

        invalid = true;
        return null;
    }

    private static bool TryParseIntWithCultures(string text, out int value)
    {
        const NumberStyles styles = NumberStyles.Integer | NumberStyles.AllowThousands;
        if (int.TryParse(text, styles, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        if (!ReferenceEquals(CultureInfo.InvariantCulture, CultureInfo.CurrentCulture)
            && int.TryParse(text, styles, CultureInfo.CurrentCulture, out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryParseLongWithCultures(string text, out long value)
    {
        const NumberStyles styles = NumberStyles.Integer | NumberStyles.AllowThousands;
        if (long.TryParse(text, styles, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        if (!ReferenceEquals(CultureInfo.InvariantCulture, CultureInfo.CurrentCulture)
            && long.TryParse(text, styles, CultureInfo.CurrentCulture, out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static string BuildPublicExtensionLimitNote(
        PublicExtensionDetectionSummary? summary,
        bool includeLeadingSpace = true)
    {
        if (summary is null || (!summary.PageLimitReached && !summary.ByteLimitReached))
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (summary.PageLimitReached)
        {
            parts.Add(summary.MaxPages.HasValue
                ? $"page cap of {summary.MaxPages.Value:N0} page(s)"
                : "configured page cap");
        }

        if (summary.ByteLimitReached)
        {
            parts.Add(summary.MaxBytes.HasValue
                ? $"byte cap of {FormatByteSize(summary.MaxBytes.Value)}"
                : "configured byte cap");
        }

        var joined = string.Join(" and ", parts);
        var prefix = includeLeadingSpace ? " " : string.Empty;
        return $"{prefix}Crawl stopped after {summary.ProcessedPageCount:N0} page(s) / {FormatByteSize(summary.TotalBytesDownloaded)} because the {joined} was reached.";
    }

    private static string FormatByteSize(long bytes)
    {
        const double OneKb = 1024d;
        const double OneMb = OneKb * 1024d;
        const double OneGb = OneMb * 1024d;

        if (bytes >= OneGb)
        {
            return $"{bytes / OneGb:0.##} GB";
        }

        if (bytes >= OneMb)
        {
            return $"{bytes / OneMb:0.##} MB";
        }

        if (bytes >= OneKb)
        {
            return $"{bytes / OneKb:0.##} KB";
        }

        return $"{bytes:N0} B";
    }
    private IReadOnlyList<string> GetAdditionalDesignSnapshotPageUrls()
    {
        if (string.IsNullOrWhiteSpace(AdditionalDesignSnapshotPages))
        {
            return Array.Empty<string>();
        }

        var separators = new[] { '\r', '\n', ',', ';' };
        var tokens = AdditionalDesignSnapshotPages
            .Split(separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim())
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return tokens;
    }

    private IReadOnlyList<(string Label, int Width, int Height)>? GetDesignScreenshotBreakpoints()
    {
        if (string.IsNullOrWhiteSpace(DesignScreenshotBreakpointsText))
        {
            return null;
        }

        var tokens = DesignScreenshotBreakpointsText
            .Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
        {
            return null;
        }

        var results = new List<(string Label, int Width, int Height)>(tokens.Length);

        foreach (var token in tokens)
        {
            var entry = token.Trim();
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var separatorIndex = entry.IndexOf(':');
            if (separatorIndex <= 0 || separatorIndex >= entry.Length - 1)
            {
                Append($"Invalid design screenshot breakpoint entry \"{entry}\": expected label:WIDTHxHEIGHT format.");
                continue;
            }

            var label = entry[..separatorIndex].Trim();
            if (string.IsNullOrWhiteSpace(label))
            {
                Append($"Invalid design screenshot breakpoint entry \"{entry}\": label cannot be empty.");
                continue;
            }

            var sizePart = entry[(separatorIndex + 1)..].Trim();
            var xIndex = sizePart.IndexOf('x');
            if (xIndex < 0)
            {
                xIndex = sizePart.IndexOf('X');
            }

            if (xIndex <= 0 || xIndex >= sizePart.Length - 1)
            {
                Append($"Invalid design screenshot breakpoint entry \"{entry}\": expected WIDTHxHEIGHT dimensions.");
                continue;
            }

            var widthText = sizePart[..xIndex].Trim();
            var heightText = sizePart[(xIndex + 1)..].Trim();

            if (!int.TryParse(widthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) || width <= 0)
            {
                Append($"Invalid design screenshot breakpoint entry \"{entry}\": width must be a positive integer.");
                continue;
            }

            if (!int.TryParse(heightText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) || height <= 0)
            {
                Append($"Invalid design screenshot breakpoint entry \"{entry}\": height must be a positive integer.");
                continue;
            }

            results.Add((label, width, height));
        }

        if (results.Count == 0)
        {
            Append("No valid design screenshot breakpoints were parsed; using defaults (mobile/tablet/desktop).");
            return null;
        }

        Append($"Using {results.Count} custom design screenshot breakpoint(s).");
        return results;
    }
    public bool CanExportPublicDesignSnapshot => !HasWordPressCredentials;

    public bool CanExportPublicDesignScreenshots => !HasWordPressCredentials;
    public bool CanExportStoreConfiguration => HasWordPressCredentials;
    public string TargetStoreUrl { get => _targetStoreUrl; set { _targetStoreUrl = value; OnPropertyChanged(); } }
    public string TargetConsumerKey { get => _targetConsumerKey; set { _targetConsumerKey = value; OnPropertyChanged(); } }
    public string TargetConsumerSecret { get => _targetConsumerSecret; set { _targetConsumerSecret = value; OnPropertyChanged(); } }
    public bool ImportStoreConfiguration
    {
        get => _importStoreConfiguration;
        set
        {
            if (_importStoreConfiguration == value)
            {
                return;
            }

            _importStoreConfiguration = value;
            OnPropertyChanged();
            SavePreferences();
        }
    }
    public bool CanReplicate => _lastProvisioningContext is { Products.Count: > 0 };

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
            OnPropertyChanged(nameof(TotalTokens));
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
            OnPropertyChanged(nameof(TotalTokens));
        }
    }

    public long TotalTokens => TotalPromptTokens + TotalCompletionTokens;

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

    public string ChatApiEndpoint
    {
        get => _chatApiEndpoint;
        set
        {
            var newValue = value?.Trim() ?? string.Empty;
            if (_chatApiEndpoint == newValue)
            {
                return;
            }

            _chatApiEndpoint = newValue;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasChatConfiguration));
            SavePreferences();
            SendChatCommand.RaiseCanExecuteChanged();
            LaunchWizardCommand?.RaiseCanExecuteChanged();
            UpdateChatConfigurationStatus();
        }
    }

    public string ChatModel
    {
        get => _chatModel;
        set
        {
            var newValue = value?.Trim() ?? string.Empty;
            if (_chatModel == newValue)
            {
                return;
            }

            _chatModel = newValue;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasChatConfiguration));
            SavePreferences();
            SendChatCommand.RaiseCanExecuteChanged();
            LaunchWizardCommand?.RaiseCanExecuteChanged();
            UpdateChatConfigurationStatus();
        }
    }

    public string ChatSystemPrompt
    {
        get => _chatSystemPrompt;
        set
        {
            var newValue = value ?? string.Empty;
            if (_chatSystemPrompt == newValue)
            {
                return;
            }

            _chatSystemPrompt = newValue;
            OnPropertyChanged();
            SavePreferences();
        }
    }

    public string ChatApiKey
    {
        get => _chatApiKey;
        set
        {
            var newValue = value?.Trim() ?? string.Empty;
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
            LaunchWizardCommand?.RaiseCanExecuteChanged();
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

    public bool HasIndexedDatasets => _artifactIndexingService.HasAnyIndexedArtifacts;

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
            CancelChatCommand?.RaiseCanExecuteChanged();
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
            SavePreferences();
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
            SavePreferences();
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
            SavePreferences();
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
            SavePreferences();
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
            SavePreferences();
        }
    }

    // Selectable terms for filters
    public ObservableCollection<SelectableTerm> CategoryChoices { get; } = new();
    public ObservableCollection<SelectableTerm> TagChoices { get; } = new();

    public ObservableCollection<string> Logs { get; } = new();
    public ObservableCollection<LogTriageIssue> LogSummaryIssues { get; } = new();

    public LogTriageResult? LatestLogSummary
    {
        get => _latestLogSummary;
        private set
        {
            if (Equals(_latestLogSummary, value))
            {
                return;
            }

            _latestLogSummary = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LatestLogSummaryStatus));
        }
    }

    public string LatestLogSummaryOverview
    {
        get => _latestLogSummaryOverview;
        private set
        {
            if (_latestLogSummaryOverview == value)
            {
                return;
            }

            _latestLogSummaryOverview = value;
            OnPropertyChanged();
        }
    }

    public string ManualRunGoals
    {
        get => _manualRunGoals;
        set
        {
            var newValue = value ?? string.Empty;
            if (_manualRunGoals == newValue)
            {
                return;
            }

            _manualRunGoals = newValue;
            OnPropertyChanged();
        }
    }

    public string LatestRunSnapshotJson
    {
        get => _latestRunSnapshotJson;
        private set
        {
            if (_latestRunSnapshotJson == value)
            {
                return;
            }

            _latestRunSnapshotJson = value;
            OnPropertyChanged();
        }
    }

    public string LatestRunAiNarrative
    {
        get => _latestRunAiNarrative;
        private set
        {
            if (_latestRunAiNarrative == value)
            {
                return;
            }

            _latestRunAiNarrative = value;
            OnPropertyChanged();
        }
    }

    public string? LatestRunAiBriefPath
    {
        get => _latestRunAiBriefPath;
        private set
        {
            if (_latestRunAiBriefPath == value)
            {
                return;
            }

            _latestRunAiBriefPath = value;
            OnPropertyChanged();
        }
    }

    public string LatestRunAiRecommendationSummary
    {
        get => _latestRunAiRecommendationSummary;
        private set
        {
            if (_latestRunAiRecommendationSummary == value)
            {
                return;
            }

            _latestRunAiRecommendationSummary = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasAiRecommendations));
        }
    }

    public ObservableCollection<AiRecommendation> LatestRunAiRecommendations { get; } = new();

    public bool HasAiRecommendations => LatestRunAiRecommendations.Count > 0 || !string.IsNullOrWhiteSpace(LatestRunAiRecommendationSummary);

    public AiArtifactAnnotation? LatestRunAiAnnotation
    {
        get => _latestRunAiAnnotation;
        private set
        {
            if (Equals(_latestRunAiAnnotation, value))
            {
                return;
            }

            _latestRunAiAnnotation = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LatestRunAiAnnotationTimestamp));
        }
    }

    public DateTimeOffset? LatestRunAiAnnotationTimestamp => LatestRunAiAnnotation?.GeneratedAt;

    public ObservableCollection<AutomationScriptDisplay> LatestAutomationScripts => _latestAutomationScripts;

    public ObservableCollection<string> LatestAutomationScriptWarnings => _latestAutomationScriptWarnings;

    public string LatestAutomationScriptSummary
    {
        get => _latestAutomationScriptSummary;
        private set
        {
            if (_latestAutomationScriptSummary == value)
            {
                return;
            }

            _latestAutomationScriptSummary = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasAutomationScriptSummary));
        }
    }

    public string LatestAutomationScriptError
    {
        get => _latestAutomationScriptError;
        private set
        {
            if (_latestAutomationScriptError == value)
            {
                return;
            }

            _latestAutomationScriptError = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasAutomationScriptError));
        }
    }

    public bool HasAutomationScripts => LatestAutomationScripts.Count > 0;

    public bool HasAutomationScriptWarnings => LatestAutomationScriptWarnings.Count > 0;

    public bool HasAutomationScriptError => !string.IsNullOrWhiteSpace(LatestAutomationScriptError);

    public bool HasAutomationScriptSummary => !string.IsNullOrWhiteSpace(LatestAutomationScriptSummary);

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

    public string? LatestLogSummaryStatus => BuildLogSummaryStatus(LatestLogSummary);

    public bool IsLogSummaryBusy
    {
        get => _isLogSummaryBusy;
        private set
        {
            if (_isLogSummaryBusy == value)
            {
                return;
            }

            _isLogSummaryBusy = value;
            OnPropertyChanged();
        }
    }

    public RelayCommand BrowseCommand { get; }
    public RelayCommand RunCommand { get; }
    public RelayCommand CancelRunCommand { get; }
    public RelayCommand SelectAllCategoriesCommand { get; }
    public RelayCommand ClearCategoriesCommand { get; }
    public RelayCommand SelectAllTagsCommand { get; }
    public RelayCommand ClearTagsCommand { get; }
    public ReadOnlyObservableCollection<RunPlan> RunPlans => _runPlans;
    public RelayCommand<RunPlan> ApproveRunPlanCommand { get; }
    public RelayCommand<RunPlan> DismissRunPlanCommand { get; }
    public bool HasRunPlans => RunPlans.Count > 0;
    public RelayCommand ExportCollectionsCommand { get; }
    public RelayCommand ReplicateCommand { get; }
    public RelayCommand OpenLogCommand { get; }
    public RelayCommand ExplainLogsCommand { get; }
    public RelayCommand LaunchWizardCommand { get; }
    public ICommand UseAiRecommendationCommand { get; }
    public ICommand CopyAutomationScriptCommand { get; }

    private void OnBrowse()
    {
        var chosen = _dialogs.BrowseForFolder(OutputFolder);
        if (!string.IsNullOrWhiteSpace(chosen))
            OutputFolder = chosen;
    }

    private bool CanLaunchWizard() => HasChatConfiguration;

    private void OnLaunchWizard()
    {
        try
        {
            var result = _dialogs.ShowOnboardingWizard(this, _chatAssistantService);
            if (result is null)
            {
                return;
            }

            ApplyWizardSettings(result);
        }
        catch (Exception ex)
        {
            Append($"AI Setup Wizard failed: {ex.Message}");
        }
    }

    public void ApplyWizardSettings(OnboardingWizardSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var applied = settings.EnsureValid();

        ExportCsv = applied.ExportCsv;
        ExportShopify = applied.ExportShopify;
        ExportWoo = applied.ExportWoo;
        ExportReviews = applied.ExportReviews;
        ExportXlsx = applied.ExportXlsx;
        ExportJsonl = applied.ExportJsonl;
        ExportPluginsCsv = applied.ExportPluginsCsv;
        ExportPluginsJsonl = applied.ExportPluginsJsonl;
        ExportThemesCsv = applied.ExportThemesCsv;
        ExportThemesJsonl = applied.ExportThemesJsonl;
        ExportPublicExtensionFootprints = applied.ExportPublicExtensionFootprints;
        ExportPublicDesignSnapshot = applied.ExportPublicDesignSnapshot;
        ExportPublicDesignScreenshots = applied.ExportPublicDesignScreenshots;
        ExportStoreConfiguration = applied.ExportStoreConfiguration;
        ImportStoreConfiguration = applied.ImportStoreConfiguration;

        EnableHttpRetries = applied.EnableHttpRetries;
        HttpRetryAttempts = applied.HttpRetryAttempts;
        HttpRetryBaseDelaySeconds = applied.HttpRetryBaseDelaySeconds;
        HttpRetryMaxDelaySeconds = applied.HttpRetryMaxDelaySeconds;

        if (!string.IsNullOrWhiteSpace(applied.WordPressUsernamePlaceholder))
        {
            WordPressUsername = applied.WordPressUsernamePlaceholder;
        }

        if (!string.IsNullOrWhiteSpace(applied.WordPressApplicationPasswordPlaceholder))
        {
            WordPressApplicationPassword = applied.WordPressApplicationPasswordPlaceholder;
        }

        if (!string.IsNullOrWhiteSpace(applied.ShopifyStoreUrlPlaceholder))
        {
            ShopifyStoreUrl = applied.ShopifyStoreUrlPlaceholder;
        }

        if (!string.IsNullOrWhiteSpace(applied.ShopifyAdminAccessTokenPlaceholder))
        {
            ShopifyAdminAccessToken = applied.ShopifyAdminAccessTokenPlaceholder;
        }

        if (!string.IsNullOrWhiteSpace(applied.ShopifyStorefrontAccessTokenPlaceholder))
        {
            ShopifyStorefrontAccessToken = applied.ShopifyStorefrontAccessTokenPlaceholder;
        }

        if (!string.IsNullOrWhiteSpace(applied.ShopifyApiKeyPlaceholder))
        {
            ShopifyApiKey = applied.ShopifyApiKeyPlaceholder;
        }

        if (!string.IsNullOrWhiteSpace(applied.ShopifyApiSecretPlaceholder))
        {
            ShopifyApiSecret = applied.ShopifyApiSecretPlaceholder;
        }

        var summary = string.IsNullOrWhiteSpace(applied.Summary)
            ? BuildWizardSummary(applied)
            : applied.Summary!.Trim();

        Append($"AI Setup Wizard applied configuration:\n{summary}");
    }

    private static string BuildWizardSummary(OnboardingWizardSettings settings)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Exports:");
        builder.AppendLine($"- Generic CSV: {settings.ExportCsv}");
        builder.AppendLine($"- Shopify CSV: {settings.ExportShopify}");
        builder.AppendLine($"- WooCommerce CSV: {settings.ExportWoo}");
        builder.AppendLine($"- Reviews CSV: {settings.ExportReviews}");
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
        builder.AppendLine("Retry strategy:");
        builder.AppendLine($"- Enabled: {settings.EnableHttpRetries}");
        builder.AppendLine($"- Attempts: {settings.HttpRetryAttempts}");
        builder.AppendLine($"- Base delay (s): {settings.HttpRetryBaseDelaySeconds:0.###}");
        builder.AppendLine($"- Max delay (s): {settings.HttpRetryMaxDelaySeconds:0.###}");
        return builder.ToString();
    }

    private void OnOpenLog()
    {
        _dialogs.ShowLogWindow(this);
    }

    private bool CanSendChat()
        => !IsChatBusy
            && HasChatConfiguration
            && !string.IsNullOrWhiteSpace(ChatInput);

    private bool CanCancelChat()
        => IsChatBusy;

    private bool CanSaveChatTranscript()
        => HasChatMessages;

    private bool CanClearChatHistory()
        => HasChatMessages;

    private void OnCancelChat()
    {
        var cts = _chatCts;
        if (cts is null || !IsChatBusy)
        {
            return;
        }

        ChatStatusMessage = "Cancelling assistant response…";

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
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
            var defaultFileName = Path.GetFileName(_chatTranscriptStore.CurrentMarkdownPath);
            var filter = "Markdown (*.md)|*.md|JSON Lines (*.jsonl)|*.jsonl|All files (*.*)|*.*";
            var target = _dialogs.SaveFile(filter, defaultFileName, _chatTranscriptStore.TranscriptDirectory);
            if (string.IsNullOrWhiteSpace(target))
            {
                return;
            }

            var format = string.Equals(Path.GetExtension(target), ".jsonl", StringComparison.OrdinalIgnoreCase)
                ? ChatTranscriptFormat.Jsonl
                : ChatTranscriptFormat.Markdown;

            await _chatTranscriptStore.SaveTranscriptAsync(target, format).ConfigureAwait(false);
            Append($"Saved chat transcript to {target}");
        }
        catch (Exception ex)
        {
            Append($"Unable to save chat transcript: {ex.Message}");
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
            Append($"Unable to reset chat transcript: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    internal void OnChatUsageReported(ChatUsageSnapshot usage)
    {
        if (usage is null)
        {
            return;
        }

        ExecuteOnUiThread(() =>
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

        ExecuteOnUiThread(() =>
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

    private bool CanExplainLogs()
        => !IsLogSummaryBusy
            && HasChatConfiguration
            && Logs.Count > 0;

    private void OnUseAiRecommendation(string? prompt)
    {
        var trimmed = prompt?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return;
        }

        ExecuteOnUiThread(() =>
        {
            ChatInput = trimmed;
            IsAssistantPanelExpanded = true;
        });
    }

    private void OnCopyAutomationScript(AutomationScriptDisplay? script)
    {
        if (script is null || string.IsNullOrWhiteSpace(script.Content))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(script.Content);
        }
        catch (Exception ex)
        {
            Append($"Unable to copy script to clipboard: {ex.Message}");
        }
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
                DiagnosticLogger: Append,
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
                    () => LatestRunSnapshotJson,
                    limit =>
                    {
                        try
                        {
                            return CollectRecentExportFiles(limit);
                        }
                        catch (Exception ex)
                        {
                            Append($"Assistant export file lookup failed: {ex.Message}");
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
                            Append($"Assistant log retrieval failed: {ex.Message}");
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
                ChatStatusMessage = SelectedChatMode == ChatInteractionMode.DatasetQuestion
                    ? AppendBudgetReminder("Dataset Q&A ready for another question.")
                    : AppendBudgetReminder("Assistant ready for another prompt.");
            }

            if (SelectedChatMode != ChatInteractionMode.DatasetQuestion && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var directives = await _chatAssistantService.ParseAssistantDirectivesAsync(session, context, trimmed, cancellationToken);
                    if (directives is not null)
                    {
                        ProcessAssistantDirectives(directives, confirmed: false);
                    }
                }
                catch (OperationCanceledException)
                {
                    wasCancelled = true;
                    throw;
                }
                catch (Exception directiveEx)
                {
                    Append($"Assistant directive processing failed: {directiveEx.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
            ChatStatusMessage = "Assistant response cancelled.";
            if (string.IsNullOrWhiteSpace(assistantMessage.Content))
            {
                assistantMessage.Content = "(Response cancelled.)";
            }
        }
        catch (Exception ex)
        {
            var guidance = ChatAssistantService.CreateErrorGuidance(ex);
            var guidanceMessage = guidance.ToMessage();
            assistantMessage.Content = guidanceMessage;
            ChatStatusMessage = guidanceMessage;
            Append($"Assistant error: {ex}");
        }
        finally
        {
            wasCancelled |= cancellationToken.IsCancellationRequested;
            if (ReferenceEquals(_chatCts, chatCts))
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

    private async Task TryAppendTranscriptAsync(ChatMessage message)
    {
        try
        {
            await _chatTranscriptStore.AppendAsync(message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Append($"Unable to write chat transcript: {ex.Message}");
        }
    }

    private Task EnsureChatTranscriptLoadedAsync()
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

                ExecuteOnUiThread(() =>
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
                Append($"Unable to load chat transcript: {ex.Message}");
            }
        });
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
                Append("No pending assistant directives to apply.");
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
                Append("No pending assistant directives to discard.");
            }
            else
            {
                _pendingAssistantDirectives = null;
                Append("Pending assistant directives discarded.");
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

        Append($"Assistant directive summary: {directives.Summary}");

        if (!string.IsNullOrWhiteSpace(directives.RiskNote))
        {
            Append($"Risk note: {directives.RiskNote}");
        }

        foreach (var reminder in directives.CredentialReminders)
        {
            Append($"Assistant credential reminder ({reminder.Credential}): {reminder.Message}");
        }

        var requiresConfirmation = !confirmed && ShouldDeferAssistantDirective(directives);
        if (requiresConfirmation)
        {
            _pendingAssistantDirectives = directives;
            Append("Assistant directives require confirmation before applying.");
            foreach (var toggle in directives.Toggles)
            {
                Append($"  Preview toggle {toggle.Name} -> {(toggle.Value ? "enabled" : "disabled")} ({FormatTogglePreviewMetadata(toggle)})");
                if (!string.IsNullOrWhiteSpace(toggle.Justification))
                {
                    Append($"    Reason: {toggle.Justification}");
                }
            }

            if (directives.Retry is not null)
            {
                Append($"  Preview retry: {DescribeRetryDirective(directives.Retry)}");
                if (!string.IsNullOrWhiteSpace(directives.Retry.Justification))
                {
                    Append($"    Reason: {directives.Retry.Justification}");
                }
            }

            foreach (var action in directives.Actions)
            {
                Append($"  Pending action {action.Name} ({FormatActionPreviewMetadata(action)})");
                if (!string.IsNullOrWhiteSpace(action.Justification))
                {
                    Append($"    Reason: {action.Justification}");
                }
            }

            Append($"Type {ApplyAssistantDirectivesCommand} to apply or {DiscardAssistantDirectivesCommand} to cancel.");
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
            Append("No configuration changes were applied.");
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
            return "risk unspecified";
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

        if (parts.Count == 0)
        {
            return "no additional metadata";
        }

        return string.Join(", ", parts);
    }

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

    private void ApplyAssistantToggle(AssistantToggleDirective toggle)
    {
        if (!_assistantToggleBindings.TryGetValue(toggle.Name, out var binding))
        {
            Append($"Assistant directive skipped unknown toggle \"{toggle.Name}\".");
            return;
        }

        var desired = toggle.Value;
        var current = binding.Getter();
        var label = desired ? "enabled" : "disabled";

        if (current == desired)
        {
            Append($"Assistant directive left {toggle.Name} unchanged (already {label}).");
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

        Append(messageBuilder.ToString());

        if (!string.IsNullOrWhiteSpace(toggle.Justification))
        {
            Append($"Reason: {toggle.Justification}");
        }
    }

    private void ApplyAssistantRetryDirective(AssistantRetryDirective directive)
    {
        var updates = new List<string>();
        var baseDelaySeconds = directive.BaseDelaySeconds;

        if (directive.Enable is bool enable)
        {
            if (EnableHttpRetries != enable)
            {
                EnableHttpRetries = enable;
                updates.Add($"enable={enable.ToString().ToLowerInvariant()}");
            }
            else
            {
                Append($"Assistant directive left HTTP retries {(enable ? "enabled" : "disabled")} (already set).");
            }
        }

        if (directive.Attempts is int attempts)
        {
            if (attempts < 0 || attempts > 10)
            {
                Append($"Assistant directive skipped invalid retry attempt count ({attempts}). Expected 0-10.");
            }
            else if (HttpRetryAttempts != attempts)
            {
                HttpRetryAttempts = attempts;
                updates.Add($"attempts={attempts}");
            }
            else
            {
                Append($"Assistant directive left HTTP retry attempts at {attempts} (already set).");
            }
        }

        if (baseDelaySeconds is double baseDelay)
        {
            if (!IsDelayWithinRange(baseDelay))
            {
                Append($"Assistant directive skipped invalid retry base delay ({FormatSeconds(baseDelay)}). Expected between 0 and 600 seconds.");
            }
            else if (Math.Abs(HttpRetryBaseDelaySeconds - baseDelay) > 0.0001)
            {
                HttpRetryBaseDelaySeconds = baseDelay;
                updates.Add($"base_delay={FormatSeconds(baseDelay)}");
            }
            else
            {
                Append($"Assistant directive left HTTP retry base delay at {FormatSeconds(baseDelay)} (already set).");
            }
        }

        if (directive.MaxDelaySeconds is double maxDelay)
        {
            if (!IsDelayWithinRange(maxDelay))
            {
                Append($"Assistant directive skipped invalid retry max delay ({FormatSeconds(maxDelay)}). Expected between 0 and 600 seconds.");
            }
            else if (baseDelaySeconds is double baseDelayForComparison && maxDelay < baseDelayForComparison)
            {
                Append($"Assistant directive skipped retry max delay ({FormatSeconds(maxDelay)}) because it is less than the base delay ({FormatSeconds(baseDelayForComparison)}).");
            }
            else if (Math.Abs(HttpRetryMaxDelaySeconds - maxDelay) > 0.0001)
            {
                HttpRetryMaxDelaySeconds = maxDelay;
                updates.Add($"max_delay={FormatSeconds(maxDelay)}");
            }
            else
            {
                Append($"Assistant directive left HTTP retry max delay at {FormatSeconds(maxDelay)} (already set).");
            }
        }

        if (updates.Count > 0)
        {
            Append("Assistant updated HTTP retry settings: " + string.Join(", ", updates));
        }

        if (!string.IsNullOrWhiteSpace(directive.Justification))
        {
            Append("Retry directive justification: " + directive.Justification);
        }
    }

    private void ApplyAssistantAction(AssistantActionDirective action)
    {
        if (string.IsNullOrWhiteSpace(action.Name))
        {
            Append("Assistant action skipped: action name was not provided.");
            return;
        }

        var normalizedName = action.Name.Trim();
        Append($"Assistant action requested: {normalizedName}.");
        if (!string.IsNullOrWhiteSpace(action.Justification))
        {
            Append($"Reason: {action.Justification}");
        }

        var loweredName = normalizedName.ToLowerInvariant();

        switch (loweredName)
        {
            case "start_run":
                if (IsRunning)
                {
                    Append("Assistant action start_run skipped: a run is already in progress.");
                    return;
                }

                if (!RunCommand.CanExecute(null))
                {
                    Append("Assistant action start_run skipped: run command is unavailable.");
                    return;
                }

                if (!ConfirmAssistantAction("The assistant wants to start a new migration run. Start the run now?", action.Justification))
                {
                    Append("Assistant action start_run canceled by operator.");
                    return;
                }

                Append("Assistant action start_run executed: run command invoked.");
                RunCommand.Execute(null);
                return;

            case "open_output_folder":
                if (action.RequiresConfirmation && !ConfirmAssistantAction("Open the configured output folder?", action.Justification))
                {
                    Append("Assistant action open_output_folder canceled by operator.");
                    return;
                }

                TryOpenAssistantPath(normalizedName, ResolveBaseOutputFolder());
                return;

            case "open_run_folder":
                if (action.RequiresConfirmation && !ConfirmAssistantAction("Open the most recent run folder?", action.Justification))
                {
                    Append("Assistant action open_run_folder canceled by operator.");
                    return;
                }

                TryOpenAssistantPath(normalizedName, _latestStoreOutputFolder);
                return;

            case "open_manual_bundle":
                if (action.RequiresConfirmation && !ConfirmAssistantAction("Open the latest manual bundle archive?", action.Justification))
                {
                    Append("Assistant action open_manual_bundle canceled by operator.");
                    return;
                }

                TryOpenAssistantPath(normalizedName, _latestManualBundlePath);
                return;

            case "open_manual_report":
                if (action.RequiresConfirmation && !ConfirmAssistantAction("Open the latest manual migration report?", action.Justification))
                {
                    Append("Assistant action open_manual_report canceled by operator.");
                    return;
                }

                TryOpenAssistantPath(normalizedName, _latestManualReportPath);
                return;

            case "open_run_delta":
                if (action.RequiresConfirmation && !ConfirmAssistantAction("Open the latest run delta summary?", action.Justification))
                {
                    Append("Assistant action open_run_delta canceled by operator.");
                    return;
                }

                TryOpenAssistantPath(normalizedName, _latestRunDeltaPath);
                return;

            case "open_ai_brief":
                if (action.RequiresConfirmation && !ConfirmAssistantAction("Open the latest AI migration brief?", action.Justification))
                {
                    Append("Assistant action open_ai_brief canceled by operator.");
                    return;
                }

                TryOpenAssistantPath(normalizedName, LatestRunAiBriefPath);
                return;
            case "schedule_run":
                if (action.Plan is not RunPlan plan)
                {
                    Append("Assistant action schedule_run skipped: plan details were missing.");
                    return;
                }

                if (action.RequiresConfirmation && !ConfirmAssistantAction(
                        BuildRunPlanConfirmationPrompt(plan),
                        action.Justification))
                {
                    Append("Assistant action schedule_run canceled by operator.");
                    return;
                }

                _runPlanner.Enqueue(plan);
                Append($"Assistant action schedule_run executed: plan \"{plan.Name}\" queued.");
                return;
        }

        if (action.RequiresConfirmation && !ConfirmAssistantAction($"The assistant requested action \"{normalizedName}\". Continue?", action.Justification))
        {
            Append($"Assistant action {normalizedName} canceled by operator.");
            return;
        }

        Append($"Assistant action {normalizedName} is not recognized. No changes applied.");
    }

    private static string BuildRunPlanConfirmationPrompt(RunPlan plan)
    {
        if (plan.ExecutionMode == RunPlanExecutionMode.Scheduled && plan.ScheduledForUtc is { } scheduled)
        {
            return $"Queue remediation plan \"{plan.Name}\" for {scheduled:u}?";
        }

        return $"Queue remediation plan \"{plan.Name}\" for approval?";
    }

    private void ApplyRunPlanOverrides(RunPlan plan)
    {
        foreach (var setting in plan.Settings)
        {
            if (!string.IsNullOrWhiteSpace(setting.Description))
            {
                Append($"Run plan note for {setting.Name}: {setting.Description}");
            }

            switch (setting.ValueKind)
            {
                case RunPlanSettingValueKind.Boolean when setting.BooleanValue is bool booleanValue:
                    if (_assistantToggleBindings.TryGetValue(setting.Name, out var binding))
                    {
                        var current = binding.Getter();
                        if (current == booleanValue)
                        {
                            Append($"Run plan left {setting.Name} unchanged (already {(booleanValue ? "enabled" : "disabled")}).");
                        }
                        else
                        {
                            binding.Setter(booleanValue);
                            Append($"Run plan set {setting.Name} to {(booleanValue ? "enabled" : "disabled") }.");
                        }
                    }
                    else
                    {
                        Append($"Run plan override skipped: toggle {setting.Name} is not recognized.");
                    }

                    break;
                case RunPlanSettingValueKind.Number when setting.NumberValue is double numericValue:
                    ApplyRunPlanNumericOverride(setting.Name, numericValue);
                    break;
                case RunPlanSettingValueKind.Text when !string.IsNullOrWhiteSpace(setting.TextValue):
                    ApplyRunPlanTextOverride(setting.Name, setting.TextValue!);
                    break;
                default:
                    Append($"Run plan override skipped: {setting.Name} value could not be interpreted.");
                    break;
            }
        }
    }

    private void ApplyRunPlanNumericOverride(string name, double value)
    {
        switch (name)
        {
            case nameof(HttpRetryAttempts):
                var attempts = (int)Math.Round(value);
                if (attempts < 0 || attempts > 10)
                {
                    Append($"Run plan override skipped: {name} value {attempts} is outside the expected range (0-10).");
                    return;
                }

                if (HttpRetryAttempts == attempts)
                {
                    Append($"Run plan left {name} at {attempts} (already set).");
                }
                else
                {
                    HttpRetryAttempts = attempts;
                    Append($"Run plan set {name} to {attempts}.");
                }

                break;
            case nameof(HttpRetryBaseDelaySeconds):
                if (value < 0 || value > 600)
                {
                    Append($"Run plan override skipped: {name} value {FormatSeconds(value)} is outside the expected range (0-600 seconds).");
                    return;
                }

                if (Math.Abs(HttpRetryBaseDelaySeconds - value) < 0.0001)
                {
                    Append($"Run plan left {name} at {FormatSeconds(value)} (already set).");
                }
                else
                {
                    HttpRetryBaseDelaySeconds = value;
                    Append($"Run plan set {name} to {FormatSeconds(value)}.");
                }

                break;
            case nameof(HttpRetryMaxDelaySeconds):
                if (value < 0 || value > 600)
                {
                    Append($"Run plan override skipped: {name} value {FormatSeconds(value)} is outside the expected range (0-600 seconds).");
                    return;
                }

                if (Math.Abs(HttpRetryMaxDelaySeconds - value) < 0.0001)
                {
                    Append($"Run plan left {name} at {FormatSeconds(value)} (already set).");
                }
                else
                {
                    HttpRetryMaxDelaySeconds = value;
                    Append($"Run plan set {name} to {FormatSeconds(value)}.");
                }

                break;
            default:
                Append($"Run plan override skipped: numeric setting {name} is not supported.");
                break;
        }
    }

    private void ApplyRunPlanTextOverride(string name, string value)
    {
        switch (name)
        {
            case nameof(ManualRunGoals):
                ManualRunGoals = value;
                Append("Run plan updated manual run goals.");
                break;
            default:
                Append($"Run plan override skipped: text setting {name} is not supported.");
                break;
        }
    }

    private void OnRunPlansChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(HasRunPlans));

    private void OnRunPlanQueued(object? sender, RunPlan plan)
    {
        if (plan is null)
        {
            return;
        }

        plan.PropertyChanged += OnRunPlanPropertyChanged;
        var scheduleText = plan.ExecutionMode == RunPlanExecutionMode.Scheduled && plan.ScheduledForUtc is { } scheduled
            ? $"scheduled for {scheduled:u}"
            : "awaiting approval";
        Append($"Assistant queued remediation plan \"{plan.Name}\" ({scheduleText}).");

        if (!string.IsNullOrWhiteSpace(plan.DirectiveSummary))
        {
            Append($"Summary: {plan.DirectiveSummary}");
        }

        if (plan.Settings.Count > 0)
        {
            var overrideSummary = string.Join(", ", plan.Settings.Select(setting => $"{setting.Name}={setting.DisplayValue}"));
            Append("Overrides: " + overrideSummary);
        }

        foreach (var note in plan.PrerequisiteNotes)
        {
            Append($"Prerequisite: {note}");
        }

        ApproveRunPlanCommand.RaiseCanExecuteChanged();
        DismissRunPlanCommand.RaiseCanExecuteChanged();
    }

    private void OnRunPlanExecutionCompleted(object? sender, RunPlanExecutionEventArgs e)
    {
        if (e.Plan is null)
        {
            return;
        }

        var status = e.Outcome.Cancelled
            ? "was cancelled"
            : e.Outcome.Success
                ? "completed"
                : "finished with issues";
        Append($"Run plan \"{e.Plan.Name}\" {status}.");
        if (!string.IsNullOrWhiteSpace(e.Outcome.Message))
        {
            Append($"Plan result: {e.Outcome.Message}");
        }

        ApproveRunPlanCommand.RaiseCanExecuteChanged();
        DismissRunPlanCommand.RaiseCanExecuteChanged();
    }

    private void OnRunPlanPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(RunPlan.Status), StringComparison.Ordinal))
        {
            return;
        }

        ApproveRunPlanCommand.RaiseCanExecuteChanged();
        DismissRunPlanCommand.RaiseCanExecuteChanged();
    }

    private bool CanApproveRunPlan(RunPlan? plan)
        => plan is not null && (plan.Status == RunPlanStatus.Pending || plan.Status == RunPlanStatus.Scheduled);

    private void OnApproveRunPlan(RunPlan? plan)
    {
        if (plan is null)
        {
            return;
        }

        if (_runPlanner.TryApprove(plan))
        {
            Append($"Operator approved run plan \"{plan.Name}\".");
            return;
        }

        Append($"Run plan \"{plan.Name}\" could not be approved (already in progress or completed).");
    }

    private bool CanDismissRunPlan(RunPlan? plan)
        => plan is not null && (plan.Status == RunPlanStatus.Pending || plan.Status == RunPlanStatus.Scheduled);

    private void OnDismissRunPlan(RunPlan? plan)
    {
        if (plan is null)
        {
            return;
        }

        if (_runPlanner.TryCancel(plan, "Dismissed by operator."))
        {
            Append($"Run plan \"{plan.Name}\" dismissed by operator.");
        }
        else
        {
            Append($"Run plan \"{plan.Name}\" could not be dismissed (already executing or finalized).");
        }
    }

    private void TryOpenAssistantPath(string actionName, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Append($"Assistant action {actionName} skipped: no path available.");
            return;
        }

        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                Append($"Assistant action {actionName} skipped: path not found ({path}).");
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            };
            Process.Start(startInfo);
            Append($"Assistant action {actionName} opened {path}.");
        }
        catch (Exception ex)
        {
            Append($"Assistant action {actionName} failed to open {path}: {ex.Message}");
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

        if (System.Windows.Application.Current?.Dispatcher is System.Windows.Threading.Dispatcher dispatcher)
        {
            if (dispatcher.CheckAccess())
            {
                return System.Windows.MessageBox.Show(text, "Confirm assistant action", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            }

            return dispatcher.Invoke(() => System.Windows.MessageBox.Show(text, "Confirm assistant action", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
        }

        return System.Windows.MessageBox.Show(text, "Confirm assistant action", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private static bool IsDelayWithinRange(double value)
        => value >= 0 && value <= 600;

    private static string FormatSeconds(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture) + "s";

    private void UpdateAiRecommendations(AiArtifactAnnotation? annotation)
    {
        ExecuteOnUiThread(() =>
        {
            LatestRunAiAnnotation = annotation;
            const string privacyNote = "Sensitive identifiers from indexed exports are masked before assistant analysis.";
            var normalizedSummary = annotation?.MarkdownSummary?.Trim();
            LatestRunAiRecommendationSummary = string.IsNullOrWhiteSpace(normalizedSummary)
                ? privacyNote
                : string.Concat(privacyNote, Environment.NewLine, Environment.NewLine, normalizedSummary);
            LatestRunAiRecommendations.Clear();

            if (annotation?.Recommendations is { Count: > 0 })
            {
                foreach (var recommendation in annotation.Recommendations)
                {
                    LatestRunAiRecommendations.Add(recommendation);
                }
            }

            OnPropertyChanged(nameof(HasAiRecommendations));
        });
    }

    private async Task OnExplainLatestLogsAsync()
    {
        var snapshot = CaptureRecentLogs();
        if (snapshot.Count == 0)
        {
            return;
        }

        await SummarizeLogsAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
    }

    private void OnExportCollections()
    {
        var baseFolder = ResolveBaseOutputFolder();
        Directory.CreateDirectory(baseFolder);

        var targetUrl = SelectedPlatform == PlatformMode.WooCommerce ? StoreUrl : ShopifyStoreUrl;
        var storeId = BuildStoreIdentifier(targetUrl);
        var storeFolder = Path.Combine(baseFolder, storeId);
        Directory.CreateDirectory(storeFolder);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");

        var categories = CategoryChoices
            .Select(BuildCollectionExportRow)
            .ToList();

        var collectionsPath = Path.Combine(storeFolder, $"{storeId}_{timestamp}_collections.xlsx");
        XlsxExporter.Write(collectionsPath, categories);
        Append($"Wrote {collectionsPath}");

        if (TagChoices.Count > 0)
        {
            var tagDicts = TagChoices
                .Select(choice => choice.Term)
                .Select(term => new Dictionary<string, object?>
                {
                    ["id"] = term.Id,
                    ["name"] = term.Name,
                    ["slug"] = term.Slug
                })
                .ToList();

            var tagsPath = Path.Combine(storeFolder, $"{storeId}_{timestamp}_tags.xlsx");
            XlsxExporter.Write(tagsPath, tagDicts);
            Append($"Wrote {tagsPath}");
        }
    }

    private ChatSessionContext BuildChatContext()
    {
        var datasets = _artifactIndexingService.GetIndexedDatasets();
        var datasetNames = datasets.Select(d => d.Name).ToList();

        return new ChatSessionContext(
            SelectedPlatform,
            ExportCsv,
            ExportShopify,
            ExportWoo,
            ExportReviews,
            ExportXlsx,
            ExportJsonl,
            ExportPluginsCsv,
            ExportPluginsJsonl,
            ExportThemesCsv,
            ExportThemesJsonl,
            ExportPublicExtensionFootprints,
            ExportPublicDesignSnapshot,
            ExportPublicDesignScreenshots,
            ExportStoreConfiguration,
            ImportStoreConfiguration,
            HasWordPressCredentials,
            HasShopifyCredentials(),
            HasTargetCredentials(),
            EnableHttpRetries,
            HttpRetryAttempts,
            AdditionalPublicExtensionPages,
            AdditionalDesignSnapshotPages,
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
            if (!Directory.Exists(OutputFolder))
            {
                return Array.Empty<ChatAssistantToolbox.ExportFileSummary>();
            }

            var files = Directory
                .EnumerateFiles(OutputFolder, "*", SearchOption.AllDirectories)
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
        if (limit <= 0 || Logs.Count == 0)
        {
            return Array.Empty<string>();
        }

        var start = Math.Max(0, Logs.Count - limit);
        return Logs.Skip(start).Take(limit).ToArray();
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

    private bool HasShopifyCredentials()
        => !string.IsNullOrWhiteSpace(ShopifyAdminAccessToken)
            || !string.IsNullOrWhiteSpace(ShopifyStorefrontAccessToken)
            || !string.IsNullOrWhiteSpace(ShopifyApiKey)
            || !string.IsNullOrWhiteSpace(ShopifyApiSecret);

    private bool HasTargetCredentials()
        => !string.IsNullOrWhiteSpace(TargetConsumerKey)
            && !string.IsNullOrWhiteSpace(TargetConsumerSecret);

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

    private void LoadPreferences()
    {
        try
        {
            if (!File.Exists(_preferencesPath))
            {
                return;
            }

            var json = File.ReadAllText(_preferencesPath);
            var snapshot = JsonSerializer.Deserialize<UserPreferences>(json);
            if (snapshot is null)
            {
                return;
            }

            _expCsv = snapshot.ExportCsv;
            _expShopify = snapshot.ExportShopify;
            _expWoo = snapshot.ExportWoo;
            _expReviews = snapshot.ExportReviews;
            _expXlsx = snapshot.ExportXlsx;
            _expJsonl = snapshot.ExportJsonl;
            _expPluginsCsv = snapshot.ExportPluginsCsv;
            _expPluginsJsonl = snapshot.ExportPluginsJsonl;
            _expThemesCsv = snapshot.ExportThemesCsv;
            _expThemesJsonl = snapshot.ExportThemesJsonl;
            _expPublicExtensionFootprints = snapshot.ExportPublicExtensionFootprints;
            _additionalPublicExtensionPages = snapshot.AdditionalPublicExtensionPages ?? string.Empty;
            _publicExtensionMaxPages = snapshot.PublicExtensionMaxPages ?? _publicExtensionMaxPages;
            _publicExtensionMaxBytes = snapshot.PublicExtensionMaxBytes ?? _publicExtensionMaxBytes;
            _additionalDesignSnapshotPages = snapshot.AdditionalDesignSnapshotPages ?? string.Empty;
            _designScreenshotBreakpointsText = snapshot.DesignScreenshotBreakpointsText ?? string.Empty;
            _expPublicDesignSnapshot = snapshot.ExportPublicDesignSnapshot;
            _expPublicDesignScreenshots = snapshot.ExportPublicDesignScreenshots;
            _expStoreConfiguration = snapshot.ExportStoreConfiguration;
            _importStoreConfiguration = snapshot.ImportStoreConfiguration;
            _chatApiEndpoint = snapshot.ChatApiEndpoint ?? string.Empty;
            _chatModel = snapshot.ChatModel ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(snapshot.ChatSystemPrompt))
            {
                _chatSystemPrompt = snapshot.ChatSystemPrompt!;
            }
            _chatMaxPromptTokens = snapshot.ChatDefaultMaxPromptTokens ?? snapshot.LegacyChatMaxPromptTokens;
            _chatMaxTotalTokens = snapshot.ChatDefaultMaxTotalTokens ?? snapshot.LegacyChatMaxTotalTokens;
            _chatMaxCostUsd = snapshot.ChatDefaultMaxCostUsd ?? snapshot.LegacyChatMaxCostUsd;
            _chatPromptTokenUsdPerThousand = snapshot.ChatPromptTokenUsdPerThousand;
            _chatCompletionTokenUsdPerThousand = snapshot.ChatCompletionTokenUsdPerThousand;
            _enableHttpRetries = snapshot.EnableHttpRetries;
            if (snapshot.HttpRetryAttempts >= 0)
            {
                _httpRetryAttempts = snapshot.HttpRetryAttempts;
            }
            if (snapshot.HttpRetryBaseDelaySeconds > 0)
            {
                _httpRetryBaseDelaySeconds = snapshot.HttpRetryBaseDelaySeconds;
            }
            if (snapshot.HttpRetryMaxDelaySeconds > 0)
            {
                _httpRetryMaxDelaySeconds = snapshot.HttpRetryMaxDelaySeconds;
            }
        }
        catch
        {
            // Ignore preference load failures and continue with defaults.
        }
    }

    private void OnLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ExplainLogsCommand?.RaiseCanExecuteChanged();

        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null || e.NewItems.Count == 0)
        {
            return;
        }

        lock (_logSummarySync)
        {
            _pendingLogSummaryCount += e.NewItems.Count;
        }

        ScheduleLogSummaryDebounced();
    }

    private void OnChatMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SaveChatTranscriptCommand.RaiseCanExecuteChanged();
        ClearChatHistoryCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(HasChatMessages));
    }

    private void OnArtifactIndexChanged(object? sender, EventArgs e)
    {
        ExecuteOnUiThread(() =>
        {
            OnPropertyChanged(nameof(HasIndexedDatasets));
            UpdateChatConfigurationStatus();
        });
    }

    private void ScheduleLogSummaryDebounced()
    {
        lock (_logSummarySync)
        {
            _logSummaryTimer ??= new System.Threading.Timer(OnLogSummaryTimerElapsed, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _logSummaryTimer.Change(_logSummaryDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnLogSummaryTimerElapsed(object? state)
    {
        int pending;
        lock (_logSummarySync)
        {
            pending = _pendingLogSummaryCount;
            _pendingLogSummaryCount = 0;
        }

        if (pending == 0)
        {
            return;
        }

        if (!HasChatConfiguration)
        {
            return;
        }

        if (IsLogSummaryBusy)
        {
            lock (_logSummarySync)
            {
                _pendingLogSummaryCount += pending;
            }

            ScheduleLogSummaryDebounced();
            return;
        }

        var snapshot = CaptureRecentLogs();
        if (snapshot.Count == 0)
        {
            return;
        }

        _ = SummarizeLogsAsync(snapshot, CancellationToken.None);
    }

    private IReadOnlyList<string> CaptureRecentLogs(int maxEntries = 80)
    {
        if (Logs.Count == 0)
        {
            return Array.Empty<string>();
        }

        var snapshot = new List<string>();
        ExecuteOnUiThread(() =>
        {
            var count = Logs.Count;
            var start = Math.Max(0, count - maxEntries);
            for (var i = start; i < count; i++)
            {
                snapshot.Add(Logs[i]);
            }
        });

        return snapshot;
    }

    private async Task SummarizeLogsAsync(IReadOnlyList<string> logSnapshot, CancellationToken cancellationToken)
    {
        if (!HasChatConfiguration || logSnapshot.Count == 0)
        {
            return;
        }

        if (IsLogSummaryBusy)
        {
            return;
        }

        ExecuteOnUiThread(() => IsLogSummaryBusy = true);

        ChatSessionSettings? session = null;

        try
        {
            session = new ChatSessionSettings(
                ChatApiEndpoint,
                ChatApiKey,
                ChatModel,
                ChatSystemPrompt,
                MaxPromptTokens: ChatMaxPromptTokens,
                MaxTotalTokens: ChatMaxTotalTokens,
                MaxCostUsd: ChatMaxCostUsd,
                PromptTokenCostPerThousandUsd: ChatPromptTokenUsdPerThousand,
                CompletionTokenCostPerThousandUsd: ChatCompletionTokenUsdPerThousand,
                DiagnosticLogger: Append,
                UsageReported: OnChatUsageReported);
            var summary = await _chatAssistantService.SummarizeLogsAsync(session, logSnapshot, cancellationToken).ConfigureAwait(false);
            if (summary is not null)
            {
                PublishLogSummary(summary);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            var message = $"AI log triage failed: {ex.Message}";
            ExecuteOnUiThread(() =>
            {
                LogSummaryIssues.Clear();
                LatestLogSummaryOverview = message;
                LatestLogSummary = new LogTriageResult(DateTimeOffset.UtcNow, message, Array.Empty<LogTriageIssue>());
            });
        }
        finally
        {
            ExecuteOnUiThread(() => IsLogSummaryBusy = false);

            UpdateChatUsageTotals(session);

            var shouldReschedule = false;
            lock (_logSummarySync)
            {
                shouldReschedule = _pendingLogSummaryCount > 0;
            }

            if (shouldReschedule)
            {
                ScheduleLogSummaryDebounced();
            }
        }
    }

    private void PublishLogSummary(LogTriageResult summary)
    {
        ExecuteOnUiThread(() =>
        {
            LogSummaryIssues.Clear();
            if (summary.Issues is { Count: > 0 })
            {
                foreach (var issue in summary.Issues)
                {
                    LogSummaryIssues.Add(issue);
                }
            }

            LatestLogSummaryOverview = summary.Overview;
            LatestLogSummary = summary;
        });
    }

    private static string? BuildLogSummaryStatus(LogTriageResult? summary)
    {
        if (summary is null)
        {
            return null;
        }

        if (summary.Issues is { Count: > 0 })
        {
            var topIssue = summary.Issues
                .OrderByDescending(issue => GetSeverityRank(issue.Severity))
                .ThenBy(issue => issue.Category, StringComparer.OrdinalIgnoreCase)
                .First();

            var severity = NormalizeSeverityLabel(topIssue.Severity);
            return $"{severity}: {topIssue.Category} – {topIssue.Description}";
        }

        return summary.Overview;
    }

    private static int GetSeverityRank(string? severity)
        => severity?.Trim().ToLowerInvariant() switch
        {
            "critical" => 4,
            "error" => 3,
            "warning" => 2,
            "info" => 1,
            _ => 0,
        };

    private static string NormalizeSeverityLabel(string? severity)
    {
        if (string.IsNullOrWhiteSpace(severity))
        {
            return "Info";
        }

        var trimmed = severity.Trim();
        if (trimmed.Length == 0)
        {
            return "Info";
        }

        return trimmed.Length == 1
            ? char.ToUpperInvariant(trimmed[0]).ToString()
            : char.ToUpperInvariant(trimmed[0]) + trimmed[1..].ToLowerInvariant();
    }

    private static void ExecuteOnUiThread(Action action)
    {
        if (action is null)
        {
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }

    private async Task IndexArtifactIfSupportedAsync(string? filePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            await _artifactIndexingService.IndexArtifactAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Append($"Embedding index update failed for {filePath}: {ex.Message}");
        }
    }


    private void SavePreferences()
    {
        try
        {
            Directory.CreateDirectory(_settingsDirectory);
            var snapshot = new UserPreferences
            {
                ExportCsv = ExportCsv,
                ExportShopify = ExportShopify,
                ExportWoo = ExportWoo,
                ExportReviews = ExportReviews,
                ExportXlsx = ExportXlsx,
                ExportJsonl = ExportJsonl,
                ExportPluginsCsv = ExportPluginsCsv,
                ExportPluginsJsonl = ExportPluginsJsonl,
                ExportThemesCsv = ExportThemesCsv,
                ExportThemesJsonl = ExportThemesJsonl,
                ExportPublicExtensionFootprints = ExportPublicExtensionFootprints,
                AdditionalPublicExtensionPages = AdditionalPublicExtensionPages,
                PublicExtensionMaxPages = PublicExtensionMaxPages,
                PublicExtensionMaxBytes = PublicExtensionMaxBytes,
                AdditionalDesignSnapshotPages = AdditionalDesignSnapshotPages,
                DesignScreenshotBreakpointsText = DesignScreenshotBreakpointsText,
                ExportPublicDesignSnapshot = ExportPublicDesignSnapshot,
                ExportPublicDesignScreenshots = ExportPublicDesignScreenshots,
                ExportStoreConfiguration = ExportStoreConfiguration,
                ImportStoreConfiguration = ImportStoreConfiguration,
                ChatApiEndpoint = ChatApiEndpoint,
                ChatModel = ChatModel,
                ChatSystemPrompt = ChatSystemPrompt,
                ChatDefaultMaxPromptTokens = ChatMaxPromptTokens,
                LegacyChatMaxPromptTokens = ChatMaxPromptTokens,
                ChatDefaultMaxTotalTokens = ChatMaxTotalTokens,
                LegacyChatMaxTotalTokens = ChatMaxTotalTokens,
                ChatDefaultMaxCostUsd = ChatMaxCostUsd,
                LegacyChatMaxCostUsd = ChatMaxCostUsd,
                ChatPromptTokenUsdPerThousand = ChatPromptTokenUsdPerThousand,
                ChatCompletionTokenUsdPerThousand = ChatCompletionTokenUsdPerThousand,
                EnableHttpRetries = EnableHttpRetries,
                HttpRetryAttempts = HttpRetryAttempts,
                HttpRetryBaseDelaySeconds = HttpRetryBaseDelaySeconds,
                HttpRetryMaxDelaySeconds = HttpRetryMaxDelaySeconds,
            };

            var json = JsonSerializer.Serialize(snapshot, _preferencesWriteOptions);
            File.WriteAllText(_preferencesPath, json);
        }
        catch
        {
            // Ignore persistence issues to avoid disrupting the UI workflow.
        }
    }

    private void LoadChatApiKey()
    {
        try
        {
            if (!File.Exists(_chatKeyPath))
            {
                _chatApiKey = string.Empty;
                OnPropertyChanged(nameof(ChatApiKey));
                HasChatApiKey = false;
                return;
            }

            var encrypted = File.ReadAllBytes(_chatKeyPath);
            if (encrypted.Length == 0)
            {
                _chatApiKey = string.Empty;
                OnPropertyChanged(nameof(ChatApiKey));
                HasChatApiKey = false;
                return;
            }

            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            var key = Encoding.UTF8.GetString(decrypted);
            _chatApiKey = key;
            OnPropertyChanged(nameof(ChatApiKey));
            HasChatApiKey = !string.IsNullOrWhiteSpace(key);
        }
        catch
        {
            _chatApiKey = string.Empty;
            OnPropertyChanged(nameof(ChatApiKey));
            HasChatApiKey = false;
        }
    }

    private bool TryPersistChatApiKey(string value)
    {
        try
        {
            Directory.CreateDirectory(_settingsDirectory);

            if (string.IsNullOrWhiteSpace(value))
            {
                if (File.Exists(_chatKeyPath))
                {
                    File.Delete(_chatKeyPath);
                }

                return true;
            }

            var bytes = Encoding.UTF8.GetBytes(value);
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_chatKeyPath, encrypted);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, object?> BuildCollectionExportRow(SelectableTerm choice)
    {
        var detail = choice.ShopifyCollection;
        var rules = detail?.Rules?
            .Select(rule => string.Join(':', new[] { rule.Column, rule.Relation, rule.Condition }
                .Where(part => !string.IsNullOrWhiteSpace(part))))
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToList();

        return new Dictionary<string, object?>
        {
            ["term_id"] = choice.Term.Id,
            ["term_name"] = choice.Term.Name,
            ["term_slug"] = choice.Term.Slug,
            ["collection_id"] = detail?.Id,
            ["handle"] = detail?.Handle ?? choice.Term.Slug ?? choice.Term.Name,
            ["title"] = detail?.Title ?? choice.Term.Name,
            ["body_html"] = detail?.BodyHtml,
            ["published_at"] = detail?.PublishedAt,
            ["updated_at"] = detail?.UpdatedAt,
            ["sort_order"] = detail?.SortOrder,
            ["template_suffix"] = detail?.TemplateSuffix,
            ["published_scope"] = detail?.PublishedScope,
            ["products_count"] = detail?.ProductsCount,
            ["admin_graphql_api_id"] = detail?.AdminGraphqlApiId,
            ["published"] = detail?.Published,
            ["disjunctive"] = detail?.Disjunctive,
            ["rules"] = rules is { Count: > 0 } ? rules : null,
            ["image_id"] = detail?.Image?.Id,
            ["image_src"] = detail?.Image?.Src,
            ["image_alt"] = detail?.Image?.Alt,
            ["image_width"] = detail?.Image?.Width,
            ["image_height"] = detail?.Image?.Height,
            ["image_created_at"] = detail?.Image?.CreatedAt,
        };
    }

    private async Task LoadFiltersForStoreAsync(string baseUrl, ILogger logger, CancellationToken cancellationToken)
    {
        var progress = RequireProgressLogger(logger);
        try
        {
            if (SelectedPlatform == PlatformMode.WooCommerce)
            {
                var cats = await _wooScraper.FetchProductCategoriesAsync(baseUrl, progress, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var tags = await _wooScraper.FetchProductTagsAsync(baseUrl, progress, cancellationToken);
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    CategoryChoices.Clear();
                    foreach (var c in cats) CategoryChoices.Add(new SelectableTerm(c));
                    TagChoices.Clear();
                    foreach (var t in tags) TagChoices.Add(new SelectableTerm(t));
                });
            }
            else
            {
                var settings = BuildShopifySettings(baseUrl);
                var collections = await _shopifyScraper.FetchCollectionsAsync(settings, progress, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var tags = await _shopifyScraper.FetchProductTagsAsync(settings, progress, cancellationToken);
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    CategoryChoices.Clear();
                    foreach (var collection in collections.Terms)
                        CategoryChoices.Add(new SelectableTerm(collection, collections.FindByTerm(collection)));
                    TagChoices.Clear();
                    foreach (var tag in tags)
                        TagChoices.Add(new SelectableTerm(tag));
                });
            }
        }
        catch (Exception ex)
        {
            logger.Report($"Filter load failed: {ex.Message}");
        }
    }

    private (int Attempts, TimeSpan BaseDelay, TimeSpan MaxDelay) GetRetrySettings()
    {
        var attempts = _enableHttpRetries ? Math.Max(0, _httpRetryAttempts) : 0;
        var baseSeconds = Math.Max(0.1, _httpRetryBaseDelaySeconds);
        var maxSeconds = Math.Max(baseSeconds, Math.Max(0.1, _httpRetryMaxDelaySeconds));
        return (attempts, TimeSpan.FromSeconds(baseSeconds), TimeSpan.FromSeconds(maxSeconds));
    }

    private async Task<RunPlanExecutionOutcome> ExecuteRunPlanAsync(RunPlan plan)
    {
        if (plan is null)
        {
            return new RunPlanExecutionOutcome(false, "Plan details were missing.");
        }

        _activeRunPlan = plan;
        _activeRunPlanOutcome = null;

        Append($"Run plan \"{plan.Name}\" initiated.");
        if (plan.PrerequisiteNotes.Count > 0)
        {
            foreach (var note in plan.PrerequisiteNotes)
            {
                Append($"  Prerequisite: {note}");
            }
        }

        ApplyRunPlanOverrides(plan);

        var cancellationToken = PrepareRunCancellationToken();

        RunPlanExecutionOutcome outcome;

        try
        {
            await OnRunAsync(cancellationToken);
            outcome = _activeRunPlanOutcome ?? new RunPlanExecutionOutcome(true, "Run invoked.");
        }
        catch (OperationCanceledException)
        {
            _activeRunPlanOutcome ??= RunPlanExecutionOutcome.CreateCancelled("Run cancelled.");
            outcome = _activeRunPlanOutcome;
        }
        finally
        {
            _activeRunPlan = null;
            _activeRunPlanOutcome = null;
        }

        return outcome;
    }

    private void OnCancelRun()
    {
        var cts = _runCts;
        if (cts is null)
        {
            return;
        }

        _runCts = null;

        try
        {
            cts.Cancel();
        }
        finally
        {
            cts.Dispose();
        }

        IsRunning = false;
        RaiseRunCommandStates();
    }

    private void RaiseRunCommandStates()
    {
        RunCommand.RaiseCanExecuteChanged();
        ReplicateCommand.RaiseCanExecuteChanged();
        CancelRunCommand.RaiseCanExecuteChanged();
    }

    private CancellationToken PrepareRunCancellationToken()
    {
        var previousCts = _runCts;
        if (previousCts is not null)
        {
            previousCts.Cancel();
            previousCts.Dispose();
        }

        var runCts = new CancellationTokenSource();
        _runCts = runCts;
        RaiseRunCommandStates();
        return runCts.Token;
    }

    private async Task OnRunAsync(CancellationToken cancellationToken)
    {
        var targetUrl = SelectedPlatform == PlatformMode.WooCommerce ? StoreUrl : ShopifyStoreUrl;
        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            Append("Please enter a store URL (e.g., https://example.com).");
            return;
        }

        try
        {
            targetUrl = WooScraper.CleanBaseUrl(targetUrl);
        }
        catch (ArgumentException ex)
        {
            Append($"Invalid store URL: {ex.Message}");
            return;
        }

        try
        {
            var runGoals = ManualRunGoals?.Trim();

            var retrySettings = GetRetrySettings();
            var retryPolicy = new HttpRetryPolicy(
                retrySettings.Attempts,
                retrySettings.BaseDelay,
                retrySettings.MaxDelay,
                logger: _loggerFactory.CreateLogger<HttpRetryPolicy>());
            _wooScraper.HttpPolicy = retryPolicy;
            _wpDirectoryClient.RetryPolicy = retryPolicy;

            _latestManualBundlePath = null;
            _latestManualReportPath = null;
            _latestRunDeltaPath = null;

            ResetProvisioningContext();

            IsRunning = true;
            ExecuteOnUiThread(() =>
            {
                _latestRunGoalsSnapshot = runGoals ?? string.Empty;
                LatestRunSnapshotJson = string.Empty;
                LatestRunAiNarrative = string.Empty;
                LatestRunAiBriefPath = null;
                LatestAutomationScriptSummary = string.Empty;
                LatestAutomationScriptError = string.Empty;
                LatestAutomationScriptWarnings.Clear();
                LatestAutomationScripts.Clear();
            });
            UpdateAiRecommendations(null);
            var baseOutputFolder = ResolveBaseOutputFolder();
            Directory.CreateDirectory(baseOutputFolder);

            var storeId = BuildStoreIdentifier(targetUrl);
            var storeOutputFolder = Path.Combine(baseOutputFolder, storeId);
            Directory.CreateDirectory(storeOutputFolder);
            var historyDirectory = GetRunHistoryDirectory(storeOutputFolder);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");

            _latestStoreOutputFolder = storeOutputFolder;

            var progressContext = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["StoreId"] = storeId,
                ["Output"] = storeOutputFolder
            };

            var operationLogger = CreateOperationLogger(
                "ManualExport",
                targetUrl,
                SelectedPlatform.ToString(),
                progressContext);
            var progressLogger = RequireProgressLogger(operationLogger);

            _artifactIndexingService.ResetForRun(storeId, timestamp);
            UpdateChatConfigurationStatus();

            // Refresh filters for this store
            await LoadFiltersForStoreAsync(targetUrl, operationLogger, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // Build filters
            string? categoryFilter = null;
            var selectedCategories = CategoryChoices.Where(x => x.IsSelected).Select(x => x.Term).ToList();
            if (SelectedPlatform == PlatformMode.WooCommerce)
            {
                var selectedCats = selectedCategories.Select(x => x.Id.ToString()).ToList();
                if (selectedCats.Count > 0) categoryFilter = string.Join(",", selectedCats);
            }

            string? tagFilter = null;
            var selectedTags = TagChoices.Where(x => x.IsSelected).Select(x => x.Term).ToList();
            if (SelectedPlatform == PlatformMode.WooCommerce)
            {
                var tagIds = selectedTags.Select(x => x.Id.ToString()).ToList();
                if (tagIds.Count > 0) tagFilter = string.Join(",", tagIds);
            }

            List<StoreProduct> prods;
            List<StoreProduct> variations = new();
            List<Dictionary<string, object?>>? shopifyDetailDicts = null;
            List<InstalledPlugin> plugins = new();
            List<InstalledTheme> themes = new();
            List<PublicExtensionFootprint> publicExtensionFootprints = new();
            PublicExtensionDetectionSummary? publicExtensionDetection = null;
            FrontEndDesignSnapshotResult? designSnapshot = null;
            bool attemptedDesignSnapshot = false;
            bool designSnapshotFailed = false;
            bool attemptedPluginFetch = false;
            bool attemptedThemeFetch = false;
            bool attemptedPublicExtensionFootprintFetch = false;
            StoreConfiguration? configuration = null;
            List<WooCustomer> customers = new();
            List<WooOrder> orders = new();
            List<WooCoupon> coupons = new();
            List<WooSubscription> subscriptions = new();
            bool attemptedCustomerFetch = false;
            bool attemptedOrderFetch = false;
            bool attemptedCouponFetch = false;
            bool attemptedSubscriptionFetch = false;
            List<TermItem> categoryTerms = new();
            List<WordPressPage> pages = new();
            List<WordPressPost> posts = new();
            List<WordPressMediaItem> mediaLibrary = new();
            WordPressMenuCollection? menuCollection = null;
            WordPressWidgetSnapshot widgets = new();
            var mediaReferenceMap = new Dictionary<string, MediaReference>(StringComparer.OrdinalIgnoreCase);
            WordPressSiteContent? siteContent = null;
            var missingCredentialExports = new List<string>();
            var designScreenshots = new List<DesignScreenshot>();
            var aiPublicExtensionInsights = new List<AiPublicExtensionInsight>();
            AiPublicExtensionCrawlContext? aiCrawlContext = null;
            var aiDesignAssets = new List<AiDesignAssetReference>();
            var aiColorPalette = new List<AiColorSwatch>();
            string? aiDesignManifestJsonPath = null;
            string? aiDesignManifestCsvPath = null;
            var aiAdditionalExtensionPages = new List<string>();
            int? aiPublicExtensionPageLimit = null;
            long? aiPublicExtensionByteLimit = null;
            string? exportVerificationSummary = null;
            string[] exportVerificationAlerts = Array.Empty<string>();
            string? exportVerificationPath = null;
            bool hasCriticalExportFindings = false;

            if (SelectedPlatform == PlatformMode.WooCommerce)
            {
                Append($"Fetching products via Store API… {targetUrl}");
                prods = await _wooScraper.FetchStoreProductsAsync(targetUrl, log: progressLogger, categoryFilter: categoryFilter, tagFilter: tagFilter, cancellationToken: cancellationToken);

                if (prods.Count == 0)
                {
                    Append("Store API empty. Trying WordPress REST fallback (basic fields)…");
                    prods = await _wooScraper.FetchWpProductsBasicAsync(targetUrl, log: progressLogger, cancellationToken: cancellationToken);
                }

                if (prods.Count == 0)
                {
                    Append("No products found via public APIs.");
                    return;
                }

                Append($"Found {prods.Count} products.");
                cancellationToken.ThrowIfCancellationRequested();

                var lastCategoryTerms = _wooScraper.LastFetchedProductCategories;
                if (lastCategoryTerms.Count > 0)
                {
                    categoryTerms = lastCategoryTerms.Where(term => term is not null).Select(term => term!).ToList();
                }
                else
                {
                    categoryTerms = await _wooScraper.FetchProductCategoriesAsync(targetUrl, progressLogger, cancellationToken);
                }

                // Variations for variable products
                var parentIds = prods.Where(p => string.Equals(p.Type, "variable", StringComparison.OrdinalIgnoreCase) || p.HasOptions == true)
                                     .Select(p => p.Id).Where(id => id > 0).Distinct().ToList();
                if (parentIds.Count > 0)
                {
                    Append($"Fetching variations for {parentIds.Count} variable products…");
                    variations = await _wooScraper.FetchStoreVariationsAsync(targetUrl, parentIds, log: progressLogger, cancellationToken: cancellationToken);
                    Append($"Found {variations.Count} variations.");
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            else
            {
                var settings = BuildShopifySettings(targetUrl);
                Append($"Fetching products via Shopify API… {settings.BaseUrl}");
                var rawProducts = await _shopifyScraper.FetchShopifyProductsAsync(settings, log: progressLogger, cancellationToken: cancellationToken);
                var pairs = rawProducts
                    .Select(p => (Product: p, Store: ShopifyConverters.ToStoreProduct(p, settings)))
                    .ToList();

                IEnumerable<(ShopifyProduct Product, StoreProduct Store)> filtered = pairs;
                if (selectedCategories.Count > 0)
                {
                    var handles = new HashSet<string>(selectedCategories
                        .Select(c => c.Slug ?? c.Name ?? string.Empty)
                        .Where(s => !string.IsNullOrWhiteSpace(s)), StringComparer.OrdinalIgnoreCase);
                    filtered = filtered.Where(pair => pair.Store.Categories.Any(c =>
                        (!string.IsNullOrWhiteSpace(c.Slug) && handles.Contains(c.Slug!)) ||
                        (!string.IsNullOrWhiteSpace(c.Name) && handles.Contains(c.Name!))));
                }

                if (selectedTags.Count > 0)
                {
                    var tagNames = new HashSet<string>(selectedTags
                        .Select(t => t.Name ?? t.Slug ?? string.Empty)
                        .Where(s => !string.IsNullOrWhiteSpace(s)), StringComparer.OrdinalIgnoreCase);
                    filtered = filtered.Where(pair => pair.Store.Tags.Any(t =>
                        (!string.IsNullOrWhiteSpace(t.Name) && tagNames.Contains(t.Name!)) ||
                        (!string.IsNullOrWhiteSpace(t.Slug) && tagNames.Contains(t.Slug!))));
                }

                var filteredList = filtered.ToList();

                if (filteredList.Count == 0)
                {
                    Append("No products found for the selected Shopify filters.");
                    return;
                }

                prods = filteredList.Select(pair => pair.Store).ToList();
                shopifyDetailDicts = filteredList
                    .Select(pair => ShopifyConverters.ToShopifyDetailDictionary(pair.Product, pair.Store))
                    .ToList();

                Append($"Found {prods.Count} products.");
                cancellationToken.ThrowIfCancellationRequested();
            }

            var needsPluginInventory = ExportPluginsCsv || ExportPluginsJsonl;
            var needsThemeInventory = ExportThemesCsv || ExportThemesJsonl;
            var pluginBundles = new List<ExtensionArtifact>();
            var themeBundles = new List<ExtensionArtifact>();
            Dictionary<string, JsonElement>? wpSettingsSnapshot = null;

            if (SelectedPlatform == PlatformMode.WooCommerce && (needsPluginInventory || needsThemeInventory))
            {
                if (string.IsNullOrWhiteSpace(WordPressUsername) || string.IsNullOrWhiteSpace(WordPressApplicationPassword))
                {
                    Append("Skipping plugin/theme exports: provide WordPress username and application password.");
                    missingCredentialExports.Add("Plugin and theme inventory exports (requires WordPress username and application password).");
                }
                else
                {
                    if (needsPluginInventory)
                    {
                        attemptedPluginFetch = true;
                        Append("Fetching installed plugins…");
                        plugins = await _wooScraper.FetchPluginsAsync(targetUrl, WordPressUsername, WordPressApplicationPassword, progressLogger, cancellationToken: cancellationToken);
                        Append(plugins.Count > 0
                            ? $"Found {plugins.Count} plugins."
                            : "No plugins returned by the authenticated endpoint.");
                    }

                    if (needsThemeInventory)
                    {
                        attemptedThemeFetch = true;
                        Append("Fetching installed themes…");
                        themes = await _wooScraper.FetchThemeAsync(targetUrl, WordPressUsername, WordPressApplicationPassword, progressLogger, cancellationToken: cancellationToken);
                        Append(themes.Count > 0
                            ? $"Found {themes.Count} themes."
                            : "No themes returned by the authenticated endpoint.");
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (SelectedPlatform == PlatformMode.WooCommerce && ExportPublicExtensionFootprints)
            {
                if (HasWordPressCredentials)
                {
                    Append("Slug-only extension detection skipped because authenticated plugin/theme exports are available.");
                }
                else
                {
                    attemptedPublicExtensionFootprintFetch = true;
                    Append("Detecting public plugin/theme slugs (slug-only export; manual install required)…");
                    var additionalEntryUrls = GetAdditionalPublicExtensionEntryUrls();
                    if (additionalEntryUrls.Count > 0)
                    {
                        Append($"Including {additionalEntryUrls.Count} additional page(s) for public asset detection.");
                    }

                    var (pageLimit, byteLimit) = GetPublicExtensionLimits(operationLogger);
                    aiAdditionalExtensionPages = additionalEntryUrls.ToList();
                    aiPublicExtensionPageLimit = pageLimit;
                    aiPublicExtensionByteLimit = byteLimit;
                    publicExtensionFootprints = await _wooScraper.FetchPublicExtensionFootprintsAsync(
                        targetUrl,
                        includeLinkedAssets: true,
                        log: progressLogger,
                        additionalEntryUrls: additionalEntryUrls,
                        maxPages: pageLimit,
                        maxBytes: byteLimit,
                        cancellationToken: cancellationToken);
                    publicExtensionDetection = _wooScraper.LastPublicExtensionDetection;
                    if (publicExtensionFootprints.Count > 0)
                    {
                        Append($"Detected {publicExtensionFootprints.Count} plugin/theme slug(s) from public assets (manual install required).");
                        await EnrichPublicExtensionFootprintsAsync(publicExtensionFootprints, operationLogger, cancellationToken);
                        aiPublicExtensionInsights.AddRange(publicExtensionFootprints.Select(footprint =>
                        {
                            var sources = footprint.SourceUrls?.Where(url => !string.IsNullOrWhiteSpace(url))
                                .Select(url => url.Trim())
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList() ?? new List<string>();

                            if (!string.IsNullOrWhiteSpace(footprint.SourceUrl) && !sources.Contains(footprint.SourceUrl, StringComparer.OrdinalIgnoreCase))
                            {
                                sources.Add(footprint.SourceUrl.Trim());
                            }

                            var slug = string.IsNullOrWhiteSpace(footprint.Slug) ? string.Empty : footprint.Slug.Trim();
                            var type = string.IsNullOrWhiteSpace(footprint.Type) ? string.Empty : footprint.Type.Trim();

                            return new AiPublicExtensionInsight(
                                slug,
                                type,
                                string.IsNullOrWhiteSpace(footprint.VersionHint) ? null : footprint.VersionHint!.Trim(),
                                string.IsNullOrWhiteSpace(footprint.WordPressVersion) ? null : footprint.WordPressVersion!.Trim(),
                                string.IsNullOrWhiteSpace(footprint.WooCommerceVersion) ? null : footprint.WooCommerceVersion!.Trim(),
                                sources,
                                string.IsNullOrWhiteSpace(footprint.AssetUrl) ? null : footprint.AssetUrl!.Trim(),
                                string.IsNullOrWhiteSpace(footprint.DirectoryTitle) ? null : footprint.DirectoryTitle!.Trim(),
                                string.IsNullOrWhiteSpace(footprint.DirectoryAuthor) ? null : footprint.DirectoryAuthor!.Trim(),
                                string.IsNullOrWhiteSpace(footprint.DirectoryVersion) ? null : footprint.DirectoryVersion!.Trim(),
                                string.IsNullOrWhiteSpace(footprint.DirectoryDownloadUrl) ? null : footprint.DirectoryDownloadUrl!.Trim(),
                                string.IsNullOrWhiteSpace(footprint.DirectoryStatus) ? null : footprint.DirectoryStatus!.Trim());
                        }));
                    }
                    else
                    {
                        Append("No public plugin/theme slugs were detected.");
                    }

                    aiCrawlContext = new AiPublicExtensionCrawlContext(
                        aiPublicExtensionPageLimit,
                        aiPublicExtensionByteLimit,
                        publicExtensionDetection?.ScheduledPageCount ?? 0,
                        publicExtensionDetection?.ProcessedPageCount ?? 0,
                        publicExtensionDetection?.TotalBytesDownloaded ?? 0,
                        publicExtensionDetection?.PageLimitReached ?? false,
                        publicExtensionDetection?.ByteLimitReached ?? false,
                        aiAdditionalExtensionPages,
                        publicExtensionDetection?.WordPressVersion,
                        publicExtensionDetection?.WooCommerceVersion);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (SelectedPlatform == PlatformMode.WooCommerce && ExportPublicDesignSnapshot)
            {
                attemptedDesignSnapshot = true;
                try
                {
                    Append("Capturing public front-end design snapshot…");
                    var additionalDesignPages = GetAdditionalDesignSnapshotPageUrls();
                    if (additionalDesignPages.Count > 0)
                    {
                        Append($"Including {additionalDesignPages.Count} additional design page(s).");
                    }

                    designSnapshot = await _wooScraper.FetchPublicDesignSnapshotAsync(targetUrl, progressLogger, additionalDesignPages, cancellationToken);
                    if (designSnapshot is null || string.IsNullOrWhiteSpace(designSnapshot.RawHtml))
                    {
                        Append("Public design snapshot capture returned no HTML.");
                    }
                    else
                    {
                        var fontCount = designSnapshot.FontUrls.Count;
                        var inlineLength = designSnapshot.InlineCss.Length;
                        var imageCount = designSnapshot.ImageFiles.Count;
                        var swatchCount = designSnapshot.ColorSwatches.Count;
                        Append($"Captured public design snapshot (inline CSS length: {inlineLength:N0} chars, fonts: {fontCount}, background images: {imageCount}, palette colors: {swatchCount}).");
                    }
                }
                catch (Exception ex)
                {
                    designSnapshotFailed = true;
                    Append($"Design snapshot capture failed: {ex.Message}");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (SelectedPlatform == PlatformMode.WooCommerce && ExportPublicDesignScreenshots)
            {
                try
                {
                    Append("Capturing public design screenshots…");
                    var screenshotFolder = Path.Combine(storeOutputFolder, "design", "screenshots");
                    var breakpoints = GetDesignScreenshotBreakpoints();
                    var screenshots = await _designScreenshotService.CaptureScreenshotsAsync(targetUrl, screenshotFolder, breakpoints, cancellationToken);

                    if (screenshots.Count > 0)
                    {
                        designScreenshots.AddRange(screenshots);
                        Append($"Captured {screenshots.Count} breakpoint screenshot(s) to {screenshotFolder}.");
                        foreach (var shot in screenshots)
                        {
                            Append($" - {shot.Label} ({shot.Width}x{shot.Height}): {shot.FilePath}");
                        }
                    }
                    else
                    {
                        Append("No screenshots were captured.");
                    }
                }
                catch (Exception ex)
                {
                    Append($"Design screenshot capture failed: {ex.Message}");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (SelectedPlatform == PlatformMode.WooCommerce)
            {
                if (string.IsNullOrWhiteSpace(WordPressUsername) || string.IsNullOrWhiteSpace(WordPressApplicationPassword))
                {
                    Append("Skipping customer/coupon/order exports: provide WordPress username and application password.");
                    missingCredentialExports.Add("Customer, coupon, order, and subscription exports (requires WordPress username and application password).");
                }
                else
                {
                    attemptedCustomerFetch = true;
                    Append("Fetching customers…");
                    customers = await _wooScraper.FetchCustomersAsync(targetUrl, WordPressUsername, WordPressApplicationPassword, progressLogger, cancellationToken: cancellationToken);
                    Append(customers.Count > 0
                        ? $"Found {customers.Count} customers."
                        : "No customers returned by the authenticated endpoint.");

                    attemptedCouponFetch = true;
                    Append("Fetching coupons…");
                    coupons = await _wooScraper.FetchCouponsAsync(targetUrl, WordPressUsername, WordPressApplicationPassword, progressLogger, cancellationToken: cancellationToken);
                    Append(coupons.Count > 0
                        ? $"Found {coupons.Count} coupons."
                        : "No coupons returned by the authenticated endpoint.");

                    attemptedOrderFetch = true;
                    Append("Fetching orders…");
                    orders = await _wooScraper.FetchOrdersAsync(targetUrl, WordPressUsername, WordPressApplicationPassword, progressLogger, cancellationToken: cancellationToken);
                    Append(orders.Count > 0
                        ? $"Found {orders.Count} orders."
                        : "No orders returned by the authenticated endpoint.");

                    attemptedSubscriptionFetch = true;
                    Append("Fetching subscriptions…");
                    subscriptions = await _wooScraper.FetchSubscriptionsAsync(targetUrl, WordPressUsername, WordPressApplicationPassword, progressLogger, cancellationToken: cancellationToken);
                    Append(subscriptions.Count > 0
                        ? $"Found {subscriptions.Count} subscriptions."
                        : "No subscriptions returned by the authenticated endpoint.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (SelectedPlatform == PlatformMode.WooCommerce
                && (plugins.Count > 0 || themes.Count > 0)
                && !string.IsNullOrWhiteSpace(WordPressUsername)
                && !string.IsNullOrWhiteSpace(WordPressApplicationPassword))
            {
                wpSettingsSnapshot = await _wooScraper.FetchWordPressSettingsAsync(targetUrl, WordPressUsername, WordPressApplicationPassword, progressLogger, cancellationToken);
                if (wpSettingsSnapshot.Count > 0)
                {
                    Append($"Captured {wpSettingsSnapshot.Count} WordPress settings entries for extension option discovery.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (SelectedPlatform == PlatformMode.WooCommerce && plugins.Count > 0)
            {
                var pluginRoot = Path.Combine(storeOutputFolder, "plugins");
                pluginBundles = await CapturePluginBundlesAsync(targetUrl, WordPressUsername, WordPressApplicationPassword, plugins, pluginRoot, wpSettingsSnapshot, operationLogger, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (SelectedPlatform == PlatformMode.WooCommerce && themes.Count > 0)
            {
                var themeRoot = Path.Combine(storeOutputFolder, "themes");
                themeBundles = await CaptureThemeBundlesAsync(targetUrl, WordPressUsername, WordPressApplicationPassword, themes, themeRoot, wpSettingsSnapshot, operationLogger, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (SelectedPlatform == PlatformMode.WooCommerce)
            {
                Append("Fetching WordPress pages…");
                pages = await _wooScraper.FetchWordPressPagesAsync(targetUrl, log: progressLogger, cancellationToken: cancellationToken);
                Append(pages.Count > 0
                    ? $"Captured {pages.Count} pages."
                    : "No pages returned by the REST API.");

                Append("Fetching WordPress posts…");
                posts = await _wooScraper.FetchWordPressPostsAsync(targetUrl, log: progressLogger, cancellationToken: cancellationToken);
                Append(posts.Count > 0
                    ? $"Captured {posts.Count} posts."
                    : "No posts returned by the REST API.");

                Append("Fetching WordPress media library…");
                mediaLibrary = await _wooScraper.FetchWordPressMediaAsync(targetUrl, log: progressLogger, cancellationToken: cancellationToken);
                Append(mediaLibrary.Count > 0
                    ? $"Captured {mediaLibrary.Count} media entries."
                    : "No media entries returned by the REST API.");

                Append("Discovering WordPress menus…");
                menuCollection = await _wooScraper.FetchWordPressMenusAsync(targetUrl, log: progressLogger, cancellationToken: cancellationToken);
                if (menuCollection is not null && menuCollection.Menus.Count > 0)
                {
                    Append($"Captured {menuCollection.Menus.Count} menus from {menuCollection.Endpoint}.");
                }
                else
                {
                    Append("No menu endpoint responded or menus unavailable.");
                }

                if (!string.IsNullOrWhiteSpace(WordPressUsername) && !string.IsNullOrWhiteSpace(WordPressApplicationPassword))
                {
                    Append("Fetching WordPress widgets…");
                    widgets = await _wooScraper.FetchWordPressWidgetsAsync(targetUrl, WordPressUsername, WordPressApplicationPassword, progressLogger, cancellationToken: cancellationToken);
                    Append(widgets.Widgets.Count > 0
                        ? $"Captured {widgets.Widgets.Count} widgets across {widgets.Areas.Count} areas."
                        : "No widgets returned by the REST API.");
                }
                else
                {
                    widgets = new WordPressWidgetSnapshot();
                    Append("Skipping widgets: provide WordPress username and application password.");
                    missingCredentialExports.Add("Widget snapshots (requires WordPress username and application password).");
                }

                var mediaFolder = Path.Combine(storeOutputFolder, "media");
                Directory.CreateDirectory(mediaFolder);
                if (mediaLibrary.Count > 0)
                {
                    Append($"Downloading media library to {mediaFolder}…");
                    var downloadedMedia = await DownloadMediaLibraryAsync(mediaLibrary, mediaFolder, operationLogger, cancellationToken);
                    foreach (var pair in downloadedMedia)
                    {
                        mediaReferenceMap[pair.Key] = pair.Value;
                    }
                }
                else
                {
                    Append("Media download skipped (no entries).");
                }

                var contentItems = pages.Cast<WordPressContentBase>().Concat(posts).ToList();
                if (contentItems.Count > 0)
                {
                    Append("Resolving content media references…");
                    await PopulateContentMediaReferencesAsync(contentItems, mediaLibrary, mediaFolder, mediaReferenceMap, operationLogger, cancellationToken);
                }

                var contentFolder = Path.Combine(storeOutputFolder, "content");
                Directory.CreateDirectory(contentFolder);

                if (pages.Count > 0)
                {
                    var pagePath = Path.Combine(contentFolder, $"{storeId}_{timestamp}_pages.json");
                    var json = JsonSerializer.Serialize(pages, _artifactWriteOptions);
                    await File.WriteAllTextAsync(pagePath, json, Encoding.UTF8, cancellationToken);
                    Append($"Wrote {pagePath}");
                }

                if (posts.Count > 0)
                {
                    var postPath = Path.Combine(contentFolder, $"{storeId}_{timestamp}_posts.json");
                    var json = JsonSerializer.Serialize(posts, _artifactWriteOptions);
                    await File.WriteAllTextAsync(postPath, json, Encoding.UTF8, cancellationToken);
                    Append($"Wrote {postPath}");
                }

                if (mediaLibrary.Count > 0)
                {
                    var mediaPath = Path.Combine(contentFolder, $"{storeId}_{timestamp}_media.json");
                    var json = JsonSerializer.Serialize(mediaLibrary, _artifactWriteOptions);
                    await File.WriteAllTextAsync(mediaPath, json, Encoding.UTF8, cancellationToken);
                    Append($"Wrote {mediaPath}");
                }

                if (menuCollection is not null && (menuCollection.Menus.Count > 0 || menuCollection.Locations.Count > 0))
                {
                    var menuPath = Path.Combine(contentFolder, $"{storeId}_{timestamp}_menus.json");
                    var json = JsonSerializer.Serialize(menuCollection, _artifactWriteOptions);
                    await File.WriteAllTextAsync(menuPath, json, Encoding.UTF8, cancellationToken);
                    Append($"Wrote {menuPath}");
                }

                if (widgets.Widgets.Count > 0 || widgets.Areas.Count > 0 || widgets.WidgetTypes.Count > 0)
                {
                    var widgetPath = Path.Combine(contentFolder, $"{storeId}_{timestamp}_widgets.json");
                    var json = JsonSerializer.Serialize(widgets, _artifactWriteOptions);
                    await File.WriteAllTextAsync(widgetPath, json, Encoding.UTF8, cancellationToken);
                    Append($"Wrote {widgetPath}");
                }

                siteContent = new WordPressSiteContent
                {
                    Pages = pages,
                    Posts = posts,
                    MediaLibrary = mediaLibrary,
                    Menus = menuCollection,
                    Widgets = widgets,
                    MediaRootDirectory = mediaFolder
                };
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (SelectedPlatform == PlatformMode.WooCommerce && ExportStoreConfiguration)
            {
                if (string.IsNullOrWhiteSpace(WordPressUsername) || string.IsNullOrWhiteSpace(WordPressApplicationPassword))
                {
                    Append("Skipping store configuration export: provide WordPress username and application password.");
                    missingCredentialExports.Add("Store configuration export (requires WordPress username and application password).");
                }
                else
                {
                    Append("Capturing store configuration…");
                    configuration = await FetchStoreConfigurationAsync(targetUrl, WordPressUsername, WordPressApplicationPassword, operationLogger, cancellationToken);
                    if (configuration is not null && HasConfigurationData(configuration))
                    {
                        var configPath = Path.Combine(storeOutputFolder, $"{storeId}_{timestamp}_configuration.json");
                        var json = JsonSerializer.Serialize(configuration, _configurationWriteOptions);
                        await File.WriteAllTextAsync(configPath, json, Encoding.UTF8, cancellationToken);
                        Append($"Wrote {configPath}");
                    }
                    else
                    {
                        Append("No store configuration data was captured.");
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Generic rows projection
            var genericRows = Mappers.ToGenericRows(prods).ToList();
            var rowsById = new Dictionary<int, GenericRow>();
            foreach (var row in genericRows)
            {
                rowsById[row.Id] = row;
            }

            var imagesFolder = Path.Combine(storeOutputFolder, "images");
            Directory.CreateDirectory(imagesFolder);
            operationLogger.Report($"Downloading product images to {imagesFolder}…");

            foreach (var product in prods)
            {
                var relativePaths = await DownloadProductImagesAsync(product, imagesFolder, operationLogger, mediaReferenceMap, cancellationToken);
                product.ImageFilePaths = relativePaths;
                if (rowsById.TryGetValue(product.Id, out var row))
                {
                    row.ImageFilePaths = relativePaths;
                }
            }

            foreach (var variation in variations)
            {
                var relativePaths = await DownloadProductImagesAsync(variation, imagesFolder, operationLogger, mediaReferenceMap, cancellationToken);
                variation.ImageFilePaths = relativePaths;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Project generic rows lazily so CSV/JSONL exporters can stream directly without materializing.
            var genericDicts = genericRows.Select(r => new Dictionary<string, object?>
            {
                ["id"] = r.Id,
                ["name"] = r.Name,
                ["slug"] = r.Slug,
                ["permalink"] = r.Permalink,
                ["sku"] = r.Sku,
                ["type"] = r.Type,
                ["description_html"] = r.DescriptionHtml,
                ["short_description_html"] = r.ShortDescriptionHtml,
                ["summary_html"] = r.SummaryHtml,
                ["meta_title"] = r.MetaTitle,
                ["meta_description"] = r.MetaDescription,
                ["meta_keywords"] = r.MetaKeywords,
                ["regular_price"] = r.RegularPrice,
                ["sale_price"] = r.SalePrice,
                ["price"] = r.Price,
                ["currency"] = r.Currency,
                ["in_stock"] = r.InStock,
                ["stock_status"] = r.StockStatus,
                ["average_rating"] = r.AverageRating,
                ["review_count"] = r.ReviewCount,
                ["has_options"] = r.HasOptions,
                ["parent_id"] = r.ParentId,
                ["categories"] = r.Categories,
                ["category_slugs"] = r.CategorySlugs,
                ["tags"] = r.Tags,
                ["tag_slugs"] = r.TagSlugs,
                ["images"] = r.Images,
                ["image_alts"] = r.ImageAlts,
                ["image_file_paths"] = r.ImageFilePaths,
            });

            List<Dictionary<string, object?>>? genericDictBuffer = null;
            List<Dictionary<string, object?>> GetGenericDictBuffer()
            {
                genericDictBuffer ??= genericDicts.ToList();
                return genericDictBuffer;
            }

            int? DetermineBufferThreshold(int count)
                => count > 1000 ? 250 : (int?)null;

            if (ExportCsv)
            {
                var path = Path.Combine(storeOutputFolder, $"{storeId}_{timestamp}_products.csv");
                CsvExporter.Write(path, genericDicts, bufferThreshold: DetermineBufferThreshold(genericRows.Count));
                Append($"Wrote {path}");
                await IndexArtifactIfSupportedAsync(path, cancellationToken);
            }
            if (ExportXlsx)
            {
                var path = Path.Combine(storeOutputFolder, $"{storeId}_{timestamp}_products.xlsx");
                var excelRows = SelectedPlatform == PlatformMode.Shopify && shopifyDetailDicts is { Count: > 0 }
                    ? shopifyDetailDicts
                    : GetGenericDictBuffer();
                XlsxExporter.Write(path, excelRows);
                Append($"Wrote {path}");
            }
            if (ExportJsonl)
            {
                var path = Path.Combine(storeOutputFolder, $"{storeId}_{timestamp}_products.jsonl");
                JsonlExporter.Write(path, genericDicts, DetermineBufferThreshold(genericRows.Count));
                Append($"Wrote {path}");
                await IndexArtifactIfSupportedAsync(path, cancellationToken);
            }

            if (SelectedPlatform == PlatformMode.WooCommerce && ExportPluginsCsv)
            {
                if (plugins.Count > 0)
                {
                    var path = Path.Combine(storeOutputFolder, $"{storeId}_{timestamp}_plugins.csv");
                    CsvExporter.WritePlugins(path, plugins, bufferThreshold: DetermineBufferThreshold(plugins.Count));
                    Append($"Wrote {path}");
                    await IndexArtifactIfSupportedAsync(path, cancellationToken);
                }
                else if (attemptedPluginFetch)
                {
                    Append("Plugins CSV export skipped (no plugin data).");
                }
            }

            if (SelectedPlatform == PlatformMode.WooCommerce && ExportPluginsJsonl)
            {
                if (plugins.Count > 0)
                {
                    var path = Path.Combine(storeOutputFolder, $"{storeId}_{timestamp}_plugins.jsonl");
                    JsonlExporter.WritePlugins(path, plugins, DetermineBufferThreshold(plugins.Count));
                    Append($"Wrote {path}");
                    await IndexArtifactIfSupportedAsync(path, cancellationToken);
                }
                else if (attemptedPluginFetch)
                {
                    Append("Plugins JSONL export skipped (no plugin data).");
                }
            }

            if (SelectedPlatform == PlatformMode.WooCommerce && ExportThemesCsv)
            {
                if (themes.Count > 0)
                {
                    var path = Path.Combine(storeOutputFolder, $"{storeId}_{timestamp}_themes.csv");
                    CsvExporter.WriteThemes(path, themes, bufferThreshold: DetermineBufferThreshold(themes.Count));
                    Append($"Wrote {path}");
                    await IndexArtifactIfSupportedAsync(path, cancellationToken);
                }
                else if (attemptedThemeFetch)
                {
                    Append("Themes CSV export skipped (no theme data).");
                }
            }

            if (SelectedPlatform == PlatformMode.WooCommerce && ExportThemesJsonl)
            {
                if (themes.Count > 0)
                {
                    var path = Path.Combine(storeOutputFolder, $"{storeId}_{timestamp}_themes.jsonl");
                    JsonlExporter.WriteThemes(path, themes, DetermineBufferThreshold(themes.Count));
                    Append($"Wrote {path}");
                    await IndexArtifactIfSupportedAsync(path, cancellationToken);
                }
                else if (attemptedThemeFetch)
                {
                    Append("Themes JSONL export skipped (no theme data).");
                }
            }

            if (SelectedPlatform == PlatformMode.WooCommerce && ExportPublicExtensionFootprints)
            {
                if (publicExtensionFootprints.Count > 0)
                {
                    var csvPath = Path.Combine(storeOutputFolder, $"{storeId}_{timestamp}_public_extension_footprints.csv");
                    var rows = publicExtensionFootprints
                        .Select(f => new Dictionary<string, object?>
                        {
                            ["type"] = f.Type,
                            ["slug"] = f.Slug,
                            ["source_url"] = f.SourceUrl,
                            ["source_urls"] = f.SourceUrls is { Count: > 0 } ? string.Join(";", f.SourceUrls) : string.Empty,
                            ["asset_url"] = f.AssetUrl,
                            ["version_hint"] = f.VersionHint,
                            ["wordpress_version"] = f.WordPressVersion,
                            ["woocommerce_version"] = f.WooCommerceVersion,
                            ["directory_status"] = f.DirectoryStatus,
                            ["directory_title"] = f.DirectoryTitle,
                            ["directory_author"] = f.DirectoryAuthor,
                            ["directory_homepage"] = f.DirectoryHomepage,
                            ["directory_version"] = f.DirectoryVersion,
                            ["directory_download_url"] = f.DirectoryDownloadUrl
                        });
                    CsvExporter.Write(csvPath, rows);
                    var limitNote = BuildPublicExtensionLimitNote(publicExtensionDetection);
                    Append($"Wrote {csvPath} (includes asset URLs and version cues when available; manual install required).{limitNote}");
                    await IndexArtifactIfSupportedAsync(csvPath, cancellationToken);

                    var jsonPath = Path.Combine(storeOutputFolder, $"{storeId}_{timestamp}_public_extension_footprints.json");
                    var json = JsonSerializer.Serialize(publicExtensionFootprints, _artifactWriteOptions);
                    await File.WriteAllTextAsync(jsonPath, json, Encoding.UTF8, cancellationToken);
                    Append($"Wrote {jsonPath} (includes asset URLs and version cues when available; manual install required).{limitNote}");
                }
                else if (attemptedPublicExtensionFootprintFetch)
                {
                    Append("Public extension footprint export skipped (no slugs detected).");
                    var limitNote = BuildPublicExtensionLimitNote(publicExtensionDetection, includeLeadingSpace: false);
                    if (!string.IsNullOrEmpty(limitNote))
                    {
                        Append(limitNote);
                    }
                }
            }

            if (SelectedPlatform == PlatformMode.WooCommerce && ExportPublicDesignSnapshot)
            {
                if (!designSnapshotFailed && designSnapshot is not null && !string.IsNullOrWhiteSpace(designSnapshot.RawHtml))
                {
                    var designRoot = Path.Combine(storeOutputFolder, "design");
                    Directory.CreateDirectory(designRoot);
                    var assetsRoot = Path.Combine(designRoot, "assets");
                    Directory.CreateDirectory(assetsRoot);

                    var htmlPath = Path.Combine(designRoot, "homepage.html");
                    await File.WriteAllTextAsync(htmlPath, designSnapshot.RawHtml, Encoding.UTF8, cancellationToken);
                    Append($"Wrote {htmlPath}");

                    var cssPath = Path.Combine(designRoot, "inline-styles.css");
                    await File.WriteAllTextAsync(cssPath, designSnapshot.InlineCss, Encoding.UTF8, cancellationToken);
                    Append($"Wrote {cssPath}");

                    var fontsPath = Path.Combine(designRoot, "fonts.json");
                    var fontsJson = JsonSerializer.Serialize(designSnapshot.FontUrls, _artifactWriteOptions);
                    await File.WriteAllTextAsync(fontsPath, fontsJson, Encoding.UTF8, cancellationToken);
                    Append($"Wrote {fontsPath}");

                    if (designSnapshot.ColorSwatches.Count > 0)
                    {
                        var colorsCsvPath = Path.Combine(designRoot, "colors.csv");
                        var csvBuilder = new StringBuilder();
                        csvBuilder.AppendLine("color,count");
                        foreach (var swatch in designSnapshot.ColorSwatches)
                        {
                            var escapedColor = swatch.Value?.Replace("\"", "\"\"") ?? string.Empty;
                            csvBuilder.Append('"');
                            csvBuilder.Append(escapedColor);
                            csvBuilder.Append("\",");
                            csvBuilder.AppendLine(swatch.Count.ToString(CultureInfo.InvariantCulture));
                        }

                        await File.WriteAllTextAsync(colorsCsvPath, csvBuilder.ToString(), Encoding.UTF8, cancellationToken);
                        Append($"Wrote {colorsCsvPath}");
                        await IndexArtifactIfSupportedAsync(colorsCsvPath, cancellationToken);

                        var colorsJsonPath = Path.Combine(designRoot, "colors.json");
                        var colorsJson = JsonSerializer.Serialize(designSnapshot.ColorSwatches, _artifactWriteOptions);
                        await File.WriteAllTextAsync(colorsJsonPath, colorsJson, Encoding.UTF8, cancellationToken);
                        Append($"Wrote {colorsJsonPath}");

                        aiColorPalette.AddRange(designSnapshot.ColorSwatches.Select(swatch =>
                            new AiColorSwatch(swatch.Value ?? string.Empty, swatch.Count)));
                    }
                    else
                    {
                        Append("No CSS color swatches were detected.");
                    }

                    var stylesheetManifest = new List<Dictionary<string, object?>>();
                    var fontManifest = new List<Dictionary<string, object?>>();
                    var imageManifest = new List<Dictionary<string, object?>>();
                    var iconManifest = new List<Dictionary<string, object?>>();
                    var manifestCsvRows = new List<DesignAssetManifestCsvRow>();

                    for (var i = 0; i < designSnapshot.Stylesheets.Count; i++)
                    {
                        var stylesheet = designSnapshot.Stylesheets[i];
                        var fileName = CreateDesignAssetFileName(
                            prefix: "stylesheet",
                            index: i + 1,
                            url: stylesheet?.ResolvedUrl ?? stylesheet?.SourceUrl,
                            contentType: stylesheet?.ContentType,
                            defaultExtension: ".css");

                        var relativeAssetPath = Path.Combine("assets", fileName);
                        var assetPath = Path.Combine(assetsRoot, fileName);
                        var content = (stylesheet?.Content?.Length ?? 0) > 0
                            ? stylesheet!.Content
                            : Encoding.UTF8.GetBytes(stylesheet?.TextContent ?? string.Empty);

                        await File.WriteAllBytesAsync(assetPath, content, cancellationToken);
                        Append($"Wrote {assetPath}");

                        var fileSize = content.Length;
                        var sha256 = Convert.ToHexString(SHA256.HashData(content));

                        stylesheetManifest.Add(new Dictionary<string, object?>
                        {
                            ["file"] = NormalizeRelativePath(relativeAssetPath),
                            ["source_url"] = stylesheet?.SourceUrl,
                            ["resolved_url"] = stylesheet?.ResolvedUrl,
                            ["referenced_from"] = stylesheet?.ReferencedFrom,
                            ["content_type"] = stylesheet?.ContentType,
                            ["file_size_bytes"] = fileSize,
                            ["sha256"] = sha256
                        });

                        manifestCsvRows.Add(new DesignAssetManifestCsvRow(
                            Type: "stylesheet",
                            File: NormalizeRelativePath(relativeAssetPath),
                            SourceUrl: stylesheet?.SourceUrl,
                            ResolvedUrl: stylesheet?.ResolvedUrl,
                            ReferencedFrom: stylesheet?.ReferencedFrom,
                            ContentType: stylesheet?.ContentType,
                            FileSizeBytes: fileSize,
                            Sha256: sha256,
                            FontFamily: null,
                            FontStyle: null,
                            FontWeight: null,
                            Rel: null,
                            LinkType: null,
                            Sizes: null,
                            Color: null,
                            Media: null,
                            Origins: null,
                            References: null));
                    }

                    for (var i = 0; i < designSnapshot.FontFiles.Count; i++)
                    {
                        var font = designSnapshot.FontFiles[i];
                        var fileName = CreateDesignAssetFileName(
                            prefix: "font",
                            index: i + 1,
                            url: font?.ResolvedUrl ?? font?.SourceUrl,
                            contentType: font?.ContentType,
                            defaultExtension: ".bin");

                        var relativeAssetPath = Path.Combine("assets", fileName);
                        var assetPath = Path.Combine(assetsRoot, fileName);
                        var content = font?.Content ?? Array.Empty<byte>();

                        await File.WriteAllBytesAsync(assetPath, content, cancellationToken);
                        Append($"Wrote {assetPath}");

                        var fileSize = content.Length;
                        var sha256 = Convert.ToHexString(SHA256.HashData(content));

                        fontManifest.Add(new Dictionary<string, object?>
                        {
                            ["file"] = NormalizeRelativePath(relativeAssetPath),
                            ["source_url"] = font?.SourceUrl,
                            ["resolved_url"] = font?.ResolvedUrl,
                            ["referenced_from"] = font?.ReferencedFrom,
                            ["content_type"] = font?.ContentType,
                            ["font_family"] = font?.FontFamily,
                            ["font_style"] = font?.FontStyle,
                            ["font_weight"] = font?.FontWeight,
                            ["file_size_bytes"] = fileSize,
                            ["sha256"] = sha256
                        });

                        manifestCsvRows.Add(new DesignAssetManifestCsvRow(
                            Type: "font",
                            File: NormalizeRelativePath(relativeAssetPath),
                            SourceUrl: font?.SourceUrl,
                            ResolvedUrl: font?.ResolvedUrl,
                            ReferencedFrom: font?.ReferencedFrom,
                            ContentType: font?.ContentType,
                            FileSizeBytes: fileSize,
                            Sha256: sha256,
                            FontFamily: font?.FontFamily,
                            FontStyle: font?.FontStyle,
                            FontWeight: font?.FontWeight,
                            Rel: null,
                            LinkType: null,
                            Sizes: null,
                            Color: null,
                            Media: null,
                            Origins: null,
                            References: null));
                    }

                    for (var i = 0; i < designSnapshot.ImageFiles.Count; i++)
                    {
                        var image = designSnapshot.ImageFiles[i];
                        var fileName = CreateDesignAssetFileName(
                            prefix: "image",
                            index: i + 1,
                            url: image?.ResolvedUrl ?? image?.SourceUrl,
                            contentType: image?.ContentType,
                            defaultExtension: ".bin");

                        var relativeAssetPath = Path.Combine("assets", fileName);
                        var assetPath = Path.Combine(assetsRoot, fileName);
                        var content = image?.Content ?? Array.Empty<byte>();

                        await File.WriteAllBytesAsync(assetPath, content, cancellationToken);
                        Append($"Wrote {assetPath}");

                        var fileSize = content.Length;
                        var sha256 = Convert.ToHexString(SHA256.HashData(content));
                        IReadOnlyList<string>? references = image?.References?
                            .Where(r => !string.IsNullOrWhiteSpace(r))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        IReadOnlyList<string>? origins = image?.Origins?
                            .Select(o => o.ToString().ToLowerInvariant())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        var safeReferences = references ?? Array.Empty<string>();
                        var safeOrigins = origins ?? Array.Empty<string>();

                        imageManifest.Add(new Dictionary<string, object?>
                        {
                            ["file"] = NormalizeRelativePath(relativeAssetPath),
                            ["source_url"] = image?.SourceUrl,
                            ["resolved_url"] = image?.ResolvedUrl,
                            ["referenced_from"] = image?.ReferencedFrom,
                            ["references"] = safeReferences,
                            ["origins"] = safeOrigins,
                            ["content_type"] = image?.ContentType,
                            ["file_size_bytes"] = fileSize,
                            ["sha256"] = sha256
                        });

                        manifestCsvRows.Add(new DesignAssetManifestCsvRow(
                            Type: "image",
                            File: NormalizeRelativePath(relativeAssetPath),
                            SourceUrl: image?.SourceUrl,
                            ResolvedUrl: image?.ResolvedUrl,
                            ReferencedFrom: image?.ReferencedFrom,
                            ContentType: image?.ContentType,
                            FileSizeBytes: fileSize,
                            Sha256: sha256,
                            FontFamily: null,
                            FontStyle: null,
                            FontWeight: null,
                            Rel: null,
                            LinkType: null,
                            Sizes: null,
                            Color: null,
                            Media: null,
                            Origins: origins,
                            References: references));
                    }

                    string? iconsRoot = null;
                    if (designSnapshot.IconFiles.Count > 0)
                    {
                        iconsRoot = Path.Combine(designRoot, "icons");
                        Directory.CreateDirectory(iconsRoot);

                        for (var i = 0; i < designSnapshot.IconFiles.Count; i++)
                        {
                            var icon = designSnapshot.IconFiles[i];
                            var fileName = CreateDesignAssetFileName(
                                prefix: "icon",
                                index: i + 1,
                                url: icon?.ResolvedUrl ?? icon?.SourceUrl,
                                contentType: icon?.ContentType,
                                defaultExtension: ".ico");

                            var relativeIconPath = Path.Combine("icons", fileName);
                            var iconPath = Path.Combine(iconsRoot, fileName);
                            var content = icon?.Content ?? Array.Empty<byte>();

                            await File.WriteAllBytesAsync(iconPath, content, cancellationToken);
                            Append($"Wrote {iconPath}");

                            var fileSize = content.Length;
                            var sha256 = Convert.ToHexString(SHA256.HashData(content));
                            IReadOnlyList<string>? references = icon?.References?
                                .Where(r => !string.IsNullOrWhiteSpace(r))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();
                            var safeReferences = references ?? Array.Empty<string>();

                            iconManifest.Add(new Dictionary<string, object?>
                            {
                                ["file"] = NormalizeRelativePath(relativeIconPath),
                                ["source_url"] = icon?.SourceUrl,
                                ["resolved_url"] = icon?.ResolvedUrl,
                                ["referenced_from"] = icon?.ReferencedFrom,
                                ["references"] = safeReferences,
                                ["content_type"] = icon?.ContentType,
                                ["rel"] = icon?.Rel,
                                ["link_type"] = icon?.LinkType,
                                ["sizes"] = icon?.Sizes,
                                ["color"] = icon?.Color,
                                ["media"] = icon?.Media,
                                ["file_size_bytes"] = fileSize,
                                ["sha256"] = sha256
                            });

                            manifestCsvRows.Add(new DesignAssetManifestCsvRow(
                                Type: "icon",
                                File: NormalizeRelativePath(relativeIconPath),
                                SourceUrl: icon?.SourceUrl,
                                ResolvedUrl: icon?.ResolvedUrl,
                                ReferencedFrom: icon?.ReferencedFrom,
                                ContentType: icon?.ContentType,
                                FileSizeBytes: fileSize,
                                Sha256: sha256,
                                FontFamily: null,
                                FontStyle: null,
                                FontWeight: null,
                                Rel: icon?.Rel,
                                LinkType: icon?.LinkType,
                                Sizes: icon?.Sizes,
                                Color: icon?.Color,
                                Media: icon?.Media,
                                Origins: null,
                                References: references));
                        }

                        Append($"Captured {designSnapshot.IconFiles.Count} design icon(s); assets saved under {iconsRoot}.");
                    }
                    else
                    {
                        Append("No design icons were captured from link tags.");
                    }

                    var cssImageCount = designSnapshot.CssImageFiles
                        .Select(img => img?.ResolvedUrl)
                        .Where(url => !string.IsNullOrWhiteSpace(url))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count();

                    var htmlImageCount = designSnapshot.HtmlImageFiles
                        .Select(img => img?.ResolvedUrl)
                        .Where(url => !string.IsNullOrWhiteSpace(url))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count();

                    if (designSnapshot.ImageFiles.Count > 0)
                    {
                        Append($"Captured {designSnapshot.ImageFiles.Count} design image(s) (CSS {cssImageCount}, HTML {htmlImageCount}); assets saved under {assetsRoot}.");
                    }
                    else
                    {
                        Append("No design images were captured from CSS or HTML markup.");
                    }

                    var manifest = new Dictionary<string, object?>
                    {
                        ["stylesheets"] = stylesheetManifest,
                        ["fonts"] = fontManifest,
                        ["images"] = imageManifest,
                        ["icons"] = iconManifest,
                        ["image_summary"] = new Dictionary<string, object?>
                        {
                            ["total"] = designSnapshot.ImageFiles.Count,
                            ["css"] = cssImageCount,
                            ["html"] = htmlImageCount
                        },
                        ["icon_summary"] = new Dictionary<string, object?>
                        {
                            ["total"] = designSnapshot.IconFiles.Count
                        },
                        ["colors"] = designSnapshot.ColorSwatches
                    };

                    if (manifestCsvRows.Count > 0)
                    {
                        aiDesignAssets.AddRange(manifestCsvRows.Select(row => new AiDesignAssetReference(
                            row.Type,
                            row.File,
                            row.Sha256,
                            string.IsNullOrWhiteSpace(row.SourceUrl) ? null : row.SourceUrl,
                            string.IsNullOrWhiteSpace(row.ResolvedUrl) ? null : row.ResolvedUrl,
                            string.IsNullOrWhiteSpace(row.ReferencedFrom) ? null : row.ReferencedFrom,
                            string.IsNullOrWhiteSpace(row.ContentType) ? null : row.ContentType,
                            row.FileSizeBytes,
                            row.Origins is { Count: > 0 } ? row.Origins.ToList() : null,
                            row.References is { Count: > 0 } ? row.References.ToList() : null)));
                    }

                    var manifestPath = Path.Combine(designRoot, "assets-manifest.json");
                    var manifestJson = JsonSerializer.Serialize(manifest, _artifactWriteOptions);
                    await File.WriteAllTextAsync(manifestPath, manifestJson, Encoding.UTF8, cancellationToken);
                    Append($"Wrote {manifestPath}");

                    aiDesignManifestJsonPath = TryGetStoreRelativePath(storeOutputFolder, manifestPath);

                    var manifestCsvPath = Path.Combine(designRoot, "assets-manifest.csv");
                    var manifestCsvBuilder = new StringBuilder();
                    manifestCsvBuilder.AppendLine("type,file,source_url,resolved_url,referenced_from,content_type,file_size_bytes,sha256,font_family,font_style,font_weight,rel,link_type,sizes,color,media,origins,references");
                    foreach (var row in manifestCsvRows)
                    {
                        var originsValue = row.Origins is { Count: > 0 } ? string.Join(';', row.Origins) : string.Empty;
                        var referencesValue = row.References is { Count: > 0 } ? string.Join(';', row.References) : string.Empty;
                        manifestCsvBuilder.AppendLine(string.Join(',', new[]
                        {
                            EscapeCsv(row.Type),
                            EscapeCsv(row.File),
                            EscapeCsv(row.SourceUrl),
                            EscapeCsv(row.ResolvedUrl),
                            EscapeCsv(row.ReferencedFrom),
                            EscapeCsv(row.ContentType),
                            EscapeCsv(row.FileSizeBytes.ToString(CultureInfo.InvariantCulture)),
                            EscapeCsv(row.Sha256),
                            EscapeCsv(row.FontFamily),
                            EscapeCsv(row.FontStyle),
                            EscapeCsv(row.FontWeight),
                            EscapeCsv(row.Rel),
                            EscapeCsv(row.LinkType),
                            EscapeCsv(row.Sizes),
                            EscapeCsv(row.Color),
                            EscapeCsv(row.Media),
                            EscapeCsv(originsValue),
                            EscapeCsv(referencesValue)
                        }));
                    }

                    await File.WriteAllTextAsync(manifestCsvPath, manifestCsvBuilder.ToString(), Encoding.UTF8, cancellationToken);
                    Append($"Wrote {manifestCsvPath}");
                    await IndexArtifactIfSupportedAsync(manifestCsvPath, cancellationToken);

                    aiDesignManifestCsvPath = TryGetStoreRelativePath(storeOutputFolder, manifestCsvPath);
                }
                else if (attemptedDesignSnapshot && !designSnapshotFailed)
                {
                    Append("Design snapshot export skipped (no HTML to write).");
                }
            }

            if (ExportShopify)
            {
                var rows = (variations.Count > 0)
                    ? Mappers.ToShopifyCsvWithVariants(prods, variations, targetUrl).ToList()
                    : Mappers.ToShopifyCsv(prods, targetUrl).ToList();
                var path = Path.Combine(storeOutputFolder, $"{storeId}_{timestamp}_shopify_products.csv");
                CsvExporter.Write(path, rows);
                Append($"Wrote {path}");
                await IndexArtifactIfSupportedAsync(path, cancellationToken);
            }

            if (ExportWoo)
            {
                var parentIds = new HashSet<int>(prods.Select(p => p.Id));
                var wooVariations = variations
                    .Where(v => v is not null && (v.ParentId is null || parentIds.Contains(v.ParentId.Value)))
                    .ToList();
                var wooCatalog = prods.Concat(wooVariations).ToList();
                var rows = Mappers.ToWooImporterCsv(wooCatalog, wooVariations).ToList();
                var path = Path.Combine(storeOutputFolder, $"{storeId}_{timestamp}_woocommerce_products.csv");
                CsvExporter.Write(path, rows);
                Append($"Wrote {path}");
                await IndexArtifactIfSupportedAsync(path, cancellationToken);
            }

            if (ExportReviews)
            {
                if (SelectedPlatform == PlatformMode.WooCommerce && prods.Count > 0 && prods[0].Prices is not null)
                {
                    var ids = prods.Select(p => p.Id).Where(id => id > 0);
                    var revs = await _wooScraper.FetchStoreReviewsAsync(targetUrl, ids, log: progressLogger, cancellationToken: cancellationToken);
                    if (revs.Count > 0)
                    {
                        var path = Path.Combine(storeOutputFolder, $"{storeId}_{timestamp}_reviews.csv");
                        CsvExporter.Write(path, revs.Select(r => new Dictionary<string, object?>
                        {
                            ["id"] = r.Id,
                            ["product_id"] = r.ProductId,
                            ["reviewer"] = r.Reviewer,
                            ["rating"] = r.Rating,
                            ["review"] = r.Review,
                            ["date_created"] = r.DateCreated
                        }));
                        Append($"Wrote {path} ({revs.Count} rows)");
                        await IndexArtifactIfSupportedAsync(path, cancellationToken);
                    }
                    else
                    {
                        Append("No reviews found or endpoint not available.");
                    }
                }
                else
                {
                    Append(SelectedPlatform == PlatformMode.Shopify
                        ? "Reviews export not available for Shopify mode."
                        : "Reviews skipped (Store API product meta not present).");
                }
            }

            if (SelectedPlatform == PlatformMode.WooCommerce)
            {
                if (customers.Count > 0)
                {
                    var path = Path.Combine(storeOutputFolder, $"{storeId}_{timestamp}_customers.json");
                    var json = JsonSerializer.Serialize(customers, _artifactWriteOptions);
                    await File.WriteAllTextAsync(path, json, Encoding.UTF8, cancellationToken);
                    Append($"Wrote {path}");
                }
                else if (attemptedCustomerFetch)
                {
                    Append("Customers export skipped (no customer data).");
                }

                if (coupons.Count > 0)
                {
                    var path = Path.Combine(storeOutputFolder, $"{storeId}_{timestamp}_coupons.json");
                    var json = JsonSerializer.Serialize(coupons, _artifactWriteOptions);
                    await File.WriteAllTextAsync(path, json, Encoding.UTF8, cancellationToken);
                    Append($"Wrote {path}");
                }
                else if (attemptedCouponFetch)
                {
                    Append("Coupons export skipped (no coupon data).");
                }

                if (orders.Count > 0)
                {
                    var path = Path.Combine(storeOutputFolder, $"{storeId}_{timestamp}_orders.json");
                    var json = JsonSerializer.Serialize(orders, _artifactWriteOptions);
                    await File.WriteAllTextAsync(path, json, Encoding.UTF8, cancellationToken);
                    Append($"Wrote {path}");
                }
                else if (attemptedOrderFetch)
                {
                    Append("Orders export skipped (no order data).");
                }

                if (subscriptions.Count > 0)
                {
                    var path = Path.Combine(storeOutputFolder, $"{storeId}_{timestamp}_subscriptions.json");
                    var json = JsonSerializer.Serialize(subscriptions, _artifactWriteOptions);
                    await File.WriteAllTextAsync(path, json, Encoding.UTF8, cancellationToken);
                    Append($"Wrote {path}");
                }
                else if (attemptedSubscriptionFetch)
                {
                    Append("Subscriptions export skipped (no subscription data).");
                }
            }

            List<string> logSnapshot;
            if (System.Windows.Application.Current is null)
            {
                logSnapshot = Logs.ToList();
            }
            else
            {
                var tempLogs = new List<string>();
                System.Windows.Application.Current.Dispatcher.Invoke(() => tempLogs.AddRange(Logs));
                logSnapshot = tempLogs;
            }

            var detectedWordPressVersion = publicExtensionDetection?.WordPressVersion
                ?? publicExtensionFootprints
                    .Select(f => f.WordPressVersion)
                    .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            var detectedWooCommerceVersion = publicExtensionDetection?.WooCommerceVersion
                ?? publicExtensionFootprints
                    .Select(f => f.WooCommerceVersion)
                    .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

            AiDesignSnapshotInsight? designInsight = null;
            if (aiDesignAssets.Count > 0 || aiColorPalette.Count > 0 || !string.IsNullOrWhiteSpace(aiDesignManifestJsonPath) || !string.IsNullOrWhiteSpace(aiDesignManifestCsvPath))
            {
                var assetList = aiDesignAssets.Count > 0 ? aiDesignAssets.ToList() : new List<AiDesignAssetReference>();
                var paletteList = aiColorPalette.Count > 0 ? aiColorPalette.ToList() : new List<AiColorSwatch>();
                designInsight = new AiDesignSnapshotInsight(assetList, paletteList, aiDesignManifestJsonPath, aiDesignManifestCsvPath);
            }

            var indexedDatasets = _artifactIndexingService.GetIndexedDatasets();
            var artifactPayloadCandidate = new AiArtifactIntelligencePayload(
                targetUrl,
                aiPublicExtensionInsights.ToList(),
                aiCrawlContext,
                designInsight,
                indexedDatasets);

            var artifactPayload = artifactPayloadCandidate.HasContent ? artifactPayloadCandidate : null;

            var planPreviewOutcome = _activeRunPlan is null
                ? null
                : new RunPlanExecutionOutcome(true, "Assistant remediation run executed during this migration.");
            var planSnapshots = _runPlanner.CreateSnapshot(_activeRunPlan, planPreviewOutcome);

            var reportContext = new ManualMigrationReportContext(
                targetUrl,
                storeId,
                storeOutputFolder,
                SelectedPlatform == PlatformMode.WooCommerce,
                plugins,
                themes,
                publicExtensionFootprints,
                publicExtensionDetection,
                detectedWordPressVersion,
                detectedWooCommerceVersion,
                pluginBundles,
                themeBundles,
                SelectedPlatform == PlatformMode.WooCommerce && needsPluginInventory,
                SelectedPlatform == PlatformMode.WooCommerce && needsThemeInventory,
                SelectedPlatform == PlatformMode.WooCommerce && ExportPublicExtensionFootprints,
                attemptedPluginFetch,
                attemptedThemeFetch,
                attemptedPublicExtensionFootprintFetch,
                designSnapshot,
                designScreenshots,
                SelectedPlatform == PlatformMode.WooCommerce && ExportPublicDesignSnapshot,
                SelectedPlatform == PlatformMode.WooCommerce && ExportPublicDesignScreenshots,
                designSnapshotFailed,
                missingCredentialExports,
                logSnapshot,
                DateTime.UtcNow,
                EnableHttpRetries,
                retrySettings.Attempts,
                retrySettings.BaseDelay,
                retrySettings.MaxDelay,
                artifactPayload,
                RunPlans: planSnapshots,
                ChatTranscriptPath: _chatTranscriptStore.CurrentJsonlPath);

            var runSnapshotJson = ManualMigrationRunSummaryFactory.CreateSnapshotJson(reportContext);
            ExecuteOnUiThread(() =>
            {
                _latestRunGoalsSnapshot = runGoals ?? string.Empty;
                LatestRunSnapshotJson = runSnapshotJson;
                LatestRunAiNarrative = string.Empty;
                LatestRunAiBriefPath = null;
            });

            ChatSessionSettings? chatSession = null;
            if (HasChatConfiguration)
            {
                chatSession = new ChatSessionSettings(
                    ChatApiEndpoint,
                    ChatApiKey,
                    ChatModel,
                    ChatSystemPrompt,
                    MaxPromptTokens: ChatMaxPromptTokens,
                    MaxTotalTokens: ChatMaxTotalTokens,
                    MaxCostUsd: ChatMaxCostUsd,
                    PromptTokenCostPerThousandUsd: ChatPromptTokenUsdPerThousand,
                    CompletionTokenCostPerThousandUsd: ChatCompletionTokenUsdPerThousand,
                    DiagnosticLogger: Append,
                    UsageReported: OnChatUsageReported);
            }

            var automationReferences = new List<ManualMigrationAutomationScript>();
            var automationDisplays = new List<AutomationScriptDisplay>();
            var automationWarnings = new List<string>();
            string? automationSummary = null;
            string? automationError = null;

            cancellationToken.ThrowIfCancellationRequested();

            if (chatSession is null)
            {
                automationError = "Configure the assistant to generate automation scripts before running.";
                Append("Automation scripts skipped: assistant configuration missing.");
            }
            else
            {
                try
                {
                    var scriptResult = await _chatAssistantService.GenerateMigrationScriptsAsync(
                        chatSession,
                        runSnapshotJson,
                        runGoals,
                        cancellationToken);

                    if (!string.IsNullOrWhiteSpace(scriptResult.Summary))
                    {
                        automationSummary = scriptResult.Summary.Trim();
                    }

                    if (!string.IsNullOrWhiteSpace(scriptResult.Error))
                    {
                        automationError = scriptResult.Error.Trim();
                    }

                    foreach (var warning in scriptResult.Warnings)
                    {
                        if (!string.IsNullOrWhiteSpace(warning))
                        {
                            automationWarnings.Add(warning.Trim());
                        }
                    }

                    if (scriptResult.Scripts.Count > 0)
                    {
                        var scriptsFolder = Path.Combine(storeOutputFolder, "manual-migration-scripts");
                        Directory.CreateDirectory(scriptsFolder);
                        Append($"Saving automation scripts to {scriptsFolder}…");

                        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var index = 0;
                        foreach (var script in scriptResult.Scripts)
                        {
                            if (string.IsNullOrWhiteSpace(script.Content))
                            {
                                automationWarnings.Add($"Script '{script.Name}' was skipped because it did not include any content.");
                                continue;
                            }

                            var fileName = ResolveAutomationScriptFileName(script, index++, usedNames);
                            var scriptPath = Path.Combine(scriptsFolder, fileName);
                            await File.WriteAllTextAsync(scriptPath, script.Content, Encoding.UTF8, cancellationToken);
                            Append($"Automation script saved: {scriptPath}");

                            var relativePath = TryGetStoreRelativePath(storeOutputFolder, scriptPath) ?? scriptPath;

                            automationReferences.Add(new ManualMigrationAutomationScript(
                                script.Name,
                                script.Description,
                                script.Language,
                                scriptPath,
                                fileName,
                                script.Notes));

                            automationDisplays.Add(new AutomationScriptDisplay(
                                script.Name,
                                script.Description,
                                script.Language,
                                script.Content,
                                scriptPath,
                                relativePath,
                                script.Notes));
                        }

                        if (automationReferences.Count > 0)
                        {
                            Append($"Saved {automationReferences.Count} automation script(s).");
                        }
                        else if (automationWarnings.Count == 0 && string.IsNullOrWhiteSpace(automationError))
                        {
                            automationWarnings.Add("The assistant did not produce any automation scripts with content.");
                        }
                    }
                    else if (automationWarnings.Count == 0 && string.IsNullOrWhiteSpace(automationError))
                    {
                        automationWarnings.Add("The assistant did not return any automation scripts for this run.");
                    }
                }
                catch (Exception ex)
                {
                    automationError = ex.Message;
                    Append($"Automation script generation failed: {ex.Message}");
                }

                UpdateChatUsageTotals(chatSession);
            }

            var distinctWarnings = automationWarnings
                .Where(warning => !string.IsNullOrWhiteSpace(warning))
                .Select(warning => warning.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var automationScriptSet = new ManualMigrationAutomationScriptSet(
                string.IsNullOrWhiteSpace(automationSummary) ? null : automationSummary,
                automationReferences.Count > 0 ? automationReferences.ToArray() : Array.Empty<ManualMigrationAutomationScript>(),
                distinctWarnings,
                string.IsNullOrWhiteSpace(automationError) ? null : automationError);

            reportContext = reportContext with { AutomationScripts = automationScriptSet };

            var summaryForUi = automationSummary ?? string.Empty;
            var errorForUi = automationError ?? string.Empty;
            var scriptsForUi = automationDisplays.ToArray();

            ExecuteOnUiThread(() =>
            {
                LatestAutomationScriptSummary = summaryForUi;
                LatestAutomationScriptError = errorForUi;
                LatestAutomationScriptWarnings.Clear();
                foreach (var warning in distinctWarnings)
                {
                    LatestAutomationScriptWarnings.Add(warning);
                }

                LatestAutomationScripts.Clear();
                foreach (var script in scriptsForUi)
                {
                    LatestAutomationScripts.Add(script);
                }
            });

            cancellationToken.ThrowIfCancellationRequested();

            string? deltaNarrativePath = null;
            string? deltaRelativePath = null;
            string? deltaDataPath = null;

            if (!string.IsNullOrWhiteSpace(runSnapshotJson))
            {
                var previousRunEntry = await LoadLatestRunHistoryEntryAsync(historyDirectory);
                var previousSnapshotJson = previousRunEntry?.SnapshotJson;
                var previousWarnings = previousRunEntry?.AutomationWarnings ?? Array.Empty<string>();

                var runDeltaJson = BuildRunDeltaJson(
                    runSnapshotJson,
                    previousSnapshotJson,
                    distinctWarnings,
                    previousWarnings,
                    timestamp,
                    previousRunEntry?.RunTimestamp);

                if (!string.IsNullOrWhiteSpace(runDeltaJson))
                {
                    if (previousSnapshotJson is null)
                    {
                        Append("No previous run snapshot found; establishing baseline for future comparisons.");
                    }

                    deltaDataPath = Path.Combine(storeOutputFolder, RunDeltaDataFileName);
                    await File.WriteAllTextAsync(deltaDataPath, runDeltaJson, Encoding.UTF8, cancellationToken);
                    Append($"Run delta data: {deltaDataPath}");

                    string? aiDeltaNarrative = null;
                    if (chatSession is not null)
                    {
                        try
                        {
                            aiDeltaNarrative = await _chatAssistantService.SummarizeRunDeltaAsync(
                                chatSession,
                                runDeltaJson,
                                cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            Append($"AI run delta summary failed: {ex.Message}");
                        }

                        UpdateChatUsageTotals(chatSession);
                    }

                    if (string.IsNullOrWhiteSpace(aiDeltaNarrative))
                    {
                        aiDeltaNarrative = CreateFallbackRunDeltaNarrative(runDeltaJson, chatSession is null);
                    }

                    if (!string.IsNullOrWhiteSpace(aiDeltaNarrative))
                    {
                        var trimmedDelta = aiDeltaNarrative.Trim();
                        deltaNarrativePath = Path.Combine(storeOutputFolder, RunDeltaNarrativeFileName);
                        await File.WriteAllTextAsync(deltaNarrativePath, trimmedDelta, Encoding.UTF8, cancellationToken);
                        Append($"Run delta summary: {deltaNarrativePath}");
                        deltaRelativePath = TryGetStoreRelativePath(storeOutputFolder, deltaNarrativePath)
                            ?? RunDeltaNarrativeFileName;
                        _latestRunDeltaPath = deltaNarrativePath;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(deltaRelativePath))
            {
                reportContext = reportContext with { RunDeltaNarrativeRelativePath = deltaRelativePath };
            }

            cancellationToken.ThrowIfCancellationRequested();

            var entityCounts = BuildEntityCounts(
                prods,
                orders,
                mediaLibrary,
                designSnapshot,
                designScreenshots,
                publicExtensionFootprints);
            var fileSystemStats = CaptureOutputFileSystemStats(storeOutputFolder);

            reportContext = reportContext with
            {
                EntityCounts = entityCounts,
                FileSystemStats = fileSystemStats
            };

            runSnapshotJson = ManualMigrationRunSummaryFactory.CreateSnapshotJson(reportContext);
            var refreshedSnapshotJson = runSnapshotJson;
            ExecuteOnUiThread(() => LatestRunSnapshotJson = refreshedSnapshotJson);

            var reportBuilder = new ManualMigrationReportBuilder(_chatAssistantService);
            var reportResult = await reportBuilder.BuildAsync(reportContext, chatSession, cancellationToken);
            var report = reportResult.ReportMarkdown;
            var reportPath = Path.Combine(storeOutputFolder, "manual-migration-report.md");
            await File.WriteAllTextAsync(reportPath, report, Encoding.UTF8, cancellationToken);
            Append($"Manual migration report: {reportPath}");
            _latestManualReportPath = reportPath;

            UpdateAiRecommendations(reportResult.Annotation);
            if (!string.IsNullOrWhiteSpace(reportResult.AnnotationError))
            {
                Append($"AI recommendations unavailable: {reportResult.AnnotationError}");
            }

            var manualBundleArchivePath = TryCreateManualBundle(
                storeOutputFolder,
                baseOutputFolder,
                storeId,
                timestamp,
                reportPath,
                cancellationToken);
            _latestManualBundlePath = string.IsNullOrWhiteSpace(manualBundleArchivePath) ? null : manualBundleArchivePath;
            if (!string.IsNullOrWhiteSpace(manualBundleArchivePath))
            {
                Append($"Manual bundle archive: {manualBundleArchivePath}");
                await TryAnnotateManualReportAsync(reportPath, manualBundleArchivePath, cancellationToken);
            }

            if (chatSession is not null)
            {
                try
                {
                    var verificationResult = await _chatAssistantService.VerifyExportsAsync(
                            chatSession,
                            reportContext,
                            reportContext.FileSystemStats?.Directories ?? Array.Empty<ManualMigrationDirectorySnapshot>(),
                            cancellationToken);

                    if (verificationResult is not null)
                    {
                        exportVerificationSummary = verificationResult.Summary;
                        exportVerificationAlerts = BuildExportVerificationAlerts(verificationResult);
                        hasCriticalExportFindings = verificationResult.Issues.Any(issue => issue.IsCritical);

                        var verificationMarkdown = BuildExportVerificationMarkdown(verificationResult);
                        exportVerificationPath = Path.Combine(storeOutputFolder, ExportVerificationFileName);
                        await File.WriteAllTextAsync(exportVerificationPath, verificationMarkdown, Encoding.UTF8, cancellationToken);
                        Append($"AI export verification: {exportVerificationPath}");
                        Append($"AI export verification summary: {verificationResult.Summary}");

                        if (verificationResult.Issues.Count > 0)
                        {
                            if (hasCriticalExportFindings)
                            {
                                Append("⚠️ AI export verification flagged critical issues:");
                            }
                            else
                            {
                                Append("AI export verification found potential issues:");
                            }

                            foreach (var issue in verificationResult.Issues)
                            {
                                Append($"  - [{issue.Severity}] {issue.Title}: {issue.Description}");
                                if (!string.IsNullOrWhiteSpace(issue.Recommendation))
                                {
                                    Append($"    Recommendation: {issue.Recommendation}");
                                }
                            }
                        }
                        else
                        {
                            Append("AI export verification completed with no issues detected.");
                        }

                        if (verificationResult.SuggestedFixes.Count > 0)
                        {
                            Append("Suggested fixes:");
                            foreach (var fix in verificationResult.SuggestedFixes)
                            {
                                if (!string.IsNullOrWhiteSpace(fix))
                                {
                                    Append("  - " + fix);
                                }
                            }
                        }

                        if (verificationResult.SuggestedDirectives is not null)
                        {
                            Append("Processing export verification directives…");
                            ProcessAssistantDirectives(verificationResult.SuggestedDirectives, confirmed: false);
                        }
                    }
                    else
                    {
                        Append("AI export verification returned no structured findings.");
                    }
                    }
                    catch (Exception ex)
                    {
                        Append($"AI export verification failed: {ex.Message}");
                    }

                UpdateChatUsageTotals(chatSession);
            }

            cancellationToken.ThrowIfCancellationRequested();

            string? aiBriefPath = null;
            if (chatSession is not null)
            {
                try
                {
                    var aiNarrative = await _chatAssistantService.SummarizeRunAsync(chatSession, runSnapshotJson, runGoals, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(aiNarrative))
                    {
                        var trimmedNarrative = aiNarrative.Trim();
                        aiBriefPath = Path.Combine(storeOutputFolder, "manual-migration-report.ai.md");
                        await File.WriteAllTextAsync(aiBriefPath, trimmedNarrative, Encoding.UTF8, cancellationToken);
                        Append($"AI migration brief: {aiBriefPath}");
                        var capturedPath = aiBriefPath;
                        ExecuteOnUiThread(() =>
                        {
                            LatestRunAiNarrative = trimmedNarrative;
                            LatestRunAiBriefPath = capturedPath;
                        });
                    }
                }
                catch (Exception ex)
                {
                    Append($"AI migration brief failed: {ex.Message}");
                    ExecuteOnUiThread(() =>
                    {
                        LatestRunAiNarrative = string.Empty;
                        LatestRunAiBriefPath = null;
                    });
                }

                UpdateChatUsageTotals(chatSession);
            }

            if (!string.IsNullOrWhiteSpace(runSnapshotJson))
            {
                try
                {
                    await PersistRunSnapshotHistoryAsync(
                        historyDirectory,
                        timestamp,
                        runSnapshotJson,
                        distinctWarnings,
                        deltaNarrativePath,
                        deltaDataPath,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    Append($"Failed to persist run snapshot history: {ex.Message}");
                }
            }

            SetProvisioningContext(prods, variations, configuration, pluginBundles, themeBundles, customers, coupons, orders, subscriptions, siteContent, categoryTerms);

            Append("All done.");
            _activeRunPlanOutcome ??= new RunPlanExecutionOutcome(true, "Run completed successfully.");

            var completionInfo = new ManualRunCompletionInfo
            {
                StoreIdentifier = storeId,
                StoreUrl = targetUrl,
                ReportPath = reportPath,
                ManualBundlePath = manualBundleArchivePath,
                AiBriefPath = aiBriefPath,
                RunDeltaPath = deltaNarrativePath,
                ExportVerificationPath = exportVerificationPath,
                ExportVerificationSummary = exportVerificationSummary,
                ExportVerificationAlerts = exportVerificationAlerts,
                HasCriticalExportFindings = hasCriticalExportFindings,
                AskFollowUp = string.IsNullOrWhiteSpace(runSnapshotJson) ? null : LaunchRunFollowUpPanel
            };

            ExecuteOnUiThread(() => _dialogs.ShowRunCompletionDialog(completionInfo));
        }
        catch (OperationCanceledException)
        {
            _activeRunPlanOutcome ??= RunPlanExecutionOutcome.CreateCancelled("Run cancelled.");
            Append("Run cancelled.");
            if (_activeRunPlan is not null)
            {
                throw;
            }
        }
        catch (Exception ex)
        {
            _activeRunPlanOutcome ??= new RunPlanExecutionOutcome(false, ex.Message);
            Append($"Error: {ex.Message}");
        }
        finally
        {
            var runCts = _runCts;
            if (runCts is not null)
            {
                _runCts = null;
                runCts.Dispose();
            }

            IsRunning = false;
            RaiseRunCommandStates();
        }
    }

    private async Task<StoreConfiguration> FetchStoreConfigurationAsync(
        string baseUrl,
        string username,
        string applicationPassword,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var configuration = new StoreConfiguration();
        var progress = RequireProgressLogger(logger);

        var settings = await _wooScraper.FetchStoreSettingsAsync(baseUrl, username, applicationPassword, progress, cancellationToken);
        if (settings.Count > 0)
        {
            configuration.StoreSettings.AddRange(settings);
            logger.Report($"Captured {settings.Count} store settings entries.");
        }
        else
        {
            logger.Report("No store settings returned or endpoint unavailable.");
        }

        var zones = await _wooScraper.FetchShippingZonesAsync(baseUrl, username, applicationPassword, progress, cancellationToken);
        if (zones.Count > 0)
        {
            configuration.ShippingZones.AddRange(zones);
            logger.Report($"Captured {zones.Count} shipping zones.");
        }
        else
        {
            logger.Report("No shipping zones returned or endpoint unavailable.");
        }

        var gateways = await _wooScraper.FetchPaymentGatewaysAsync(baseUrl, username, applicationPassword, progress, cancellationToken);
        if (gateways.Count > 0)
        {
            configuration.PaymentGateways.AddRange(gateways);
            logger.Report($"Captured {gateways.Count} payment gateways.");
        }
        else
        {
            logger.Report("No payment gateways returned or endpoint unavailable.");
        }

        return configuration;
    }

    private static bool HasConfigurationData(StoreConfiguration configuration)
    {
        return configuration.StoreSettings.Count > 0
            || configuration.ShippingZones.Count > 0
            || configuration.PaymentGateways.Count > 0;
    }

    private async Task<List<ExtensionArtifact>> CapturePluginBundlesAsync(
        string baseUrl,
        string username,
        string applicationPassword,
        List<InstalledPlugin> plugins,
        string rootFolder,
        IReadOnlyDictionary<string, JsonElement>? settingsSnapshot,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var bundles = new List<ExtensionArtifact>();
        Directory.CreateDirectory(rootFolder);
        var progress = RequireProgressLogger(logger);

        foreach (var plugin in plugins)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slug = ResolvePluginSlug(plugin);
            var folderName = SanitizeForPath(slug ?? plugin.Name ?? plugin.PluginFile ?? Guid.NewGuid().ToString("N"));
            var bundleFolder = Path.Combine(rootFolder, folderName);
            Directory.CreateDirectory(bundleFolder);

            try
            {
                plugin.OptionData.Clear();
                plugin.OptionKeys.Clear();
                plugin.AssetManifest = null;
                plugin.AssetPaths.Clear();

                var options = await _wooScraper.FetchPluginOptionsAsync(baseUrl, username, applicationPassword, plugin, progress, settingsSnapshot, cancellationToken);
                if (options.Count > 0)
                {
                    foreach (var kvp in options)
                    {
                        var node = TryCloneJsonNode(kvp.Value);
                        if (node is not null || kvp.Value.ValueKind == JsonValueKind.Null)
                        {
                            plugin.OptionData[kvp.Key] = node;
                        }
                    }

                    if (plugin.OptionData.Count > 0)
                    {
                        var optionsPath = Path.Combine(bundleFolder, "options.json");
                        var json = JsonSerializer.Serialize(plugin.OptionData, _artifactWriteOptions);
                        await File.WriteAllTextAsync(optionsPath, json, Encoding.UTF8, cancellationToken);
                        var keys = plugin.OptionData.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
                        plugin.OptionKeys.AddRange(keys);
                        logger.Report($"Captured {keys.Count} options for plugin {plugin.Name ?? slug ?? plugin.PluginFile ?? folderName}.");
                    }
                }

                var manifest = await _wooScraper.FetchPluginAssetManifestAsync(baseUrl, username, applicationPassword, plugin, progress, cancellationToken);
                if (manifest.Paths.Count > 0 || !string.IsNullOrWhiteSpace(manifest.ManifestJson))
                {
                    if (manifest.Paths.Count > 0)
                    {
                        plugin.AssetPaths.AddRange(manifest.Paths);
                    }

                    if (!string.IsNullOrWhiteSpace(manifest.ManifestJson))
                    {
                        plugin.AssetManifest = TryParseJsonNode(manifest.ManifestJson);
                        if (plugin.AssetManifest is not null)
                        {
                            var manifestPath = Path.Combine(bundleFolder, "manifest.json");
                            var manifestJson = plugin.AssetManifest.ToJsonString(_artifactWriteOptions);
                            await File.WriteAllTextAsync(manifestPath, manifestJson, Encoding.UTF8, cancellationToken);
                        }
                    }

                    if (plugin.AssetPaths.Count > 0)
                    {
                        plugin.AssetPaths.Clear();
                        plugin.AssetPaths.AddRange(manifest.Paths.Distinct(StringComparer.OrdinalIgnoreCase));
                    }

                    var assetCount = plugin.AssetPaths.Count;
                    if (plugin.AssetManifest is not null)
                    {
                        logger.Report($"Captured asset manifest for plugin {plugin.Name ?? slug ?? plugin.PluginFile ?? folderName} ({assetCount} references).");
                    }
                    else if (assetCount > 0)
                    {
                        logger.Report($"Captured {assetCount} asset references for plugin {plugin.Name ?? slug ?? plugin.PluginFile ?? folderName}.");
                    }
                }

                var archivePath = Path.Combine(bundleFolder, "archive.zip");
                var downloaded = await _wooScraper.DownloadPluginArchiveAsync(baseUrl, username, applicationPassword, plugin, archivePath, progress, cancellationToken);
                if (!downloaded && File.Exists(archivePath))
                {
                    File.Delete(archivePath);
                }
            }
            catch (Exception ex)
            {
                logger.Report($"Failed to capture plugin bundle for {plugin.Name ?? slug ?? plugin.PluginFile ?? folderName}: {ex.Message}");
            }

            var artifactSlug = slug ?? plugin.Slug ?? plugin.PluginFile ?? folderName;
            bundles.Add(new ExtensionArtifact(artifactSlug, bundleFolder));
        }

        return bundles;
    }

    private async Task<List<ExtensionArtifact>> CaptureThemeBundlesAsync(
        string baseUrl,
        string username,
        string applicationPassword,
        List<InstalledTheme> themes,
        string rootFolder,
        IReadOnlyDictionary<string, JsonElement>? settingsSnapshot,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var bundles = new List<ExtensionArtifact>();
        Directory.CreateDirectory(rootFolder);
        var progress = RequireProgressLogger(logger);

        foreach (var theme in themes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slug = ResolveThemeSlug(theme);
            var folderName = SanitizeForPath(slug ?? theme.Name ?? theme.Stylesheet ?? Guid.NewGuid().ToString("N"));
            var bundleFolder = Path.Combine(rootFolder, folderName);
            Directory.CreateDirectory(bundleFolder);

            try
            {
                theme.OptionData.Clear();
                theme.OptionKeys.Clear();
                theme.AssetManifest = null;
                theme.AssetPaths.Clear();

                var options = await _wooScraper.FetchThemeOptionsAsync(baseUrl, username, applicationPassword, theme, progress, settingsSnapshot, cancellationToken);
                if (options.Count > 0)
                {
                    foreach (var kvp in options)
                    {
                        var node = TryCloneJsonNode(kvp.Value);
                        if (node is not null || kvp.Value.ValueKind == JsonValueKind.Null)
                        {
                            theme.OptionData[kvp.Key] = node;
                        }
                    }

                    if (theme.OptionData.Count > 0)
                    {
                        var optionsPath = Path.Combine(bundleFolder, "options.json");
                        var json = JsonSerializer.Serialize(theme.OptionData, _artifactWriteOptions);
                        await File.WriteAllTextAsync(optionsPath, json, Encoding.UTF8, cancellationToken);
                        var keys = theme.OptionData.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
                        theme.OptionKeys.AddRange(keys);
                        logger.Report($"Captured {keys.Count} options for theme {theme.Name ?? slug ?? folderName}.");
                    }
                }

                var manifest = await _wooScraper.FetchThemeAssetManifestAsync(baseUrl, username, applicationPassword, theme, progress, cancellationToken);
                if (manifest.Paths.Count > 0 || !string.IsNullOrWhiteSpace(manifest.ManifestJson))
                {
                    if (manifest.Paths.Count > 0)
                    {
                        theme.AssetPaths.AddRange(manifest.Paths);
                    }

                    if (!string.IsNullOrWhiteSpace(manifest.ManifestJson))
                    {
                        theme.AssetManifest = TryParseJsonNode(manifest.ManifestJson);
                        if (theme.AssetManifest is not null)
                        {
                            var manifestPath = Path.Combine(bundleFolder, "manifest.json");
                            var manifestJson = theme.AssetManifest.ToJsonString(_artifactWriteOptions);
                            await File.WriteAllTextAsync(manifestPath, manifestJson, Encoding.UTF8, cancellationToken);
                        }
                    }

                    if (theme.AssetPaths.Count > 0)
                    {
                        theme.AssetPaths.Clear();
                        theme.AssetPaths.AddRange(manifest.Paths.Distinct(StringComparer.OrdinalIgnoreCase));
                    }

                    var assetCount = theme.AssetPaths.Count;
                    if (theme.AssetManifest is not null)
                    {
                        logger.Report($"Captured asset manifest for theme {theme.Name ?? slug ?? folderName} ({assetCount} references).");
                    }
                    else if (assetCount > 0)
                    {
                        logger.Report($"Captured {assetCount} asset references for theme {theme.Name ?? slug ?? folderName}.");
                    }
                }

                var archivePath = Path.Combine(bundleFolder, "archive.zip");
                var downloaded = await _wooScraper.DownloadThemeArchiveAsync(baseUrl, username, applicationPassword, theme, archivePath, progress, cancellationToken);
                if (!downloaded && File.Exists(archivePath))
                {
                    File.Delete(archivePath);
                }
            }
            catch (Exception ex)
            {
                logger.Report($"Failed to capture theme bundle for {theme.Name ?? slug ?? folderName}: {ex.Message}");
            }

            var artifactSlug = slug ?? theme.Slug ?? theme.Stylesheet ?? folderName;
            bundles.Add(new ExtensionArtifact(artifactSlug, bundleFolder));
        }

        return bundles;
    }

    private static string? ResolvePluginSlug(InstalledPlugin plugin)
    {
        if (!string.IsNullOrWhiteSpace(plugin.Slug))
        {
            return plugin.Slug;
        }

        if (!string.IsNullOrWhiteSpace(plugin.PluginFile))
        {
            var pluginFile = plugin.PluginFile;
            var slash = pluginFile.IndexOf('/');
            if (slash > 0)
            {
                return pluginFile[..slash];
            }

            if (pluginFile.EndsWith(".php", StringComparison.OrdinalIgnoreCase))
            {
                return pluginFile[..^4];
            }
        }

        return string.IsNullOrWhiteSpace(plugin.Name) ? null : SanitizeForPath(plugin.Name);
    }

    private static string? ResolveThemeSlug(InstalledTheme theme)
    {
        if (!string.IsNullOrWhiteSpace(theme.Slug))
        {
            return theme.Slug;
        }

        if (!string.IsNullOrWhiteSpace(theme.Stylesheet))
        {
            return theme.Stylesheet;
        }

        if (!string.IsNullOrWhiteSpace(theme.Template))
        {
            return theme.Template;
        }

        return string.IsNullOrWhiteSpace(theme.Name) ? null : SanitizeForPath(theme.Name);
    }

    private async Task<string?> DownloadProductImagesAsync(
        StoreProduct product,
        string imagesFolder,
        ILogger? logger,
        IDictionary<string, MediaReference>? mediaMap,
        CancellationToken cancellationToken)
    {
        product.LocalImageFilePaths.Clear();
        if (product.Images is null || product.Images.Count == 0)
        {
            return null;
        }

        var baseName = SanitizeForPath(product.Name ?? product.Slug ?? $"product-{product.Id}");
        var relativePaths = new List<string>();
        var absolutePaths = new List<string>();
        var index = 1;

        foreach (var image in product.Images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var src = image.Src;
            if (string.IsNullOrWhiteSpace(src))
            {
                continue;
            }

            if (mediaMap is not null && mediaMap.TryGetValue(src, out var reference))
            {
                relativePaths.Add(reference.RelativePath);
                absolutePaths.Add(reference.AbsolutePath);
                continue;
            }

            if (!Uri.TryCreate(src, UriKind.Absolute, out var uri))
            {
                logger?.Report($"Skipping image for product {product.Id}: invalid URL '{src}'.");
                continue;
            }

            var extension = Path.GetExtension(uri.AbsolutePath);
            if (string.IsNullOrWhiteSpace(extension) || extension.Length > 5)
            {
                extension = ".jpg";
            }

            var fileName = $"{baseName}-{index}{extension}";
            var absolutePath = Path.Combine(imagesFolder, fileName);
            var relativePath = NormalizeRelativePath(Path.Combine("images", fileName));

            try
            {
                logger?.Report($"Downloading {src} -> {relativePath}");
                var bytes = await _httpClient.GetByteArrayAsync(uri, cancellationToken);
                await File.WriteAllBytesAsync(absolutePath, bytes, cancellationToken);
                relativePaths.Add(relativePath);
                absolutePaths.Add(absolutePath);
                if (mediaMap is not null)
                {
                    mediaMap[src] = new MediaReference(relativePath, absolutePath);
                }
                index++;
            }
            catch (Exception ex)
            {
                logger?.Report($"Failed to download {src}: {ex.Message}");
            }
        }

        if (absolutePaths.Count > 0)
        {
            product.LocalImageFilePaths.Clear();
            product.LocalImageFilePaths.AddRange(absolutePaths);
        }

        return relativePaths.Count > 0 ? string.Join(", ", relativePaths) : null;
    }

    private async Task<Dictionary<string, MediaReference>> DownloadMediaLibraryAsync(
        List<WordPressMediaItem> mediaItems,
        string mediaRoot,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, MediaReference>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in mediaItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(item.SourceUrl))
            {
                continue;
            }

            var relativeWithinMedia = BuildMediaRelativePath(item);
            if (string.IsNullOrWhiteSpace(relativeWithinMedia))
            {
                continue;
            }

            var absolutePath = Path.Combine(mediaRoot, relativeWithinMedia);
            var directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var relativePath = NormalizeRelativePath(Path.Combine("media", relativeWithinMedia));

            if (!File.Exists(absolutePath))
            {
                try
                {
                    logger?.Report($"Downloading {item.SourceUrl} -> {relativePath}");
                    using var response = await _httpClient.GetAsync(item.SourceUrl, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        logger?.Report($"Failed to download {item.SourceUrl}: {(int)response.StatusCode} ({response.ReasonPhrase}).");
                        continue;
                    }

                    await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                    await using var output = File.Create(absolutePath);
                    await input.CopyToAsync(output, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger?.Report($"Failed to download {item.SourceUrl}: {ex.Message}");
                    continue;
                }
            }

            item.LocalFilePath = absolutePath;
            item.RelativeFilePath = relativePath;
            map[item.SourceUrl] = new MediaReference(relativePath, absolutePath);
        }

        return map;
    }

    private async Task PopulateContentMediaReferencesAsync(
        List<WordPressContentBase> contentItems,
        List<WordPressMediaItem> mediaLibrary,
        string mediaRoot,
        IDictionary<string, MediaReference> mediaMap,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var existingByUrl = mediaLibrary
            .Where(m => !string.IsNullOrWhiteSpace(m.SourceUrl))
            .GroupBy(m => m.SourceUrl!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var content in contentItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (content is null)
            {
                continue;
            }

            content.ReferencedMediaFiles.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var url in content.ReferencedMediaUrls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(url) || !seen.Add(url))
                {
                    continue;
                }

                var reference = await EnsureMediaReferenceAsync(url, mediaRoot, mediaMap, logger, cancellationToken);
                if (reference is null)
                {
                    continue;
                }

                content.ReferencedMediaFiles.Add(reference.RelativePath);
                EnsureMediaLibraryEntry(url, reference, existingByUrl, mediaLibrary);
            }

            if (!string.IsNullOrWhiteSpace(content.FeaturedMediaUrl))
            {
                var reference = await EnsureMediaReferenceAsync(content.FeaturedMediaUrl!, mediaRoot, mediaMap, logger, cancellationToken);
                if (reference is not null)
                {
                    content.FeaturedMediaFile = reference.RelativePath;
                    EnsureMediaLibraryEntry(content.FeaturedMediaUrl!, reference, existingByUrl, mediaLibrary);
                }
                else
                {
                    content.FeaturedMediaFile = null;
                }
            }
            else
            {
                content.FeaturedMediaFile = null;
            }
        }
    }

    private async Task EnrichPublicExtensionFootprintsAsync(
        IList<PublicExtensionFootprint> footprints,
        ILogger log,
        CancellationToken cancellationToken)
    {
        if (footprints.Count == 0)
        {
            return;
        }

        var cache = new Dictionary<string, WordPressDirectoryEntry?>(StringComparer.OrdinalIgnoreCase);

        foreach (var footprint in footprints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slug = footprint.Slug;
            if (string.IsNullOrWhiteSpace(slug))
            {
                ApplyDirectoryMetadata(footprint, null, "missing_slug");
                continue;
            }

            if (string.Equals(footprint.Type, "mu-plugin", StringComparison.OrdinalIgnoreCase))
            {
                ApplyDirectoryMetadata(footprint, null, "skipped_mu_plugin");
                log.Report($"Skipping WordPress.org lookup for mu-plugin slug '{slug}' (must-use plugins are not listed in the public directory).");
                continue;
            }

            if (!WordPressDirectoryClient.IsLikelyDirectorySlug(slug))
            {
                ApplyDirectoryMetadata(footprint, null, "skipped_non_directory_slug");
                log.Report($"Skipping WordPress.org lookup for {footprint.Type} slug '{slug}' (unlikely to exist in the public directory).");
                continue;
            }

            var cacheKey = $"{footprint.Type}:{slug}";
            if (cache.TryGetValue(cacheKey, out var cachedEntry))
            {
                ApplyDirectoryMetadata(footprint, cachedEntry, cachedEntry is null ? "not_found" : "resolved");
                continue;
            }

            WordPressDirectoryEntry? entry = null;
            try
            {
                log.Report($"Looking up {footprint.Type} slug '{slug}' on WordPress.org…");
                entry = footprint.Type.Equals("theme", StringComparison.OrdinalIgnoreCase)
                    ? await _wpDirectoryClient.GetThemeAsync(slug, cancellationToken).ConfigureAwait(false)
                    : await _wpDirectoryClient.GetPluginAsync(slug, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                ApplyDirectoryMetadata(footprint, null, "lookup_error");
                log.Report($"WordPress.org lookup failed for slug '{slug}': {ex.Message}");
                cache[cacheKey] = null;
                await Task.Delay(DirectoryLookupDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            cache[cacheKey] = entry;
            if (entry is null)
            {
                ApplyDirectoryMetadata(footprint, null, "not_found");
                log.Report($"No WordPress.org directory entry found for {footprint.Type} slug '{slug}'.");
            }
            else
            {
                ApplyDirectoryMetadata(footprint, entry, "resolved");
                var resolvedLabel = entry.Title ?? entry.Slug ?? slug;
                var versionLabel = string.IsNullOrWhiteSpace(entry.Version) ? string.Empty : $" v{entry.Version}";
                log.Report($"Resolved {footprint.Type} slug '{slug}' to {resolvedLabel}{versionLabel}.");
                if (!string.IsNullOrWhiteSpace(entry.Homepage))
                {
                    log.Report($" • Homepage: {entry.Homepage}");
                }
                if (!string.IsNullOrWhiteSpace(entry.DownloadUrl) &&
                    !string.Equals(entry.DownloadUrl, entry.Homepage, StringComparison.OrdinalIgnoreCase))
                {
                    log.Report($" • Download: {entry.DownloadUrl}");
                }
            }

            await Task.Delay(DirectoryLookupDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ApplyDirectoryMetadata(PublicExtensionFootprint footprint, WordPressDirectoryEntry? entry, string status)
    {
        footprint.DirectoryStatus = status;
        if (entry is null)
        {
            footprint.DirectoryTitle = null;
            footprint.DirectoryAuthor = null;
            footprint.DirectoryHomepage = null;
            footprint.DirectoryVersion = null;
            footprint.DirectoryDownloadUrl = null;
            return;
        }

        footprint.DirectoryTitle = entry.Title;
        footprint.DirectoryAuthor = entry.Author;
        footprint.DirectoryHomepage = entry.Homepage;
        footprint.DirectoryVersion = entry.Version;
        footprint.DirectoryDownloadUrl = entry.DownloadUrl;
    }

    private async Task<MediaReference?> EnsureMediaReferenceAsync(
        string url,
        string mediaRoot,
        IDictionary<string, MediaReference> mediaMap,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (mediaMap.TryGetValue(url, out var cached))
        {
            return cached;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var extension = Path.GetExtension(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 10)
        {
            extension = ".bin";
        }

        var baseName = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "asset";
        }
        baseName = SanitizeSegment(baseName);
        var hash = ComputeShortHash(url);
        var fileName = $"{baseName}-{hash}{extension}";

        var contentFolder = Path.Combine(mediaRoot, "content");
        Directory.CreateDirectory(contentFolder);

        var absolutePath = Path.Combine(contentFolder, fileName);
        var relativePath = NormalizeRelativePath(Path.Combine("media", "content", fileName));

        if (!File.Exists(absolutePath))
        {
            try
            {
                logger?.Report($"Downloading {url} -> {relativePath}");
                using var response = await _httpClient.GetAsync(uri, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    logger?.Report($"Failed to download {url}: {(int)response.StatusCode} ({response.ReasonPhrase}).");
                    return null;
                }

                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = File.Create(absolutePath);
                await input.CopyToAsync(output, cancellationToken);
            }
            catch (Exception ex)
            {
                logger?.Report($"Failed to download {url}: {ex.Message}");
                return null;
            }
        }

        var reference = new MediaReference(relativePath, absolutePath);
        mediaMap[url] = reference;
        return reference;
    }

    private void EnsureMediaLibraryEntry(
        string url,
        MediaReference reference,
        IDictionary<string, WordPressMediaItem> existing,
        List<WordPressMediaItem> mediaLibrary)
    {
        if (existing.TryGetValue(url, out var current))
        {
            current.RelativeFilePath ??= reference.RelativePath;
            current.LocalFilePath ??= reference.AbsolutePath;
            return;
        }

        var placeholder = CreateContentMediaPlaceholder(url, reference);
        mediaLibrary.Add(placeholder);
        existing[url] = placeholder;
    }

    private WordPressMediaItem CreateContentMediaPlaceholder(string url, MediaReference reference)
    {
        var fileName = Path.GetFileName(reference.AbsolutePath);
        var baseName = string.IsNullOrWhiteSpace(fileName)
            ? $"asset-{ComputeShortHash(url)}"
            : Path.GetFileNameWithoutExtension(fileName);
        baseName = SanitizeSegment(baseName);
        var extension = Path.GetExtension(fileName);
        var mimeType = GuessMimeTypeFromExtension(extension);
        var mediaType = GuessMediaType(mimeType);

        var item = new WordPressMediaItem
        {
            Id = 0,
            SourceUrl = url,
            Slug = baseName,
            Title = new WordPressRenderedText { Rendered = baseName },
            Status = "inherit",
            Type = "attachment",
            MediaType = mediaType,
            MimeType = mimeType,
            RelativeFilePath = reference.RelativePath,
            LocalFilePath = reference.AbsolutePath,
            MediaDetails = new WordPressMediaDetails
            {
                File = TrimMediaRoot(reference.RelativePath)
            }
        };

        item.Normalize();
        return item;
    }

    private string? TryCreateManualBundle(
        string storeOutputFolder,
        string baseOutputFolder,
        string storeId,
        string timestamp,
        string reportPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var deliverables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(reportPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            deliverables.Add(reportPath);
        }

        var designFolder = Path.Combine(storeOutputFolder, "design");
        if (Directory.Exists(designFolder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            deliverables.Add(designFolder);
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(storeOutputFolder, $"{storeId}_{timestamp}_*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var extension = Path.GetExtension(file);
                if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
                {
                    deliverables.Add(file);
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Store folder disappeared unexpectedly; treat as no deliverables.
        }
        catch (Exception ex)
        {
            Append($"Manual bundle packaging skipped: {ex.Message}");
            return null;
        }

        if (deliverables.Count == 0)
        {
            return null;
        }

        var stagingFolderName = $"{storeId}_{timestamp}_manual_bundle";
        var stagingFolder = Path.Combine(storeOutputFolder, stagingFolderName);

        try
        {
            if (Directory.Exists(stagingFolder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Delete(stagingFolder, recursive: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(stagingFolder);

            foreach (var item in deliverables)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(item))
                {
                    var destinationDirectory = Path.Combine(stagingFolder, Path.GetFileName(item));
                    CopyDirectory(item, destinationDirectory, cancellationToken);
                }
                else if (File.Exists(item))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var destinationFile = Path.Combine(stagingFolder, Path.GetFileName(item));
                    File.Copy(item, destinationFile, overwrite: true);
                }
            }

            var archivePath = Path.Combine(baseOutputFolder, $"{stagingFolderName}.zip");
            if (File.Exists(archivePath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(archivePath);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ZipFile.CreateFromDirectory(stagingFolder, archivePath);
            return archivePath;
        }
        catch (Exception ex)
        {
            Append($"Manual bundle packaging skipped: {ex.Message}");
            return null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingFolder))
                {
                    Directory.Delete(stagingFolder, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private static async Task TryAnnotateManualReportAsync(
        string reportPath,
        string archivePath,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(reportPath))
            {
                return;
            }

            var summaryNote = $"- **Archive:** `{archivePath}`";
            cancellationToken.ThrowIfCancellationRequested();
            var content = await File.ReadAllTextAsync(reportPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            if (content.Contains(summaryNote, StringComparison.Ordinal))
            {
                return;
            }

            const string sectionMarker = "\n\n##";
            var insertionIndex = content.IndexOf(sectionMarker, StringComparison.Ordinal);
            var insertionText = summaryNote + Environment.NewLine;
            string updatedContent;

            if (insertionIndex >= 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                updatedContent = content.Insert(insertionIndex, insertionText);
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                updatedContent = content + Environment.NewLine + insertionText;
            }

            await File.WriteAllTextAsync(reportPath, updatedContent, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort annotation; ignore failures.
        }
    }

    private void LaunchRunFollowUpPanel()
    {
        var snapshot = LatestRunSnapshotJson;
        var narrative = LatestRunAiNarrative;
        if (string.IsNullOrWhiteSpace(snapshot) && string.IsNullOrWhiteSpace(narrative))
        {
            ExecuteOnUiThread(() =>
            {
                IsAssistantPanelExpanded = true;
                ChatStatusMessage = "No run summary is available yet. Complete a run to ask follow-up questions.";
            });
            return;
        }

        PrepareAssistantForRunFollowUp(snapshot, narrative, _latestRunGoalsSnapshot, LatestRunAiBriefPath);
    }

    private void PrepareAssistantForRunFollowUp(string snapshotJson, string? narrative, string? goals, string? aiBriefPath)
    {
        ExecuteOnUiThread(() =>
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

    private static void CopyDirectory(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destinationDirectory);

        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetFile = Path.Combine(destinationDirectory, Path.GetFileName(file));
            File.Copy(file, targetFile, overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetDirectory = Path.Combine(destinationDirectory, Path.GetFileName(directory));
            CopyDirectory(directory, targetDirectory, cancellationToken);
        }
    }

    private void Append(string message)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() => Logs.Add(message));
    }

    private LoggerProgressAdapter CreateOperationLogger(
        string operationName,
        string? url = null,
        string? entityType = null,
        IReadOnlyDictionary<string, object?>? additionalContext = null,
        LogLevel level = LogLevel.Information)
    {
        var categoryName = string.IsNullOrWhiteSpace(operationName)
            ? typeof(MainViewModel).FullName ?? nameof(MainViewModel)
            : $"WcScraper.Wpf.Run.{operationName}";
        var logger = _loggerFactory.CreateLogger(categoryName);
        return LoggerProgressAdapter.ForOperation(logger, Append, operationName, url, entityType, additionalContext, level);
    }

    private static IProgress<string> RequireProgressLogger(ILogger logger)
    {
        if (logger is IProgress<string> progress)
        {
            return progress;
        }

        throw new ArgumentException("Logger must support progress reporting.", nameof(logger));
    }

    private static IProgress<string>? TryGetProgressLogger(ILogger? logger)
        => logger is IProgress<string> progress ? progress : null;

    private sealed class UiScraperInstrumentation : IScraperInstrumentation
    {
        private readonly Action<string> _append;
        private readonly IScraperInstrumentation _inner;

        public UiScraperInstrumentation(Action<string> append, ILoggerFactory loggerFactory)
        {
            _append = append ?? throw new ArgumentNullException(nameof(append));
            loggerFactory ??= NullLoggerFactory.Instance;
            _inner = ScraperInstrumentation.Create(new ScraperInstrumentationOptions
            {
                LoggerFactory = loggerFactory
            });
        }

        public IDisposable BeginScope(ScraperOperationContext context) => _inner.BeginScope(context);

        public void RecordRequestStart(ScraperOperationContext context)
        {
            _append(FormatMessage(context, "Starting", suffix: $"({FormatUrl(context)})"));
            _inner.RecordRequestStart(context);
        }

        public void RecordRequestSuccess(ScraperOperationContext context, TimeSpan duration, HttpStatusCode? statusCode = null, int retryCount = 0)
        {
            var statusLabel = statusCode is { } code ? ((int)code).ToString(CultureInfo.InvariantCulture) : "n/a";
            var message = $"Completed in {duration.TotalMilliseconds:F0}ms (status: {statusLabel}, retries: {retryCount})";
            _append(FormatMessage(context, "Completed", suffix: message));
            _inner.RecordRequestSuccess(context, duration, statusCode, retryCount);
        }

        public void RecordRequestFailure(ScraperOperationContext context, TimeSpan duration, Exception exception, HttpStatusCode? statusCode = null, int retryCount = 0)
        {
            var statusLabel = statusCode is { } code ? ((int)code).ToString(CultureInfo.InvariantCulture) : "n/a";
            var message = $"Failed after {duration.TotalMilliseconds:F0}ms (status: {statusLabel}, retries: {retryCount}): {exception.Message}";
            _append(FormatMessage(context, "Failed", suffix: message));
            _inner.RecordRequestFailure(context, duration, exception, statusCode, retryCount);
        }

        public void RecordRetry(ScraperOperationContext context)
        {
            if (context.RetryAttempt is not { } attempt)
            {
                _append(FormatMessage(context, "Retrying"));
            }
            else
            {
                var delayMs = context.RetryDelay?.TotalMilliseconds ?? 0d;
                var reason = string.IsNullOrWhiteSpace(context.RetryReason) ? "unspecified" : context.RetryReason;
                var message = $"Retrying in {delayMs:F0}ms (attempt {attempt}): {reason}";
                _append(FormatMessage(context, "Retrying", suffix: message));
            }

            _inner.RecordRetry(context);
        }

        private static string FormatEntity(ScraperOperationContext context) =>
            string.IsNullOrWhiteSpace(context.EntityType) ? "resource" : context.EntityType!;

        private static string FormatUrl(ScraperOperationContext context) =>
            string.IsNullOrWhiteSpace(context.Url) ? "n/a" : context.Url!;

        private static string FormatMessage(ScraperOperationContext context, string action, string? suffix = null)
        {
            var message = $"[{context.OperationName}] {action} {FormatEntity(context)}";
            if (!string.IsNullOrWhiteSpace(suffix))
            {
                message = $"{message} {suffix}";
            }

            return message;
        }
    }

    private static string BuildMediaRelativePath(WordPressMediaItem item)
    {
        var uploadPath = item.MediaDetails?.File;
        if (!string.IsNullOrWhiteSpace(uploadPath))
        {
            var parts = uploadPath
                .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(SanitizeSegment)
                .ToArray();
            if (parts.Length > 0)
            {
                return Path.Combine(parts);
            }
        }

        var fileName = item.GuessFileName();
        var baseName = string.IsNullOrWhiteSpace(fileName)
            ? $"media-{item.Id}"
            : Path.GetFileNameWithoutExtension(fileName);
        baseName = SanitizeSegment(baseName);
        var extension = string.IsNullOrWhiteSpace(fileName) ? ".bin" : Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 5)
        {
            extension = ".bin";
        }

        var folder = item.Date?.UtcDateTime.ToString("yyyy/MM") ?? "misc";
        var folderParts = folder
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SanitizeSegment)
            .ToArray();

        return Path.Combine(folderParts.Append($"{baseName}{extension}").ToArray());
    }

    private static readonly Dictionary<string, string> s_knownMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
        [".bmp"] = "image/bmp",
        [".ico"] = "image/vnd.microsoft.icon",
        [".heic"] = "image/heic",
        [".mp4"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".webm"] = "video/webm",
        [".ogv"] = "video/ogg",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg",
        [".m4a"] = "audio/mp4",
        [".pdf"] = "application/pdf",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".css"] = "text/css",
        [".js"] = "application/javascript",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".otf"] = "font/otf"
    };

    private static readonly Dictionary<string, string> s_knownExtensionsByMimeType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["text/css"] = ".css",
        ["application/css"] = ".css",
        ["font/woff"] = ".woff",
        ["font/woff2"] = ".woff2",
        ["font/ttf"] = ".ttf",
        ["font/otf"] = ".otf",
        ["application/font-woff"] = ".woff",
        ["application/font-woff2"] = ".woff2",
        ["application/x-font-ttf"] = ".ttf",
        ["application/x-font-opentype"] = ".otf",
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
        ["image/svg+xml"] = ".svg",
        ["image/x-icon"] = ".ico",
        ["image/vnd.microsoft.icon"] = ".ico"
    };

    private static string GuessMimeTypeFromExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return "application/octet-stream";
        }

        return s_knownMimeTypes.TryGetValue(extension, out var known)
            ? known
            : "application/octet-stream";
    }

    private static string GuessMediaType(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return "file";
        }

        if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return "image";
        }

        if (mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return "video";
        }

        if (mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
        {
            return "audio";
        }

        return "file";
    }

    private static string TrimMediaRoot(string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        return normalized.StartsWith("media/", StringComparison.OrdinalIgnoreCase)
            ? normalized["media/".Length..]
            : normalized;
    }

    private static string ComputeShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    private static string CreateDesignAssetFileName(string prefix, int index, string? url, string? contentType, string defaultExtension)
    {
        var extension = GetDesignAssetExtension(url, contentType, defaultExtension);
        var hashSeed = string.IsNullOrWhiteSpace(url)
            ? $"{prefix}:{index}"
            : url!;
        var hash = ComputeShortHash($"{prefix}|{index}|{hashSeed}");

        var stem = TryExtractFileNameStem(url);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = $"{prefix}-{index}";
        }

        var sanitized = SanitizeForPath(stem);
        if (string.IsNullOrWhiteSpace(sanitized) || string.Equals(sanitized, "store", StringComparison.OrdinalIgnoreCase))
        {
            sanitized = $"{prefix}-{index}";
        }

        return $"{sanitized}-{hash}{extension}";
    }

    private sealed record DesignAssetManifestCsvRow(
        string Type,
        string File,
        string? SourceUrl,
        string? ResolvedUrl,
        string? ReferencedFrom,
        string? ContentType,
        long FileSizeBytes,
        string Sha256,
        string? FontFamily,
        string? FontStyle,
        string? FontWeight,
        string? Rel,
        string? LinkType,
        string? Sizes,
        string? Color,
        string? Media,
        IReadOnlyList<string>? Origins,
        IReadOnlyList<string>? References);

    private static string GetDesignAssetExtension(string? url, string? contentType, string defaultExtension)
    {
        var fromUrl = TryExtractExtensionFromUrl(url);
        if (!string.IsNullOrWhiteSpace(fromUrl))
        {
            return fromUrl!;
        }

        if (!string.IsNullOrWhiteSpace(contentType) && s_knownExtensionsByMimeType.TryGetValue(contentType!, out var mapped))
        {
            return mapped;
        }

        return defaultExtension;
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var needsQuotes = value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
        var escaped = value.Replace("\"", "\"\"");
        return needsQuotes ? $"\"{escaped}\"" : escaped;
    }

    private static string? TryExtractExtensionFromUrl(string? url)
    {
        var fileName = TryExtractFileName(url);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(ext))
        {
            return null;
        }

        return ext.ToLowerInvariant();
    }

    private static string? TryExtractFileNameStem(string? url)
    {
        var fileName = TryExtractFileName(url);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        return Path.GetFileNameWithoutExtension(fileName);
    }

    private static string? TryExtractFileName(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var trimmed = url.Split('#')[0];
        trimmed = trimmed.Split('?')[0];

        if (Uri.TryCreate(trimmed, UriKind.RelativeOrAbsolute, out var uri))
        {
            trimmed = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString;
        }

        trimmed = trimmed.Replace("\\\\", "/");
        var lastSlash = trimmed.LastIndexOf('/');
        var segment = lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;
        return string.IsNullOrWhiteSpace(segment) ? null : segment;
    }

    private static JsonNode? TryCloneJsonNode(JsonElement element)
    {
        try
        {
            return JsonNode.Parse(element.GetRawText());
        }
        catch (JsonException)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Null => JsonValue.Create<string?>(null),
                JsonValueKind.String => JsonValue.Create(element.GetString()),
                JsonValueKind.True => JsonValue.Create(true),
                JsonValueKind.False => JsonValue.Create(false),
                JsonValueKind.Number => element.TryGetInt64(out var l)
                    ? JsonValue.Create(l)
                    : double.TryParse(element.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dbl)
                        ? JsonValue.Create(dbl)
                        : JsonValue.Create(element.GetRawText()),
                _ => null
            };
        }
    }

    private static JsonNode? TryParseJsonNode(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ResolveAutomationScriptFileName(MigrationAutomationScript script, int index, HashSet<string> usedNames)
    {
        if (usedNames is null)
        {
            throw new ArgumentNullException(nameof(usedNames));
        }

        var baseStem = string.Empty;
        var extension = GuessScriptExtension(script.Language);

        if (!string.IsNullOrWhiteSpace(script.FileName))
        {
            var candidate = Path.GetFileName(script.FileName.Trim());
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                var candidateStem = Path.GetFileNameWithoutExtension(candidate);
                if (!string.IsNullOrWhiteSpace(candidateStem))
                {
                    baseStem = SanitizeForPath(candidateStem);
                }

                var candidateExtension = Path.GetExtension(candidate);
                if (!string.IsNullOrWhiteSpace(candidateExtension))
                {
                    extension = candidateExtension;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(baseStem) && !string.IsNullOrWhiteSpace(script.Name))
        {
            baseStem = SanitizeForPath(script.Name);
        }

        if (string.IsNullOrWhiteSpace(baseStem) || baseStem.Equals("store", StringComparison.OrdinalIgnoreCase))
        {
            baseStem = $"script-{index + 1}";
        }

        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = GuessScriptExtension(script.Language);
        }

        if (!extension.StartsWith(".", StringComparison.Ordinal))
        {
            extension = "." + extension.Trim();
        }

        var candidateName = baseStem + extension;
        var attempt = 1;
        while (usedNames.Contains(candidateName))
        {
            attempt++;
            candidateName = $"{baseStem}-{attempt}{extension}";
        }

        usedNames.Add(candidateName);
        return candidateName;
    }

    private static string GuessScriptExtension(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return ".txt";
        }

        return language.Trim().ToLowerInvariant() switch
        {
            "shell" or "bash" or "sh" or "zsh" or "wp-cli" => ".sh",
            "powershell" or "ps" => ".ps1",
            "python" or "py" => ".py",
            "rest" or "http" => ".http",
            "json" => ".json",
            "php" => ".php",
            "js" or "javascript" => ".js",
            _ => ".txt"
        };
    }

    private static string? TryGetStoreRelativePath(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var relative = Path.GetRelativePath(root, path);
            return NormalizeRelativePath(relative);
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace("\\", "/");
    }

    private static string GetRunHistoryDirectory(string storeOutputFolder)
    {
        return Path.Combine(storeOutputFolder, RunSnapshotHistoryFolderName);
    }

    private async Task<RunHistoryEntry?> LoadLatestRunHistoryEntryAsync(string historyDirectory)
    {
        if (string.IsNullOrWhiteSpace(historyDirectory) || !Directory.Exists(historyDirectory))
        {
            return null;
        }

        try
        {
            var files = Directory.GetFiles(historyDirectory, "run-snapshot-*.json");
            if (files.Length == 0)
            {
                return null;
            }

            var latestFile = files
                .OrderByDescending(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
                .First();

            await using var stream = File.OpenRead(latestFile);
            var node = await JsonNode.ParseAsync(stream).ConfigureAwait(false);
            if (node is null)
            {
                return null;
            }

            string? snapshotJson = null;
            if (node["snapshot"] is JsonNode snapshotNode)
            {
                snapshotJson = snapshotNode.ToJsonString(_runHistoryWriteOptions);
            }
            else if (node["snapshotJson"] is JsonNode rawNode)
            {
                snapshotJson = rawNode.GetValue<string?>();
            }

            if (string.IsNullOrWhiteSpace(snapshotJson))
            {
                return null;
            }

            var warnings = new List<string>();
            if (node["automationWarnings"] is JsonArray warningsArray)
            {
                foreach (var entry in warningsArray)
                {
                    var text = entry?.GetValue<string?>();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        warnings.Add(text.Trim());
                    }
                }
            }

            var runTimestamp = node["runTimestamp"]?.GetValue<string?>()
                ?? Path.GetFileNameWithoutExtension(latestFile);

            DateTime? capturedAt = null;
            var timestampValue = node["timestampUtc"]?.GetValue<string?>();
            if (!string.IsNullOrWhiteSpace(timestampValue)
                && DateTime.TryParse(
                    timestampValue,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                capturedAt = parsed;
            }

            return new RunHistoryEntry(runTimestamp ?? string.Empty, capturedAt, snapshotJson, warnings);
        }
        catch
        {
            return null;
        }
    }

    private async Task PersistRunSnapshotHistoryAsync(
        string historyDirectory,
        string runTimestamp,
        string runSnapshotJson,
        IReadOnlyList<string> automationWarnings,
        string? deltaNarrativePath,
        string? deltaDataPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(historyDirectory)
            || string.IsNullOrWhiteSpace(runTimestamp)
            || string.IsNullOrWhiteSpace(runSnapshotJson))
        {
            return;
        }

        Directory.CreateDirectory(historyDirectory);

        JsonNode? snapshotNode;
        try
        {
            snapshotNode = JsonNode.Parse(runSnapshotJson);
        }
        catch
        {
            snapshotNode = null;
        }

        var root = new JsonObject
        {
            ["timestampUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["runTimestamp"] = runTimestamp
        };

        if (snapshotNode is not null)
        {
            root["snapshot"] = snapshotNode;
        }
        else
        {
            root["snapshotJson"] = runSnapshotJson;
        }

        var warningArray = new JsonArray();
        if (automationWarnings is not null)
        {
            foreach (var warning in automationWarnings)
            {
                if (!string.IsNullOrWhiteSpace(warning))
                {
                    warningArray.Add(warning.Trim());
                }
            }
        }

        root["automationWarnings"] = warningArray;

        if (!string.IsNullOrWhiteSpace(deltaNarrativePath))
        {
            root["deltaNarrativePath"] = deltaNarrativePath;
        }

        if (!string.IsNullOrWhiteSpace(deltaDataPath))
        {
            root["deltaDataPath"] = deltaDataPath;
        }

        var fileName = $"run-snapshot-{runTimestamp}.json";
        var filePath = Path.Combine(historyDirectory, fileName);
        await File.WriteAllTextAsync(filePath, root.ToJsonString(_runHistoryWriteOptions), Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);

        PruneRunSnapshotHistory(historyDirectory);
    }

    private static void PruneRunSnapshotHistory(string historyDirectory)
    {
        if (string.IsNullOrWhiteSpace(historyDirectory) || !Directory.Exists(historyDirectory))
        {
            return;
        }

        try
        {
            var files = Directory.GetFiles(historyDirectory, "run-snapshot-*.json");
            if (files.Length <= RunSnapshotHistoryLimit)
            {
                return;
            }

            var toDelete = files
                .OrderByDescending(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
                .Skip(RunSnapshotHistoryLimit)
                .ToList();

            foreach (var path in toDelete)
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // Ignore cleanup failures.
                }
            }
        }
        catch
        {
            // Ignore cleanup failures.
        }
    }

    private string BuildRunDeltaJson(
        string currentSnapshotJson,
        string? previousSnapshotJson,
        IReadOnlyList<string> currentWarnings,
        IReadOnlyList<string> previousWarnings,
        string currentTimestamp,
        string? previousTimestamp)
    {
        var current = TryParseRunSnapshot(currentSnapshotJson);
        if (current is null)
        {
            return JsonSerializer.Serialize(new { error = "Unable to parse current run snapshot." }, _runHistoryWriteOptions);
        }

        var previous = TryParseRunSnapshot(previousSnapshotJson);

        var pluginAdded = current.Plugins.Values
            .Where(plugin => previous is null || !previous.Plugins.ContainsKey(plugin.Slug))
            .Select(plugin => new { plugin.Slug, plugin.Name, plugin.Version, plugin.Status })
            .ToList();

        IEnumerable<object> pluginRemoved;
        if (previous is null)
        {
            pluginRemoved = Array.Empty<object>();
        }
        else
        {
            pluginRemoved = previous.Plugins.Values
                .Where(plugin => !current.Plugins.ContainsKey(plugin.Slug))
                .Select(plugin => new { plugin.Slug, plugin.Name, plugin.Version, plugin.Status })
                .Cast<object>()
                .ToArray();
        }

        var pluginUpdated = new List<object>();
        if (previous is not null)
        {
            foreach (var (slug, plugin) in current.Plugins)
            {
                if (previous.Plugins.TryGetValue(slug, out var oldPlugin))
                {
                    if (!string.Equals(plugin.Version, oldPlugin.Version, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(plugin.Status, oldPlugin.Status, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(plugin.Name, oldPlugin.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        pluginUpdated.Add(new
                        {
                            slug = plugin.Slug,
                            previous = new { oldPlugin.Name, oldPlugin.Version, oldPlugin.Status },
                            current = new { plugin.Name, plugin.Version, plugin.Status }
                        });
                    }
                }
            }
        }

        var themeAdded = current.Themes.Values
            .Where(theme => previous is null || !previous.Themes.ContainsKey(theme.Slug))
            .Select(theme => new { theme.Slug, theme.Name, theme.Version, theme.Status })
            .ToList();

        IEnumerable<object> themeRemoved;
        if (previous is null)
        {
            themeRemoved = Array.Empty<object>();
        }
        else
        {
            themeRemoved = previous.Themes.Values
                .Where(theme => !current.Themes.ContainsKey(theme.Slug))
                .Select(theme => new { theme.Slug, theme.Name, theme.Version, theme.Status })
                .Cast<object>()
                .ToArray();
        }

        var themeUpdated = new List<object>();
        if (previous is not null)
        {
            foreach (var (slug, theme) in current.Themes)
            {
                if (previous.Themes.TryGetValue(slug, out var oldTheme))
                {
                    if (!string.Equals(theme.Version, oldTheme.Version, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(theme.Status, oldTheme.Status, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(theme.Name, oldTheme.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        themeUpdated.Add(new
                        {
                            slug = theme.Slug,
                            previous = new { oldTheme.Name, oldTheme.Version, oldTheme.Status },
                            current = new { theme.Name, theme.Version, theme.Status }
                        });
                    }
                }
            }
        }

        var extensionAdded = current.PublicExtensions.Values
            .Where(extension => previous is null || !previous.PublicExtensions.ContainsKey(extension.Key))
            .Select(extension => new
            {
                extension.Slug,
                extension.Type,
                extension.VersionHint,
                extension.DirectoryVersion,
                extension.DirectoryStatus,
                extension.DirectoryTitle
            })
            .ToList();

        IEnumerable<object> extensionRemoved;
        if (previous is null)
        {
            extensionRemoved = Array.Empty<object>();
        }
        else
        {
            extensionRemoved = previous.PublicExtensions.Values
                .Where(extension => !current.PublicExtensions.ContainsKey(extension.Key))
                .Select(extension => new
                {
                    extension.Slug,
                    extension.Type,
                    extension.VersionHint,
                    extension.DirectoryVersion,
                    extension.DirectoryStatus,
                    extension.DirectoryTitle
                })
                .Cast<object>()
                .ToArray();
        }

        var extensionUpdated = new List<object>();
        if (previous is not null)
        {
            foreach (var (key, extension) in current.PublicExtensions)
            {
                if (previous.PublicExtensions.TryGetValue(key, out var oldExtension))
                {
                    if (!string.Equals(extension.VersionHint, oldExtension.VersionHint, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(extension.DirectoryVersion, oldExtension.DirectoryVersion, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(extension.DirectoryStatus, oldExtension.DirectoryStatus, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(extension.DirectoryTitle, oldExtension.DirectoryTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        extensionUpdated.Add(new
                        {
                            slug = extension.Slug,
                            type = extension.Type,
                            previous = new
                            {
                                oldExtension.VersionHint,
                                oldExtension.DirectoryVersion,
                                oldExtension.DirectoryStatus,
                                oldExtension.DirectoryTitle
                            },
                            current = new
                            {
                                extension.VersionHint,
                                extension.DirectoryVersion,
                                extension.DirectoryStatus,
                                extension.DirectoryTitle
                            }
                        });
                    }
                }
            }
        }

        var designChanges = BuildDesignChanges(current.Design, previous?.Design);

        var automationDiff = BuildRunDeltaSetDiff(currentWarnings ?? Array.Empty<string>(), previousWarnings ?? Array.Empty<string>());
        var credentialDiff = BuildRunDeltaSetDiff(current.MissingCredentialExports, previous?.MissingCredentialExports ?? Array.Empty<string>());
        var logDiff = BuildRunDeltaSetDiff(current.LogHighlights, previous?.LogHighlights ?? Array.Empty<string>());

        var diff = new
        {
            storeIdentifier = current.StoreIdentifier,
            storeUrl = current.StoreUrl,
            currentRun = new
            {
                timestamp = currentTimestamp,
                pluginCount = current.Plugins.Count,
                themeCount = current.Themes.Count,
                publicExtensionCount = current.PublicExtensions.Count,
                designSnapshotStatus = DescribeDesignSnapshotStatus(current.Design),
                designScreenshotCount = current.Design?.Screenshots.Count ?? 0
            },
            previousRun = previous is null
                ? null
                : new
                {
                    timestamp = previousTimestamp,
                    pluginCount = previous.Plugins.Count,
                    themeCount = previous.Themes.Count,
                    publicExtensionCount = previous.PublicExtensions.Count,
                    designSnapshotStatus = DescribeDesignSnapshotStatus(previous.Design),
                    designScreenshotCount = previous.Design?.Screenshots.Count ?? 0
                },
            changes = new
            {
                plugins = new { added = pluginAdded, removed = pluginRemoved, updated = pluginUpdated },
                themes = new { added = themeAdded, removed = themeRemoved, updated = themeUpdated },
                publicExtensions = new { added = extensionAdded, removed = extensionRemoved, updated = extensionUpdated },
                design = designChanges,
                automationWarnings = new
                {
                    added = automationDiff.Added,
                    resolved = automationDiff.Resolved,
                    current = automationDiff.Current,
                    previous = automationDiff.Previous
                },
                missingCredentialNotes = new
                {
                    added = credentialDiff.Added,
                    resolved = credentialDiff.Resolved,
                    current = credentialDiff.Current,
                    previous = credentialDiff.Previous
                },
                logHighlights = new
                {
                    added = logDiff.Added,
                    resolved = logDiff.Resolved,
                    current = logDiff.Current,
                    previous = logDiff.Previous
                }
            },
            notes = previous is null ? "Baseline run established (no prior history)." : null
        };

        return JsonSerializer.Serialize(diff, _runHistoryWriteOptions);
    }

    private static ManualMigrationEntityCounts BuildEntityCounts(
        IList<StoreProduct> products,
        IList<WooOrder> orders,
        IList<WordPressMediaItem> mediaItems,
        FrontEndDesignSnapshotResult? designSnapshot,
        IReadOnlyList<DesignScreenshot> designScreenshots,
        IReadOnlyList<PublicExtensionFootprint> publicExtensions)
    {
        var designAssetCount = 0;
        if (designSnapshot is not null)
        {
            designAssetCount += designSnapshot.Stylesheets?.Count ?? 0;
            designAssetCount += designSnapshot.FontFiles?.Count ?? 0;
            designAssetCount += designSnapshot.IconFiles?.Count ?? 0;
            designAssetCount += designSnapshot.ImageFiles?.Count ?? 0;
        }

        if (designScreenshots is not null)
        {
            designAssetCount += designScreenshots.Count;
        }

        return new ManualMigrationEntityCounts(
            products?.Count ?? 0,
            orders?.Count ?? 0,
            mediaItems?.Count ?? 0,
            designAssetCount,
            publicExtensions?.Count ?? 0);
    }

    private ManualMigrationFileSystemStats CaptureOutputFileSystemStats(string rootFolder)
    {
        if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder))
        {
            return new ManualMigrationFileSystemStats(Array.Empty<ManualMigrationDirectorySnapshot>(), 0, 0);
        }

        var snapshots = new List<ManualMigrationDirectorySnapshot>();

        try
        {
            var rootInfo = new DirectoryInfo(rootFolder);
            var rootTotals = CaptureDirectory(rootInfo, rootFolder, snapshots);

            snapshots.Sort((left, right) =>
            {
                var leftIsRoot = string.Equals(left.RelativePath, ".", StringComparison.Ordinal);
                var rightIsRoot = string.Equals(right.RelativePath, ".", StringComparison.Ordinal);
                if (leftIsRoot && !rightIsRoot)
                {
                    return -1;
                }

                if (!leftIsRoot && rightIsRoot)
                {
                    return 1;
                }

                return string.Compare(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase);
            });

            return new ManualMigrationFileSystemStats(snapshots, rootTotals.FileCount, rootTotals.TotalBytes);
        }
        catch
        {
            return new ManualMigrationFileSystemStats(Array.Empty<ManualMigrationDirectorySnapshot>(), 0, 0);
        }

        static (int FileCount, long TotalBytes) CaptureDirectory(
            DirectoryInfo directory,
            string root,
            ICollection<ManualMigrationDirectorySnapshot> results)
        {
            var fileCount = 0;
            long totalBytes = 0;

            FileInfo[] files;
            try
            {
                files = directory.GetFiles();
            }
            catch (IOException)
            {
                files = Array.Empty<FileInfo>();
            }
            catch (UnauthorizedAccessException)
            {
                files = Array.Empty<FileInfo>();
            }

            foreach (var file in files)
            {
                fileCount++;
                try
                {
                    totalBytes += file.Length;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            DirectoryInfo[] children;
            try
            {
                children = directory.GetDirectories();
            }
            catch (IOException)
            {
                children = Array.Empty<DirectoryInfo>();
            }
            catch (UnauthorizedAccessException)
            {
                children = Array.Empty<DirectoryInfo>();
            }

            foreach (var child in children)
            {
                var totals = CaptureDirectory(child, root, results);
                fileCount += totals.FileCount;
                totalBytes += totals.TotalBytes;
            }

            var relative = Path.GetRelativePath(root, directory.FullName);
            if (string.IsNullOrEmpty(relative) || relative == ".")
            {
                relative = ".";
            }

            var snapshot = new ManualMigrationDirectorySnapshot(directory.FullName, relative, fileCount, totalBytes);
            results.Add(snapshot);
            return (fileCount, totalBytes);
        }
    }

    private static string[] BuildExportVerificationAlerts(ExportVerificationResult result)
    {
        if (result.Issues.Count == 0)
        {
            return Array.Empty<string>();
        }

        return result.Issues
            .Select(issue =>
            {
                var severity = issue.IsCritical
                    ? "Critical"
                    : issue.IsWarning
                        ? "Warning"
                        : (string.IsNullOrWhiteSpace(issue.Severity) ? "Info" : issue.Severity.Trim());

                var messageBuilder = new StringBuilder();
                messageBuilder.Append(severity);
                messageBuilder.Append(':');
                messageBuilder.Append(' ');
                messageBuilder.Append(issue.Title);

                if (!string.IsNullOrWhiteSpace(issue.Description))
                {
                    messageBuilder.Append(" — ");
                    messageBuilder.Append(issue.Description);
                }

                if (!string.IsNullOrWhiteSpace(issue.Recommendation))
                {
                    messageBuilder.Append(" (");
                    messageBuilder.Append(issue.Recommendation);
                    messageBuilder.Append(')');
                }

                return messageBuilder.ToString();
            })
            .ToArray();
    }

    private static string BuildExportVerificationMarkdown(ExportVerificationResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# AI Export Verification");
        builder.AppendLine();
        builder.AppendLine($"- **Generated:** {result.GeneratedAt:O}");
        builder.AppendLine($"- **Summary:** {result.Summary}");
        builder.AppendLine();

        if (result.Issues.Count > 0)
        {
            builder.AppendLine("## Findings");
            builder.AppendLine();

            foreach (var issue in result.Issues)
            {
                var severity = string.IsNullOrWhiteSpace(issue.Severity)
                    ? "INFO"
                    : issue.Severity.Trim().ToUpperInvariant();
                builder.AppendLine($"- **{severity}** — {issue.Title}");
                builder.AppendLine($"  - {issue.Description}");
                if (!string.IsNullOrWhiteSpace(issue.Recommendation))
                {
                    builder.AppendLine($"  - Recommendation: {issue.Recommendation}");
                }
            }

            builder.AppendLine();
        }
        else
        {
            builder.AppendLine("No issues detected.");
            builder.AppendLine();
        }

        if (result.SuggestedFixes.Count > 0)
        {
            builder.AppendLine("## Suggested fixes");
            builder.AppendLine();
            foreach (var fix in result.SuggestedFixes)
            {
                if (!string.IsNullOrWhiteSpace(fix))
                {
                    builder.AppendLine($"- {fix}");
                }
            }
            builder.AppendLine();
        }

        if (result.SuggestedDirectives is not null)
        {
            builder.AppendLine("## Suggested directives");
            builder.AppendLine();
            builder.AppendLine("The assistant proposed configuration changes. Use `/apply-directives` to review and apply them.");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static object? BuildDesignChanges(RunDesignState? current, RunDesignState? previous)
    {
        if (current is null && previous is null)
        {
            return null;
        }

        var changes = new Dictionary<string, object>();

        var currentStatus = DescribeDesignSnapshotStatus(current);
        var previousStatus = DescribeDesignSnapshotStatus(previous);
        if (!string.Equals(currentStatus, previousStatus, StringComparison.OrdinalIgnoreCase))
        {
            changes["snapshotStatus"] = new { current = currentStatus, previous = previousStatus };
        }

        var snapshotMetrics = CompareDesignSnapshotMetrics(current?.Snapshot, previous?.Snapshot);
        if (snapshotMetrics.Count > 0)
        {
            changes["snapshotMetrics"] = snapshotMetrics;
        }

        var screenshotDiff = BuildRunDeltaSetDiff(current?.Screenshots ?? Array.Empty<string>(), previous?.Screenshots ?? Array.Empty<string>());
        if (screenshotDiff.Added.Count > 0 || screenshotDiff.Resolved.Count > 0)
        {
            changes["screenshots"] = new
            {
                added = screenshotDiff.Added,
                removed = screenshotDiff.Resolved,
                current = screenshotDiff.Current,
                previous = screenshotDiff.Previous
            };
        }

        return changes.Count == 0 ? null : changes;
    }

    private static string DescribeDesignSnapshotStatus(RunDesignState? design)
    {
        if (design is null)
        {
            return "not requested";
        }

        if (design.SnapshotFailed)
        {
            return "failed";
        }

        if (design.Snapshot is not null)
        {
            return "captured";
        }

        if (design.RequestedSnapshot)
        {
            return "requested";
        }

        return "not requested";
    }

    private static List<object> CompareDesignSnapshotMetrics(RunDesignSnapshotState? current, RunDesignSnapshotState? previous)
    {
        var results = new List<object>();
        if (current is null && previous is null)
        {
            return results;
        }

        var metrics = new (string Name, int? Current, int? Previous)[]
        {
            ("htmlLength", current?.HtmlLength, previous?.HtmlLength),
            ("inlineCssLength", current?.InlineCssLength, previous?.InlineCssLength),
            ("stylesheetCount", current?.StylesheetCount, previous?.StylesheetCount),
            ("fontFileCount", current?.FontFileCount, previous?.FontFileCount),
            ("iconCount", current?.IconCount, previous?.IconCount),
            ("imageCount", current?.ImageCount, previous?.ImageCount),
            ("cssImageCount", current?.CssImageCount, previous?.CssImageCount),
            ("htmlImageCount", current?.HtmlImageCount, previous?.HtmlImageCount),
            ("fontDeclarationCount", current?.FontDeclarationCount, previous?.FontDeclarationCount),
            ("colorPaletteCount", current?.ColorPaletteCount, previous?.ColorPaletteCount),
            ("pageCount", current?.PageCount, previous?.PageCount)
        };

        foreach (var metric in metrics)
        {
            if (metric.Current != metric.Previous)
            {
                results.Add(new { metric = metric.Name, previous = metric.Previous, current = metric.Current });
            }
        }

        return results;
    }

    private static RunDeltaSetDiff BuildRunDeltaSetDiff(IReadOnlyList<string> current, IReadOnlyList<string> previous)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var currentSet = new HashSet<string>(current.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()), comparer);
        var previousSet = new HashSet<string>(previous.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()), comparer);

        var added = currentSet.Except(previousSet, comparer).OrderBy(value => value, comparer).ToArray();
        var resolved = previousSet.Except(currentSet, comparer).OrderBy(value => value, comparer).ToArray();
        var currentOrdered = currentSet.OrderBy(value => value, comparer).ToArray();
        var previousOrdered = previousSet.OrderBy(value => value, comparer).ToArray();

        return new RunDeltaSetDiff(added, resolved, currentOrdered, previousOrdered);
    }

    private static RunSnapshotDetails? TryParseRunSnapshot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var storeIdentifier = TryGetOptionalString(root, "storeIdentifier") ?? string.Empty;
            var storeUrl = TryGetOptionalString(root, "storeUrl");
            var isWooCommerce = false;
            if (root.TryGetProperty("isWooCommerce", out var isWooProperty)
                && isWooProperty.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                isWooCommerce = isWooProperty.ValueKind == JsonValueKind.True;
            }

            var requestedPublic = false;
            if (root.TryGetProperty("requestedPublicExtensionFootprints", out var requestedProperty)
                && requestedProperty.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                requestedPublic = requestedProperty.ValueKind == JsonValueKind.True;
            }

            var plugins = new Dictionary<string, RunPluginState>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("plugins", out var pluginArray) && pluginArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var pluginElement in pluginArray.EnumerateArray())
                {
                    var slug = TryGetOptionalString(pluginElement, "slug");
                    if (string.IsNullOrWhiteSpace(slug))
                    {
                        continue;
                    }

                    var plugin = new RunPluginState(
                        slug,
                        TryGetOptionalString(pluginElement, "name"),
                        TryGetOptionalString(pluginElement, "version"),
                        TryGetOptionalString(pluginElement, "status"));
                    plugins[plugin.Slug] = plugin;
                }
            }

            var themes = new Dictionary<string, RunThemeState>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("themes", out var themeArray) && themeArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var themeElement in themeArray.EnumerateArray())
                {
                    var slug = TryGetOptionalString(themeElement, "slug");
                    if (string.IsNullOrWhiteSpace(slug))
                    {
                        continue;
                    }

                    var theme = new RunThemeState(
                        slug,
                        TryGetOptionalString(themeElement, "name"),
                        TryGetOptionalString(themeElement, "version"),
                        TryGetOptionalString(themeElement, "status"));
                    themes[theme.Slug] = theme;
                }
            }

            var publicExtensions = new Dictionary<string, RunPublicExtensionState>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("publicExtensions", out var extensionArray) && extensionArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var extensionElement in extensionArray.EnumerateArray())
                {
                    var slug = TryGetOptionalString(extensionElement, "slug") ?? string.Empty;
                    var type = TryGetOptionalString(extensionElement, "type") ?? string.Empty;
                    var key = $"{slug}|{type}";
                    var extension = new RunPublicExtensionState(
                        slug,
                        type,
                        TryGetOptionalString(extensionElement, "versionHint"),
                        TryGetOptionalString(extensionElement, "directoryVersion"),
                        TryGetOptionalString(extensionElement, "directoryStatus"),
                        TryGetOptionalString(extensionElement, "directoryTitle"));
                    publicExtensions[key] = extension;
                }
            }

            RunDesignState? design = null;
            if (root.TryGetProperty("design", out var designElement) && designElement.ValueKind == JsonValueKind.Object)
            {
                var requestedSnapshot = designElement.TryGetProperty("requestedSnapshot", out var requestedElement) && requestedElement.ValueKind == JsonValueKind.True;
                var snapshotFailed = designElement.TryGetProperty("snapshotFailed", out var failedElement) && failedElement.ValueKind == JsonValueKind.True;
                RunDesignSnapshotState? snapshot = null;
                if (designElement.TryGetProperty("snapshot", out var snapshotElement) && snapshotElement.ValueKind == JsonValueKind.Object)
                {
                    snapshot = new RunDesignSnapshotState(
                        GetOptionalInt(snapshotElement, "htmlLength"),
                        GetOptionalInt(snapshotElement, "inlineCssLength"),
                        GetOptionalInt(snapshotElement, "stylesheetCount"),
                        GetOptionalInt(snapshotElement, "fontFileCount"),
                        GetOptionalInt(snapshotElement, "iconCount"),
                        GetOptionalInt(snapshotElement, "imageCount"),
                        GetOptionalInt(snapshotElement, "cssImageCount"),
                        GetOptionalInt(snapshotElement, "htmlImageCount"),
                        GetOptionalInt(snapshotElement, "fontDeclarationCount"),
                        GetOptionalInt(snapshotElement, "colorPaletteCount"),
                        snapshotElement.TryGetProperty("pages", out var pagesElement) && pagesElement.ValueKind == JsonValueKind.Array
                            ? pagesElement.GetArrayLength()
                            : (int?)null);
                }

                var requestedScreenshots = designElement.TryGetProperty("requestedScreenshots", out var shotsElement) && shotsElement.ValueKind == JsonValueKind.True;
                var screenshots = new List<string>();
                if (designElement.TryGetProperty("screenshots", out var screenshotsElement) && screenshotsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var screenshotElement in screenshotsElement.EnumerateArray())
                    {
                        var fileName = TryGetOptionalString(screenshotElement, "fileName");
                        if (!string.IsNullOrWhiteSpace(fileName))
                        {
                            screenshots.Add(fileName);
                            continue;
                        }

                        var label = TryGetOptionalString(screenshotElement, "label");
                        if (!string.IsNullOrWhiteSpace(label))
                        {
                            screenshots.Add(label);
                        }
                    }
                }

                design = new RunDesignState(requestedSnapshot, snapshotFailed, snapshot, requestedScreenshots, screenshots);
            }

            var missingCredentialExports = ParseStringArray(root, "missingCredentialExports");
            var logHighlights = ParseStringArray(root, "logHighlights");

            return new RunSnapshotDetails(
                storeIdentifier,
                storeUrl,
                isWooCommerce,
                requestedPublic,
                plugins,
                themes,
                publicExtensions,
                design,
                missingCredentialExports,
                logHighlights);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ParseStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return array.EnumerateArray()
            .Select(element => element.ValueKind == JsonValueKind.String ? element.GetString() : null)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToArray();
    }

    private static string? TryGetOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Null => null,
            _ => property.GetRawText()
        };
    }

    private static int? GetOptionalInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
        {
            return value;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var longValue))
        {
            return (int)Math.Clamp(longValue, int.MinValue, int.MaxValue);
        }

        return null;
    }

    private static string CreateFallbackRunDeltaNarrative(string runDeltaJson, bool assistantMissing)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Run delta summary");
        builder.AppendLine();
        if (assistantMissing)
        {
            builder.AppendLine("Assistant configuration missing. Review the structured diff below.");
        }
        else
        {
            builder.AppendLine("The assistant could not summarize the run delta automatically. Review the structured diff below.");
        }

        builder.AppendLine();
        builder.AppendLine("```json");
        builder.AppendLine(runDeltaJson);
        builder.AppendLine("```");
        return builder.ToString();
    }

    private sealed record RunHistoryEntry(
        string RunTimestamp,
        DateTime? CapturedAtUtc,
        string SnapshotJson,
        IReadOnlyList<string> AutomationWarnings);

    private sealed record RunSnapshotDetails(
        string StoreIdentifier,
        string? StoreUrl,
        bool IsWooCommerce,
        bool RequestedPublicExtensionFootprints,
        IReadOnlyDictionary<string, RunPluginState> Plugins,
        IReadOnlyDictionary<string, RunThemeState> Themes,
        IReadOnlyDictionary<string, RunPublicExtensionState> PublicExtensions,
        RunDesignState? Design,
        IReadOnlyList<string> MissingCredentialExports,
        IReadOnlyList<string> LogHighlights);

    private sealed record RunPluginState(string Slug, string? Name, string? Version, string? Status);

    private sealed record RunThemeState(string Slug, string? Name, string? Version, string? Status);

    private sealed record RunPublicExtensionState(
        string Slug,
        string Type,
        string? VersionHint,
        string? DirectoryVersion,
        string? DirectoryStatus,
        string? DirectoryTitle)
    {
        public string Key => $"{Slug}|{Type}";
    }

    private sealed record RunDesignState(
        bool RequestedSnapshot,
        bool SnapshotFailed,
        RunDesignSnapshotState? Snapshot,
        bool RequestedScreenshots,
        IReadOnlyList<string> Screenshots);

    private sealed record RunDesignSnapshotState(
        int? HtmlLength,
        int? InlineCssLength,
        int? StylesheetCount,
        int? FontFileCount,
        int? IconCount,
        int? ImageCount,
        int? CssImageCount,
        int? HtmlImageCount,
        int? FontDeclarationCount,
        int? ColorPaletteCount,
        int? PageCount);

    private sealed record RunDeltaSetDiff(
        IReadOnlyList<string> Added,
        IReadOnlyList<string> Resolved,
        IReadOnlyList<string> Current,
        IReadOnlyList<string> Previous);

    private static string SanitizeSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "segment";
        }

        var sanitized = SanitizeForPath(value);
        return string.IsNullOrWhiteSpace(sanitized) ? "segment" : sanitized;
    }

    public sealed record AutomationScriptDisplay(
        string Name,
        string? Description,
        string Language,
        string Content,
        string FilePath,
        string RelativePath,
        IReadOnlyList<string> Notes);

    private void SetProvisioningContext(
        List<StoreProduct> products,
        List<StoreProduct> variations,
        StoreConfiguration? configuration,
        List<ExtensionArtifact> pluginBundles,
        List<ExtensionArtifact> themeBundles,
        List<WooCustomer> customers,
        List<WooCoupon> coupons,
        List<WooOrder> orders,
        List<WooSubscription> subscriptions,
        WordPressSiteContent? siteContent,
        List<TermItem> categoryTerms)
    {
        var productSnapshots = CloneProductsWithMedia(products);
        var variationSnapshots = CloneProductsWithMedia(variations);
        var variableProducts = BuildVariableProducts(productSnapshots, variationSnapshots);
        var customerSnapshots = CloneRecords(customers);
        var couponSnapshots = CloneRecords(coupons);
        var orderSnapshots = CloneRecords(orders);
        var subscriptionSnapshots = CloneRecords(subscriptions);
        var categorySnapshots = CloneRecords(categoryTerms);
        _lastProvisioningContext = new ProvisioningContext(
            productSnapshots,
            variationSnapshots,
            variableProducts,
            configuration,
            pluginBundles,
            themeBundles,
            customerSnapshots,
            couponSnapshots,
            orderSnapshots,
            subscriptionSnapshots,
            siteContent,
            categorySnapshots);
        OnPropertyChanged(nameof(CanReplicate));
        ReplicateCommand.RaiseCanExecuteChanged();
    }

    private static List<StoreProduct> CloneProductsWithMedia(IEnumerable<StoreProduct> source)
    {
        var list = source?.Where(p => p is not null).Select(p => p!).ToList() ?? new List<StoreProduct>();
        if (list.Count == 0)
        {
            return new List<StoreProduct>();
        }

        var json = JsonSerializer.Serialize(list);
        var clones = JsonSerializer.Deserialize<List<StoreProduct>>(json) ?? new List<StoreProduct>();

        if (clones.Count != list.Count)
        {
            // Fallback to manual cloning to preserve indexes if serialization yielded a different count.
            clones = list.Select(p => CloneProduct(p)).ToList();
            return clones;
        }

        for (var i = 0; i < list.Count; i++)
        {
            var original = list[i];
            var clone = clones[i];
            clone.LocalImageFilePaths.Clear();
            clone.LocalImageFilePaths.AddRange(original.LocalImageFilePaths);
        }

        return clones;
    }

    private static StoreProduct CloneProduct(StoreProduct product)
    {
        var json = JsonSerializer.Serialize(product);
        var clone = JsonSerializer.Deserialize<StoreProduct>(json) ?? new StoreProduct();
        clone.LocalImageFilePaths.Clear();
        clone.LocalImageFilePaths.AddRange(product.LocalImageFilePaths);
        return clone;
    }

    private static List<T> CloneRecords<T>(IEnumerable<T> source) where T : class
    {
        var list = source?.Where(item => item is not null).Select(item => item!).ToList() ?? new List<T>();
        if (list.Count == 0)
        {
            return new List<T>();
        }

        var json = JsonSerializer.Serialize(list);
        var clones = JsonSerializer.Deserialize<List<T>>(json) ?? new List<T>();
        return clones;
    }

    private static List<ProvisioningVariableProduct> BuildVariableProducts(
        List<StoreProduct> products,
        List<StoreProduct> variations)
    {
        var result = new List<ProvisioningVariableProduct>();
        if (products.Count == 0 || variations.Count == 0)
        {
            return result;
        }

        var parentLookup = products
            .Where(p => p is not null && p.Id > 0)
            .ToDictionary(p => p.Id, p => p);

        var grouped = variations
            .Where(v => v is not null && v.ParentId is int id && id > 0)
            .GroupBy(v => v.ParentId!.Value);

        foreach (var group in grouped)
        {
            if (!parentLookup.TryGetValue(group.Key, out var parent))
            {
                continue;
            }

            var orderedVariations = group
                .Where(v => v is not null)
                .Select(v => v!)
                .ToList();

            if (orderedVariations.Count == 0)
            {
                continue;
            }

            result.Add(new ProvisioningVariableProduct(parent, orderedVariations));
        }

        return result;
    }

    private void ResetProvisioningContext()
    {
        _lastProvisioningContext = null;
        OnPropertyChanged(nameof(CanReplicate));
        ReplicateCommand.RaiseCanExecuteChanged();
    }

    private void ClearFilters()
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            CategoryChoices.Clear();
            TagChoices.Clear();
        });
    }

    private static void SetSelection(ObservableCollection<SelectableTerm> terms, bool isSelected)
    {
        foreach (var term in terms)
        {
            term.IsSelected = isSelected;
        }
    }

    private async Task OnReplicateStoreAsync(CancellationToken cancellationToken)
    {
        if (_lastProvisioningContext is null || _lastProvisioningContext.Products.Count == 0)
        {
            Append("Run an export before provisioning.");
            return;
        }

        if (string.IsNullOrWhiteSpace(TargetStoreUrl) || string.IsNullOrWhiteSpace(TargetConsumerKey) || string.IsNullOrWhiteSpace(TargetConsumerSecret))
        {
            Append("Enter the target store URL, consumer key, and consumer secret before provisioning.");
            return;
        }

        try
        {
            IsRunning = true;
            var provisioningContext = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ProductCount"] = _lastProvisioningContext.Products.Count,
                ["TargetUrl"] = TargetStoreUrl
            };

            var operationLogger = CreateOperationLogger(
                "WooProvisioning",
                TargetStoreUrl,
                entityType: "WooCommerce",
                additionalContext: provisioningContext);
            var progressLogger = RequireProgressLogger(operationLogger);
            var settings = new WooProvisioningSettings(
                TargetStoreUrl,
                TargetConsumerKey,
                TargetConsumerSecret,
                string.IsNullOrWhiteSpace(WordPressUsername) ? null : WordPressUsername,
                string.IsNullOrWhiteSpace(WordPressApplicationPassword) ? null : WordPressApplicationPassword);
            Append($"Provisioning {_lastProvisioningContext.Products.Count} products to {settings.BaseUrl}…");
            StoreConfiguration? configuration = null;
            if (ImportStoreConfiguration)
            {
                configuration = _lastProvisioningContext.Configuration;
                if (configuration is null || !HasConfigurationData(configuration))
                {
                    Append("No stored configuration available. Run an export with configuration enabled if you wish to replicate settings.");
                    configuration = null;
                }
                else
                {
                    Append("Applying captured store configuration before provisioning products…");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (_lastProvisioningContext.PluginBundles.Count > 0)
            {
                Append($"Uploading {_lastProvisioningContext.PluginBundles.Count} plugin bundles…");
                await _wooProvisioningService.UploadPluginsAsync(settings, _lastProvisioningContext.PluginBundles, progressLogger, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (_lastProvisioningContext.ThemeBundles.Count > 0)
            {
                Append($"Uploading {_lastProvisioningContext.ThemeBundles.Count} theme bundles…");
                await _wooProvisioningService.UploadThemesAsync(settings, _lastProvisioningContext.ThemeBundles, progressLogger, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            await _wooProvisioningService.ProvisionAsync(
                settings,
                _lastProvisioningContext.Products,
                variableProducts: _lastProvisioningContext.VariableProducts,
                variations: _lastProvisioningContext.Variations,
                configuration: configuration,
                _lastProvisioningContext.Customers,
                _lastProvisioningContext.Coupons,
                _lastProvisioningContext.Orders,
                subscriptions: _lastProvisioningContext.Subscriptions,
                siteContent: _lastProvisioningContext.SiteContent,
                categoryMetadata: _lastProvisioningContext.ProductCategories,
                progress: progressLogger,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            Append($"Provisioning failed: {ex.Message}");
        }
        finally
        {
            var runCts = _runCts;
            if (runCts is not null)
            {
                _runCts = null;
                runCts.Dispose();
            }

            IsRunning = false;
            RaiseRunCommandStates();
        }
    }

    private sealed class ProvisioningContext
    {
        public ProvisioningContext(
            List<StoreProduct> products,
            List<StoreProduct> variations,
            List<ProvisioningVariableProduct> variableProducts,
            StoreConfiguration? configuration,
            List<ExtensionArtifact> pluginBundles,
            List<ExtensionArtifact> themeBundles,
            List<WooCustomer> customers,
            List<WooCoupon> coupons,
            List<WooOrder> orders,
            List<WooSubscription> subscriptions,
            WordPressSiteContent? siteContent,
            List<TermItem> productCategories)
        {
            Products = products;
            Variations = variations;
            VariableProducts = variableProducts;
            Configuration = configuration;
            PluginBundles = pluginBundles;
            ThemeBundles = themeBundles;
            Customers = customers;
            Coupons = coupons;
            Orders = orders;
            Subscriptions = subscriptions;
            SiteContent = siteContent;
            ProductCategories = productCategories;
        }

        public List<StoreProduct> Products { get; }
        public List<StoreProduct> Variations { get; }
        public List<ProvisioningVariableProduct> VariableProducts { get; }
        public StoreConfiguration? Configuration { get; }
        public List<ExtensionArtifact> PluginBundles { get; }
        public List<ExtensionArtifact> ThemeBundles { get; }
        public List<WooCustomer> Customers { get; }
        public List<WooCoupon> Coupons { get; }
        public List<WooOrder> Orders { get; }
        public List<WooSubscription> Subscriptions { get; }
        public WordPressSiteContent? SiteContent { get; }
        public List<TermItem> ProductCategories { get; }
    }

    private sealed record MediaReference(string RelativePath, string AbsolutePath);

    private ShopifySettings BuildShopifySettings(string baseUrl)
    {
        return new ShopifySettings(
            baseUrl,
            string.IsNullOrWhiteSpace(ShopifyAdminAccessToken) ? null : ShopifyAdminAccessToken,
            string.IsNullOrWhiteSpace(ShopifyStorefrontAccessToken) ? null : ShopifyStorefrontAccessToken,
            string.IsNullOrWhiteSpace(ShopifyApiKey) ? null : ShopifyApiKey,
            string.IsNullOrWhiteSpace(ShopifyApiSecret) ? null : ShopifyApiSecret);
    }

    private string ResolveBaseOutputFolder()
        => string.IsNullOrWhiteSpace(OutputFolder)
            ? Path.GetFullPath("output")
            : OutputFolder;

    private static string BuildStoreIdentifier(string targetUrl)
    {
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri))
        {
            var fallback = SanitizeForPath(targetUrl);
            return string.IsNullOrWhiteSpace(fallback) ? "store" : fallback;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(uri.Host))
        {
            parts.Add(uri.Host);
        }

        if (segments.Length > 0)
        {
            parts.AddRange(segments);
        }

        var combined = parts.Count > 0
            ? string.Join("_", parts)
            : uri.Host ?? targetUrl;

        var sanitized = SanitizeForPath(combined);
        return string.IsNullOrWhiteSpace(sanitized) ? "store" : sanitized;
    }

    private static string SanitizeForPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "store";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.')
            {
                builder.Append(ch);
            }
            else
            {
                builder.Append('-');
            }
        }

        var sanitized = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "store" : sanitized;
    }

    private sealed class UserPreferences
    {
        public bool ExportCsv { get; set; }
        public bool ExportShopify { get; set; }
        public bool ExportWoo { get; set; }
        public bool ExportReviews { get; set; }
        public bool ExportXlsx { get; set; }
        public bool ExportJsonl { get; set; }
        public bool ExportPluginsCsv { get; set; }
        public bool ExportPluginsJsonl { get; set; }
        public bool ExportThemesCsv { get; set; }
        public bool ExportThemesJsonl { get; set; }
        public bool ExportPublicExtensionFootprints { get; set; }
        public string? AdditionalPublicExtensionPages { get; set; }
        public string? PublicExtensionMaxPages { get; set; }
        public string? PublicExtensionMaxBytes { get; set; }
        public string? AdditionalDesignSnapshotPages { get; set; }
        public string? DesignScreenshotBreakpointsText { get; set; }
        public bool ExportPublicDesignSnapshot { get; set; }
        public bool ExportPublicDesignScreenshots { get; set; }
        public bool ExportStoreConfiguration { get; set; }
        public bool ImportStoreConfiguration { get; set; }
        public string? ChatApiEndpoint { get; set; }
        public string? ChatModel { get; set; }
        public string? ChatSystemPrompt { get; set; }
        [JsonPropertyName("ChatDefaultMaxPromptTokens")]
        public int? ChatDefaultMaxPromptTokens { get; set; }

        [JsonPropertyName("ChatMaxPromptTokens")]
        public int? LegacyChatMaxPromptTokens { get; set; }

        [JsonPropertyName("ChatDefaultMaxTotalTokens")]
        public int? ChatDefaultMaxTotalTokens { get; set; }

        [JsonPropertyName("ChatMaxTotalTokens")]
        public int? LegacyChatMaxTotalTokens { get; set; }

        [JsonPropertyName("ChatDefaultMaxCostUsd")]
        public decimal? ChatDefaultMaxCostUsd { get; set; }

        [JsonPropertyName("ChatMaxCostUsd")]
        public decimal? LegacyChatMaxCostUsd { get; set; }
        public decimal? ChatPromptTokenUsdPerThousand { get; set; }
        public decimal? ChatCompletionTokenUsdPerThousand { get; set; }
        public bool EnableHttpRetries { get; set; }
        public int HttpRetryAttempts { get; set; }
        public double HttpRetryBaseDelaySeconds { get; set; }
        public double HttpRetryMaxDelaySeconds { get; set; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        if (name == nameof(HasChatConfiguration) || name == nameof(IsLogSummaryBusy))
        {
            ExplainLogsCommand?.RaiseCanExecuteChanged();
        }
    }

    private void OnWordPressCredentialsChanged()
    {
        OnPropertyChanged(nameof(HasWordPressCredentials));
        OnPropertyChanged(nameof(CanExportExtensions));
        OnPropertyChanged(nameof(CanExportStoreConfiguration));
        OnPropertyChanged(nameof(CanExportPublicExtensionFootprints));
        OnPropertyChanged(nameof(CanExportPublicDesignSnapshot));
        OnPropertyChanged(nameof(CanExportPublicDesignScreenshots));

        if (HasWordPressCredentials && ExportPublicExtensionFootprints)
        {
            ExportPublicExtensionFootprints = false;
        }

        if (HasWordPressCredentials && ExportPublicDesignSnapshot)
        {
            ExportPublicDesignSnapshot = false;
        }

        if (HasWordPressCredentials && ExportPublicDesignScreenshots)
        {
            ExportPublicDesignScreenshots = false;
        }
    }
}

// Helper wrapper for selectable filters
public sealed class SelectableTerm : INotifyPropertyChanged
{
    public TermItem Term { get; }
    public ShopifyCollectionDetails? ShopifyCollection { get; }
    private bool _isSelected;

    public SelectableTerm(TermItem term, ShopifyCollectionDetails? shopifyCollection = null)
    {
        Term = term;
        ShopifyCollection = shopifyCollection;
    }
    public string? Name => Term.Name;
    public bool IsSelected { get => _isSelected; set { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public enum PlatformMode
{
    WooCommerce,
    Shopify
}
