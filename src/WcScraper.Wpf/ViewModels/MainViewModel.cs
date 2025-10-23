
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using WcScraper.Core;
using WcScraper.Core.Exporters;
using WcScraper.Core.Shopify;
using WcScraper.Wpf.Reporting;
using WcScraper.Wpf.Services;

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
    private static readonly TimeSpan DirectoryLookupDelay = TimeSpan.FromMilliseconds(400);
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

    public MainViewModel(IDialogService dialogs)
        : this(dialogs, new WooScraper(), new ShopifyScraper(), new HttpClient())
    {
    }

    internal MainViewModel(IDialogService dialogs, WooScraper wooScraper, ShopifyScraper shopifyScraper)
        : this(dialogs, wooScraper, shopifyScraper, new HttpClient())
    {
    }

    internal MainViewModel(
        IDialogService dialogs,
        WooScraper wooScraper,
        ShopifyScraper shopifyScraper,
        HttpClient httpClient)
        : this(dialogs, wooScraper, shopifyScraper, httpClient, new HeadlessBrowserScreenshotService())
    {
    }

    internal MainViewModel(
        IDialogService dialogs,
        WooScraper wooScraper,
        ShopifyScraper shopifyScraper,
        HttpClient httpClient,
        HeadlessBrowserScreenshotService designScreenshotService)
    {
        _dialogs = dialogs;
        _wooScraper = wooScraper;
        _shopifyScraper = shopifyScraper;
        _httpClient = httpClient;
        _wooProvisioningService = new WooProvisioningService();
        _wpDirectoryClient = new WordPressDirectoryClient(_httpClient);
        _designScreenshotService = designScreenshotService ?? throw new ArgumentNullException(nameof(designScreenshotService));
        BrowseCommand = new RelayCommand(OnBrowse);
        RunCommand = new RelayCommand(async () => await OnRunAsync(), () => !IsRunning);
        SelectAllCategoriesCommand = new RelayCommand(() => SetSelection(CategoryChoices, true));
        ClearCategoriesCommand = new RelayCommand(() => SetSelection(CategoryChoices, false));
        SelectAllTagsCommand = new RelayCommand(() => SetSelection(TagChoices, true));
        ClearTagsCommand = new RelayCommand(() => SetSelection(TagChoices, false));
        ExportCollectionsCommand = new RelayCommand(OnExportCollections);
        ReplicateCommand = new RelayCommand(async () => await OnReplicateStoreAsync(), () => !IsRunning && CanReplicate);
    }

    // XAML-friendly default constructor + Dialogs setter
    public MainViewModel() : this(new WcScraper.Wpf.Services.DialogService()) { }
    public IDialogService Dialogs { set { /* for XAML object element */ } }

    public string StoreUrl { get => _storeUrl; set { _storeUrl = value; OnPropertyChanged(); } }
    public string OutputFolder { get => _outputFolder; set { _outputFolder = value; OnPropertyChanged(); } }
    public bool ExportCsv { get => _expCsv; set { _expCsv = value; OnPropertyChanged(); } }
    public bool ExportShopify { get => _expShopify; set { _expShopify = value; OnPropertyChanged(); } }
    public bool ExportWoo { get => _expWoo; set { _expWoo = value; OnPropertyChanged(); } }
    public bool ExportReviews { get => _expReviews; set { _expReviews = value; OnPropertyChanged(); } }
    public bool ExportXlsx { get => _expXlsx; set { _expXlsx = value; OnPropertyChanged(); } }
    public bool ExportJsonl { get => _expJsonl; set { _expJsonl = value; OnPropertyChanged(); } }
    public bool ExportPluginsCsv { get => _expPluginsCsv; set { _expPluginsCsv = value; OnPropertyChanged(); } }
    public bool ExportPluginsJsonl { get => _expPluginsJsonl; set { _expPluginsJsonl = value; OnPropertyChanged(); } }
    public bool ExportThemesCsv { get => _expThemesCsv; set { _expThemesCsv = value; OnPropertyChanged(); } }
    public bool ExportThemesJsonl { get => _expThemesJsonl; set { _expThemesJsonl = value; OnPropertyChanged(); } }
    public bool ExportPublicExtensionFootprints { get => _expPublicExtensionFootprints; set { _expPublicExtensionFootprints = value; OnPropertyChanged(); } }
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
        }
    }
    public bool ExportPublicDesignSnapshot { get => _expPublicDesignSnapshot; set { _expPublicDesignSnapshot = value; OnPropertyChanged(); } }
    public bool ExportPublicDesignScreenshots { get => _expPublicDesignScreenshots; set { _expPublicDesignScreenshots = value; OnPropertyChanged(); } }
    public bool ExportStoreConfiguration { get => _expStoreConfiguration; set { _expStoreConfiguration = value; OnPropertyChanged(); } }
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            _isRunning = value;
            OnPropertyChanged();
            RunCommand.RaiseCanExecuteChanged();
            ReplicateCommand.RaiseCanExecuteChanged();
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

    private (int? PageLimit, long? ByteLimit) GetPublicExtensionLimits(IProgress<string>? log)
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
    public bool ImportStoreConfiguration { get => _importStoreConfiguration; set { _importStoreConfiguration = value; OnPropertyChanged(); } }
    public bool CanReplicate => _lastProvisioningContext is { Products.Count: > 0 };

    // Selectable terms for filters
    public ObservableCollection<SelectableTerm> CategoryChoices { get; } = new();
    public ObservableCollection<SelectableTerm> TagChoices { get; } = new();

    public ObservableCollection<string> Logs { get; } = new();

    public RelayCommand BrowseCommand { get; }
    public RelayCommand RunCommand { get; }
    public RelayCommand SelectAllCategoriesCommand { get; }
    public RelayCommand ClearCategoriesCommand { get; }
    public RelayCommand SelectAllTagsCommand { get; }
    public RelayCommand ClearTagsCommand { get; }
    public RelayCommand ExportCollectionsCommand { get; }
    public RelayCommand ReplicateCommand { get; }

    private void OnBrowse()
    {
        var chosen = _dialogs.BrowseForFolder(OutputFolder);
        if (!string.IsNullOrWhiteSpace(chosen))
            OutputFolder = chosen;
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

    private async Task LoadFiltersForStoreAsync(string baseUrl, IProgress<string> logger)
    {
        try
        {
            if (SelectedPlatform == PlatformMode.WooCommerce)
            {
                var cats = await _wooScraper.FetchProductCategoriesAsync(baseUrl, logger);
                var tags = await _wooScraper.FetchProductTagsAsync(baseUrl, logger);
                App.Current?.Dispatcher.Invoke(() =>
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
                var collections = await _shopifyScraper.FetchCollectionsAsync(settings, logger);
                var tags = await _shopifyScraper.FetchProductTagsAsync(settings, logger);
                App.Current?.Dispatcher.Invoke(() =>
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

    private async Task OnRunAsync()
    {
        var targetUrl = SelectedPlatform == PlatformMode.WooCommerce ? StoreUrl : ShopifyStoreUrl;
        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            Append("Please enter a store URL (e.g., https://example.com).");
            return;
        }

        var retrySettings = GetRetrySettings();
        var retryPolicy = new HttpRetryPolicy(retrySettings.Attempts, retrySettings.BaseDelay, retrySettings.MaxDelay);
        _wooScraper.HttpPolicy = retryPolicy;

        ResetProvisioningContext();

        try
        {
            IsRunning = true;
            var baseOutputFolder = ResolveBaseOutputFolder();
            Directory.CreateDirectory(baseOutputFolder);
            IProgress<string> logger = new Progress<string>(Append);

            var storeId = BuildStoreIdentifier(targetUrl);
            var storeOutputFolder = Path.Combine(baseOutputFolder, storeId);
            Directory.CreateDirectory(storeOutputFolder);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");

            // Refresh filters for this store
            await LoadFiltersForStoreAsync(targetUrl, logger);

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

            if (SelectedPlatform == PlatformMode.WooCommerce)
            {
                Append($"Fetching products via Store API… {targetUrl}");
                prods = await _wooScraper.FetchStoreProductsAsync(targetUrl, log: logger, categoryFilter: categoryFilter, tagFilter: tagFilter);

                if (prods.Count == 0)
                {
                    Append("Store API empty. Trying WordPress REST fallback (basic fields)…");
                    prods = await _wooScraper.FetchWpProductsBasicAsync(targetUrl, log: logger);
                }

                if (prods.Count == 0)
                {
                    Append("No products found via public APIs.");
                    return;
                }

                Append($"Found {prods.Count} products.");

                var lastCategoryTerms = _wooScraper.LastFetchedProductCategories;
                if (lastCategoryTerms.Count > 0)
                {
                    categoryTerms = lastCategoryTerms.Where(term => term is not null).Select(term => term!).ToList();
                }
                else
                {
                    categoryTerms = await _wooScraper.FetchProductCategoriesAsync(targetUrl, logger);
                }

                // Variations for variable products
                var parentIds = prods.Where(p => string.Equals(p.Type, "variable", StringComparison.OrdinalIgnoreCase) || p.HasOptions == true)
                                     .Select(p => p.Id).Where(id => id > 0).Distinct().ToList();
                if (parentIds.Count > 0)
                {
                    Append($"Fetching variations for {parentIds.Count} variable products…");
                    variations = await _wooScraper.FetchStoreVariationsAsync(targetUrl, parentIds, log: logger);
                    Append($"Found {variations.Count} variations.");
                }
            }
            else
            {
                var settings = BuildShopifySettings(targetUrl);
                Append($"Fetching products via Shopify API… {settings.BaseUrl}");
                var rawProducts = await _shopifyScraper.FetchShopifyProductsAsync(settings, log: logger);
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
                        plugins = await _wooScraper.FetchPluginsAsync(targetUrl, WordPressUsername, WordPressApplicationPassword, logger);
                        Append(plugins.Count > 0
                            ? $"Found {plugins.Count} plugins."
                            : "No plugins returned by the authenticated endpoint.");
                    }

                    if (needsThemeInventory)
                    {
                        attemptedThemeFetch = true;
                        Append("Fetching installed themes…");
                        themes = await _wooScraper.FetchThemeAsync(targetUrl, WordPressUsername, WordPressApplicationPassword, logger);
                        Append(themes.Count > 0
                            ? $"Found {themes.Count} themes."
                            : "No themes returned by the authenticated endpoint.");
                    }
                }
            }

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

                    var (pageLimit, byteLimit) = GetPublicExtensionLimits(logger);
                    publicExtensionFootprints = await _wooScraper.FetchPublicExtensionFootprintsAsync(
                        targetUrl,
                        includeLinkedAssets: true,
                        log: logger,
                        additionalEntryUrls: additionalEntryUrls,
                        maxPages: pageLimit,
                        maxBytes: byteLimit);
                    publicExtensionDetection = _wooScraper.LastPublicExtensionDetection;
                    if (publicExtensionFootprints.Count > 0)
                    {
                        Append($"Detected {publicExtensionFootprints.Count} plugin/theme slug(s) from public assets (manual install required).");
                        await EnrichPublicExtensionFootprintsAsync(publicExtensionFootprints, logger);
                    }
                    else
                    {
                        Append("No public plugin/theme slugs were detected.");
                    }
                }
            }

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

                    designSnapshot = await _wooScraper.FetchPublicDesignSnapshotAsync(targetUrl, logger, additionalDesignPages);
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

            if (SelectedPlatform == PlatformMode.WooCommerce && ExportPublicDesignScreenshots)
            {
                try
                {
                    Append("Capturing public design screenshots…");
                    var screenshotFolder = Path.Combine(storeOutputFolder, "design", "screenshots");
                    var breakpoints = GetDesignScreenshotBreakpoints();
                    var screenshots = await _designScreenshotService.CaptureScreenshotsAsync(targetUrl, screenshotFolder, breakpoints);

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
                    customers = await _wooScraper.FetchCustomersAsync(targetUrl, WordPressUsername, WordPressApplicationPassword, logger);
                    Append(customers.Count > 0
                        ? $"Found {customers.Count} customers."
                        : "No customers returned by the authenticated endpoint.");

                    attemptedCouponFetch = true;
                    Append("Fetching coupons…");
                    coupons = await _wooScraper.FetchCouponsAsync(targetUrl, WordPressUsername, WordPressApplicationPassword, logger);
                    Append(coupons.Count > 0
                        ? $"Found {coupons.Count} coupons."
                        : "No coupons returned by the authenticated endpoint.");

                    attemptedOrderFetch = true;
                    Append("Fetching orders…");
                    orders = await _wooScraper.FetchOrdersAsync(targetUrl, WordPressUsername, WordPressApplicationPassword, logger);
                    Append(orders.Count > 0
                        ? $"Found {orders.Count} orders."
                        : "No orders returned by the authenticated endpoint.");

                    attemptedSubscriptionFetch = true;
                    Append("Fetching subscriptions…");
                    subscriptions = await _wooScraper.FetchSubscriptionsAsync(targetUrl, WordPressUsername, WordPressApplicationPassword, logger);
                    Append(subscriptions.Count > 0
                        ? $"Found {subscriptions.Count} subscriptions."
                        : "No subscriptions returned by the authenticated endpoint.");
                }
            }

            if (SelectedPlatform == PlatformMode.WooCommerce
                && (plugins.Count > 0 || themes.Count > 0)
                && !string.IsNullOrWhiteSpace(WordPressUsername)
                && !string.IsNullOrWhiteSpace(WordPressApplicationPassword))
            {
                wpSettingsSnapshot = await _wooScraper.FetchWordPressSettingsAsync(targetUrl, WordPressUsername, WordPressApplicationPassword, logger);
                if (wpSettingsSnapshot.Count > 0)
                {
                    Append($"Captured {wpSettingsSnapshot.Count} WordPress settings entries for extension option discovery.");
                }
            }

            if (SelectedPlatform == PlatformMode.WooCommerce && plugins.Count > 0)
            {
                var pluginRoot = Path.Combine(storeOutputFolder, "plugins");
                pluginBundles = await CapturePluginBundlesAsync(targetUrl, WordPressUsername, WordPressApplicationPassword, plugins, pluginRoot, wpSettingsSnapshot, logger);
            }

            if (SelectedPlatform == PlatformMode.WooCommerce && themes.Count > 0)
            {
                var themeRoot = Path.Combine(storeOutputFolder, "themes");
                themeBundles = await CaptureThemeBundlesAsync(targetUrl, WordPressUsername, WordPressApplicationPassword, themes, themeRoot, wpSettingsSnapshot, logger);
            }

            if (SelectedPlatform == PlatformMode.WooCommerce)
            {
                Append("Fetching WordPress pages…");
                pages = await _wooScraper.FetchWordPressPagesAsync(targetUrl, log: logger);
                Append(pages.Count > 0
                    ? $"Captured {pages.Count} pages."
                    : "No pages returned by the REST API.");

                Append("Fetching WordPress posts…");
                posts = await _wooScraper.FetchWordPressPostsAsync(targetUrl, log: logger);
                Append(posts.Count > 0
                    ? $"Captured {posts.Count} posts."
                    : "No posts returned by the REST API.");

                Append("Fetching WordPress media library…");
                mediaLibrary = await _wooScraper.FetchWordPressMediaAsync(targetUrl, log: logger);
                Append(mediaLibrary.Count > 0
                    ? $"Captured {mediaLibrary.Count} media entries."
                    : "No media entries returned by the REST API.");

                Append("Discovering WordPress menus…");
                menuCollection = await _wooScraper.FetchWordPressMenusAsync(targetUrl, log: logger);
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
                    widgets = await _wooScraper.FetchWordPressWidgetsAsync(targetUrl, WordPressUsername, WordPressApplicationPassword, logger);
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
                    var downloadedMedia = await DownloadMediaLibraryAsync(mediaLibrary, mediaFolder, logger);
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
                    await PopulateContentMediaReferencesAsync(contentItems, mediaLibrary, mediaFolder, mediaReferenceMap, logger);
                }

                var contentFolder = Path.Combine(storeOutputFolder, "content");
                Directory.CreateDirectory(contentFolder);

                if (pages.Count > 0)
                {
                    var pagePath = Path.Combine(contentFolder, $"{storeId}_{timestamp}_pages.json");
                    var json = JsonSerializer.Serialize(pages, _artifactWriteOptions);
                    await File.WriteAllTextAsync(pagePath, json, Encoding.UTF8);
                    Append($"Wrote {pagePath}");
                }

                if (posts.Count > 0)
                {
                    var postPath = Path.Combine(contentFolder, $"{storeId}_{timestamp}_posts.json");
                    var json = JsonSerializer.Serialize(posts, _artifactWriteOptions);
                    await File.WriteAllTextAsync(postPath, json, Encoding.UTF8);
                    Append($"Wrote {postPath}");
                }

                if (mediaLibrary.Count > 0)
                {
                    var mediaPath = Path.Combine(contentFolder, $"{storeId}_{timestamp}_media.json");
                    var json = JsonSerializer.Serialize(mediaLibrary, _artifactWriteOptions);
                    await File.WriteAllTextAsync(mediaPath, json, Encoding.UTF8);
                    Append($"Wrote {mediaPath}");
                }

                if (menuCollection is not null && (menuCollection.Menus.Count > 0 || menuCollection.Locations.Count > 0))
                {
                    var menuPath = Path.Combine(contentFolder, $"{storeId}_{timestamp}_menus.json");
                    var json = JsonSerializer.Serialize(menuCollection, _artifactWriteOptions);
                    await File.WriteAllTextAsync(menuPath, json, Encoding.UTF8);
                    Append($"Wrote {menuPath}");
                }

                if (widgets.Widgets.Count > 0 || widgets.Areas.Count > 0 || widgets.WidgetTypes.Count > 0)
                {
                    var widgetPath = Path.Combine(contentFolder, $"{storeId}_{timestamp}_widgets.json");
                    var json = JsonSerializer.Serialize(widgets, _artifactWriteOptions);
                    await File.WriteAllTextAsync(widgetPath, json, Encoding.UTF8);
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
                    configuration = await FetchStoreConfigurationAsync(targetUrl, WordPressUsername, WordPressApplicationPassword, logger);
                    if (configuration is not null && HasConfigurationData(configuration))
                    {
                        var configPath = Path.Combine(storeOutputFolder, $"{storeId}_{timestamp}_configuration.json");
                        var json = JsonSerializer.Serialize(configuration, _configurationWriteOptions);
                        await File.WriteAllTextAsync(configPath, json, Encoding.UTF8);
                        Append($"Wrote {configPath}");
                    }
                    else
                    {
                        Append("No store configuration data was captured.");
                    }
                }
            }

            // Generic rows projection
            var genericRows = Mappers.ToGenericRows(prods).ToList();
            var rowsById = new Dictionary<int, GenericRow>();
            foreach (var row in genericRows)
            {
                rowsById[row.Id] = row;
            }

            var imagesFolder = Path.Combine(storeOutputFolder, "images");
            Directory.CreateDirectory(imagesFolder);
            logger.Report($"Downloading product images to {imagesFolder}…");

            foreach (var product in prods)
            {
                var relativePaths = await DownloadProductImagesAsync(product, imagesFolder, logger, mediaReferenceMap);
                product.ImageFilePaths = relativePaths;
                if (rowsById.TryGetValue(product.Id, out var row))
                {
                    row.ImageFilePaths = relativePaths;
                }
            }

            foreach (var variation in variations)
            {
                var relativePaths = await DownloadProductImagesAsync(variation, imagesFolder, logger, mediaReferenceMap);
                variation.ImageFilePaths = relativePaths;
            }

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
            }).ToList();

            if (ExportCsv)
            {
                var path = Path.Combine(storeOutputFolder, $"{storeId}_{timestamp}_products.csv");
                CsvExporter.Write(path, genericDicts);
                Append($"Wrote {path}");
            }
            if (ExportXlsx)
            {
                var path = Path.Combine(storeOutputFolder, $"{storeId}_{timestamp}_products.xlsx");
                var excelRows = SelectedPlatform == PlatformMode.Shopify && shopifyDetailDicts is { Count: > 0 }
                    ? shopifyDetailDicts
                    : genericDicts;
                XlsxExporter.Write(path, excelRows);
                Append($"Wrote {path}");
            }
            if (ExportJsonl)
            {
                var path = Path.Combine(storeOutputFolder, $"{storeId}_{timestamp}_products.jsonl");
                JsonlExporter.Write(path, genericDicts);
                Append($"Wrote {path}");
            }

            if (SelectedPlatform == PlatformMode.WooCommerce && ExportPluginsCsv)
            {
                if (plugins.Count > 0)
                {
                    var path = Path.Combine(storeOutputFolder, $"{storeId}_{timestamp}_plugins.csv");
                    CsvExporter.WritePlugins(path, plugins);
                    Append($"Wrote {path}");
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
                    JsonlExporter.WritePlugins(path, plugins);
                    Append($"Wrote {path}");
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
                    CsvExporter.WriteThemes(path, themes);
                    Append($"Wrote {path}");
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
                    JsonlExporter.WriteThemes(path, themes);
                    Append($"Wrote {path}");
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

                    var jsonPath = Path.Combine(storeOutputFolder, $"{storeId}_{timestamp}_public_extension_footprints.json");
                    var json = JsonSerializer.Serialize(publicExtensionFootprints, _artifactWriteOptions);
                    await File.WriteAllTextAsync(jsonPath, json, Encoding.UTF8);
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
                    await File.WriteAllTextAsync(htmlPath, designSnapshot.RawHtml, Encoding.UTF8);
                    Append($"Wrote {htmlPath}");

                    var cssPath = Path.Combine(designRoot, "inline-styles.css");
                    await File.WriteAllTextAsync(cssPath, designSnapshot.InlineCss, Encoding.UTF8);
                    Append($"Wrote {cssPath}");

                    var fontsPath = Path.Combine(designRoot, "fonts.json");
                    var fontsJson = JsonSerializer.Serialize(designSnapshot.FontUrls, _artifactWriteOptions);
                    await File.WriteAllTextAsync(fontsPath, fontsJson, Encoding.UTF8);
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

                        await File.WriteAllTextAsync(colorsCsvPath, csvBuilder.ToString(), Encoding.UTF8);
                        Append($"Wrote {colorsCsvPath}");

                        var colorsJsonPath = Path.Combine(designRoot, "colors.json");
                        var colorsJson = JsonSerializer.Serialize(designSnapshot.ColorSwatches, _artifactWriteOptions);
                        await File.WriteAllTextAsync(colorsJsonPath, colorsJson, Encoding.UTF8);
                        Append($"Wrote {colorsJsonPath}");
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

                        await File.WriteAllBytesAsync(assetPath, content);
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
                            type: "stylesheet",
                            file: NormalizeRelativePath(relativeAssetPath),
                            sourceUrl: stylesheet?.SourceUrl,
                            resolvedUrl: stylesheet?.ResolvedUrl,
                            referencedFrom: stylesheet?.ReferencedFrom,
                            contentType: stylesheet?.ContentType,
                            fileSizeBytes: fileSize,
                            sha256: sha256,
                            fontFamily: null,
                            fontStyle: null,
                            fontWeight: null,
                            rel: null,
                            linkType: null,
                            sizes: null,
                            color: null,
                            media: null,
                            origins: null,
                            references: null));
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

                        await File.WriteAllBytesAsync(assetPath, content);
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
                            type: "font",
                            file: NormalizeRelativePath(relativeAssetPath),
                            sourceUrl: font?.SourceUrl,
                            resolvedUrl: font?.ResolvedUrl,
                            referencedFrom: font?.ReferencedFrom,
                            contentType: font?.ContentType,
                            fileSizeBytes: fileSize,
                            sha256: sha256,
                            fontFamily: font?.FontFamily,
                            fontStyle: font?.FontStyle,
                            fontWeight: font?.FontWeight,
                            rel: null,
                            linkType: null,
                            sizes: null,
                            color: null,
                            media: null,
                            origins: null,
                            references: null));
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

                        await File.WriteAllBytesAsync(assetPath, content);
                        Append($"Wrote {assetPath}");

                        var fileSize = content.Length;
                        var sha256 = Convert.ToHexString(SHA256.HashData(content));
                        var references = image?.References?
                            .Where(r => !string.IsNullOrWhiteSpace(r))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        var origins = image?.Origins?
                            .Select(o => o.ToString().ToLowerInvariant())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        imageManifest.Add(new Dictionary<string, object?>
                        {
                            ["file"] = NormalizeRelativePath(relativeAssetPath),
                            ["source_url"] = image?.SourceUrl,
                            ["resolved_url"] = image?.ResolvedUrl,
                            ["referenced_from"] = image?.ReferencedFrom,
                            ["references"] = references ?? Array.Empty<string>(),
                            ["origins"] = origins ?? Array.Empty<string>(),
                            ["content_type"] = image?.ContentType,
                            ["file_size_bytes"] = fileSize,
                            ["sha256"] = sha256
                        });

                        manifestCsvRows.Add(new DesignAssetManifestCsvRow(
                            type: "image",
                            file: NormalizeRelativePath(relativeAssetPath),
                            sourceUrl: image?.SourceUrl,
                            resolvedUrl: image?.ResolvedUrl,
                            referencedFrom: image?.ReferencedFrom,
                            contentType: image?.ContentType,
                            fileSizeBytes: fileSize,
                            sha256: sha256,
                            fontFamily: null,
                            fontStyle: null,
                            fontWeight: null,
                            rel: null,
                            linkType: null,
                            sizes: null,
                            color: null,
                            media: null,
                            origins: origins,
                            references: references));
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

                            await File.WriteAllBytesAsync(iconPath, content);
                            Append($"Wrote {iconPath}");

                            var fileSize = content.Length;
                            var sha256 = Convert.ToHexString(SHA256.HashData(content));
                            var references = icon?.References?
                                .Where(r => !string.IsNullOrWhiteSpace(r))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();

                            iconManifest.Add(new Dictionary<string, object?>
                            {
                                ["file"] = NormalizeRelativePath(relativeIconPath),
                                ["source_url"] = icon?.SourceUrl,
                                ["resolved_url"] = icon?.ResolvedUrl,
                                ["referenced_from"] = icon?.ReferencedFrom,
                                ["references"] = references ?? Array.Empty<string>(),
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
                                type: "icon",
                                file: NormalizeRelativePath(relativeIconPath),
                                sourceUrl: icon?.SourceUrl,
                                resolvedUrl: icon?.ResolvedUrl,
                                referencedFrom: icon?.ReferencedFrom,
                                contentType: icon?.ContentType,
                                fileSizeBytes: fileSize,
                                sha256: sha256,
                                fontFamily: null,
                                fontStyle: null,
                                fontWeight: null,
                                rel: icon?.Rel,
                                linkType: icon?.LinkType,
                                sizes: icon?.Sizes,
                                color: icon?.Color,
                                media: icon?.Media,
                                origins: null,
                                references: references));
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

                    var manifestPath = Path.Combine(designRoot, "assets-manifest.json");
                    var manifestJson = JsonSerializer.Serialize(manifest, _artifactWriteOptions);
                    await File.WriteAllTextAsync(manifestPath, manifestJson, Encoding.UTF8);
                    Append($"Wrote {manifestPath}");

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

                    await File.WriteAllTextAsync(manifestCsvPath, manifestCsvBuilder.ToString(), Encoding.UTF8);
                    Append($"Wrote {manifestCsvPath}");
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
            }

            if (ExportReviews)
            {
                if (SelectedPlatform == PlatformMode.WooCommerce && prods.Count > 0 && prods[0].Prices is not null)
                {
                    var ids = prods.Select(p => p.Id).Where(id => id > 0);
                    var revs = await _wooScraper.FetchStoreReviewsAsync(targetUrl, ids, log: logger);
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
                    await File.WriteAllTextAsync(path, json, Encoding.UTF8);
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
                    await File.WriteAllTextAsync(path, json, Encoding.UTF8);
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
                    await File.WriteAllTextAsync(path, json, Encoding.UTF8);
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
                    await File.WriteAllTextAsync(path, json, Encoding.UTF8);
                    Append($"Wrote {path}");
                }
                else if (attemptedSubscriptionFetch)
                {
                    Append("Subscriptions export skipped (no subscription data).");
                }
            }

            List<string> logSnapshot;
            if (App.Current is null)
            {
                logSnapshot = Logs.ToList();
            }
            else
            {
                var tempLogs = new List<string>();
                App.Current.Dispatcher.Invoke(() => tempLogs.AddRange(Logs));
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
                retrySettings.MaxDelay);

            var reportBuilder = new ManualMigrationReportBuilder();
            var report = reportBuilder.Build(reportContext);
            var reportPath = Path.Combine(storeOutputFolder, "manual-migration-report.md");
            await File.WriteAllTextAsync(reportPath, report, Encoding.UTF8);
            Append($"Manual migration report: {reportPath}");

            var manualBundleArchivePath = TryCreateManualBundle(storeOutputFolder, baseOutputFolder, storeId, timestamp, reportPath);
            if (!string.IsNullOrWhiteSpace(manualBundleArchivePath))
            {
                Append($"Manual bundle archive: {manualBundleArchivePath}");
                await TryAnnotateManualReportAsync(reportPath, manualBundleArchivePath);
            }

            SetProvisioningContext(prods, variations, configuration, pluginBundles, themeBundles, customers, coupons, orders, subscriptions, siteContent, categoryTerms);

            Append("All done.");
        }
        catch (Exception ex)
        {
            Append($"Error: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
        }
    }

    private async Task<StoreConfiguration> FetchStoreConfigurationAsync(
        string baseUrl,
        string username,
        string applicationPassword,
        IProgress<string> logger)
    {
        var configuration = new StoreConfiguration();

        var settings = await _wooScraper.FetchStoreSettingsAsync(baseUrl, username, applicationPassword, logger);
        if (settings.Count > 0)
        {
            configuration.StoreSettings.AddRange(settings);
            logger.Report($"Captured {settings.Count} store settings entries.");
        }
        else
        {
            logger.Report("No store settings returned or endpoint unavailable.");
        }

        var zones = await _wooScraper.FetchShippingZonesAsync(baseUrl, username, applicationPassword, logger);
        if (zones.Count > 0)
        {
            configuration.ShippingZones.AddRange(zones);
            logger.Report($"Captured {zones.Count} shipping zones.");
        }
        else
        {
            logger.Report("No shipping zones returned or endpoint unavailable.");
        }

        var gateways = await _wooScraper.FetchPaymentGatewaysAsync(baseUrl, username, applicationPassword, logger);
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
        IProgress<string> logger)
    {
        var bundles = new List<ExtensionArtifact>();
        Directory.CreateDirectory(rootFolder);

        foreach (var plugin in plugins)
        {
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

                var options = await _wooScraper.FetchPluginOptionsAsync(baseUrl, username, applicationPassword, plugin, logger, settingsSnapshot);
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
                        await File.WriteAllTextAsync(optionsPath, json, Encoding.UTF8);
                        var keys = plugin.OptionData.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
                        plugin.OptionKeys.AddRange(keys);
                        logger.Report($"Captured {keys.Count} options for plugin {plugin.Name ?? slug ?? plugin.PluginFile ?? folderName}.");
                    }
                }

                var manifest = await _wooScraper.FetchPluginAssetManifestAsync(baseUrl, username, applicationPassword, plugin, logger);
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
                            await File.WriteAllTextAsync(manifestPath, manifestJson, Encoding.UTF8);
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
                var downloaded = await _wooScraper.DownloadPluginArchiveAsync(baseUrl, username, applicationPassword, plugin, archivePath, logger);
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
        IProgress<string> logger)
    {
        var bundles = new List<ExtensionArtifact>();
        Directory.CreateDirectory(rootFolder);

        foreach (var theme in themes)
        {
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

                var options = await _wooScraper.FetchThemeOptionsAsync(baseUrl, username, applicationPassword, theme, logger, settingsSnapshot);
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
                        await File.WriteAllTextAsync(optionsPath, json, Encoding.UTF8);
                        var keys = theme.OptionData.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
                        theme.OptionKeys.AddRange(keys);
                        logger.Report($"Captured {keys.Count} options for theme {theme.Name ?? slug ?? folderName}.");
                    }
                }

                var manifest = await _wooScraper.FetchThemeAssetManifestAsync(baseUrl, username, applicationPassword, theme, logger);
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
                            await File.WriteAllTextAsync(manifestPath, manifestJson, Encoding.UTF8);
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
                var downloaded = await _wooScraper.DownloadThemeArchiveAsync(baseUrl, username, applicationPassword, theme, archivePath, logger);
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
        IProgress<string>? logger,
        IDictionary<string, MediaReference>? mediaMap = null)
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
                var bytes = await _httpClient.GetByteArrayAsync(uri);
                await File.WriteAllBytesAsync(absolutePath, bytes);
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
        IProgress<string>? logger)
    {
        var map = new Dictionary<string, MediaReference>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in mediaItems)
        {
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
                    using var response = await _httpClient.GetAsync(item.SourceUrl);
                    if (!response.IsSuccessStatusCode)
                    {
                        logger?.Report($"Failed to download {item.SourceUrl}: {(int)response.StatusCode} ({response.ReasonPhrase}).");
                        continue;
                    }

                    await using var input = await response.Content.ReadAsStreamAsync();
                    await using var output = File.Create(absolutePath);
                    await input.CopyToAsync(output);
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
        IProgress<string>? logger)
    {
        var existingByUrl = mediaLibrary
            .Where(m => !string.IsNullOrWhiteSpace(m.SourceUrl))
            .GroupBy(m => m.SourceUrl!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var content in contentItems)
        {
            if (content is null)
            {
                continue;
            }

            content.ReferencedMediaFiles.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var url in content.ReferencedMediaUrls)
            {
                if (string.IsNullOrWhiteSpace(url) || !seen.Add(url))
                {
                    continue;
                }

                var reference = await EnsureMediaReferenceAsync(url, mediaRoot, mediaMap, logger);
                if (reference is null)
                {
                    continue;
                }

                content.ReferencedMediaFiles.Add(reference.RelativePath);
                EnsureMediaLibraryEntry(url, reference, existingByUrl, mediaLibrary);
            }

            if (!string.IsNullOrWhiteSpace(content.FeaturedMediaUrl))
            {
                var reference = await EnsureMediaReferenceAsync(content.FeaturedMediaUrl!, mediaRoot, mediaMap, logger);
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

    private async Task EnrichPublicExtensionFootprintsAsync(IList<PublicExtensionFootprint> footprints, IProgress<string> log)
    {
        if (footprints.Count == 0)
        {
            return;
        }

        var cache = new Dictionary<string, WordPressDirectoryEntry?>(StringComparer.OrdinalIgnoreCase);

        foreach (var footprint in footprints)
        {
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
                    ? await _wpDirectoryClient.GetThemeAsync(slug).ConfigureAwait(false)
                    : await _wpDirectoryClient.GetPluginAsync(slug).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                ApplyDirectoryMetadata(footprint, null, "lookup_error");
                log.Report($"WordPress.org lookup failed for slug '{slug}': {ex.Message}");
                cache[cacheKey] = null;
                await Task.Delay(DirectoryLookupDelay).ConfigureAwait(false);
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

            await Task.Delay(DirectoryLookupDelay).ConfigureAwait(false);
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
        IProgress<string>? logger)
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
                using var response = await _httpClient.GetAsync(uri);
                if (!response.IsSuccessStatusCode)
                {
                    logger?.Report($"Failed to download {url}: {(int)response.StatusCode} ({response.ReasonPhrase}).");
                    return null;
                }

                await using var input = await response.Content.ReadAsStreamAsync();
                await using var output = File.Create(absolutePath);
                await input.CopyToAsync(output);
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

    private string? TryCreateManualBundle(string storeOutputFolder, string baseOutputFolder, string storeId, string timestamp, string reportPath)
    {
        var deliverables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(reportPath))
        {
            deliverables.Add(reportPath);
        }

        var designFolder = Path.Combine(storeOutputFolder, "design");
        if (Directory.Exists(designFolder))
        {
            deliverables.Add(designFolder);
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(storeOutputFolder, $"{storeId}_{timestamp}_*", SearchOption.TopDirectoryOnly))
            {
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
                Directory.Delete(stagingFolder, recursive: true);
            }

            Directory.CreateDirectory(stagingFolder);

            foreach (var item in deliverables)
            {
                if (Directory.Exists(item))
                {
                    var destinationDirectory = Path.Combine(stagingFolder, Path.GetFileName(item));
                    CopyDirectory(item, destinationDirectory);
                }
                else if (File.Exists(item))
                {
                    var destinationFile = Path.Combine(stagingFolder, Path.GetFileName(item));
                    File.Copy(item, destinationFile, overwrite: true);
                }
            }

            var archivePath = Path.Combine(baseOutputFolder, $"{stagingFolderName}.zip");
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

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

    private static async Task TryAnnotateManualReportAsync(string reportPath, string archivePath)
    {
        try
        {
            if (!File.Exists(reportPath))
            {
                return;
            }

            var summaryNote = $"- **Archive:** `{archivePath}`";
            var content = await File.ReadAllTextAsync(reportPath, Encoding.UTF8).ConfigureAwait(false);
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
                updatedContent = content.Insert(insertionIndex, insertionText);
            }
            else
            {
                updatedContent = content + Environment.NewLine + insertionText;
            }

            await File.WriteAllTextAsync(reportPath, updatedContent, Encoding.UTF8).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort annotation; ignore failures.
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return;
        }

        Directory.CreateDirectory(destinationDirectory);

        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            var targetFile = Path.Combine(destinationDirectory, Path.GetFileName(file));
            File.Copy(file, targetFile, overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            var targetDirectory = Path.Combine(destinationDirectory, Path.GetFileName(directory));
            CopyDirectory(directory, targetDirectory);
        }
    }

    private void Append(string message)
    {
        App.Current?.Dispatcher.Invoke(() => Logs.Add(message));
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

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace("\\", "/");
    }

    private static string SanitizeSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "segment";
        }

        var sanitized = SanitizeForPath(value);
        return string.IsNullOrWhiteSpace(sanitized) ? "segment" : sanitized;
    }

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
        App.Current?.Dispatcher.Invoke(() =>
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

    private async Task OnReplicateStoreAsync()
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
            IProgress<string> logger = new Progress<string>(Append);
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

            if (_lastProvisioningContext.PluginBundles.Count > 0)
            {
                Append($"Uploading {_lastProvisioningContext.PluginBundles.Count} plugin bundles…");
                await _wooProvisioningService.UploadPluginsAsync(settings, _lastProvisioningContext.PluginBundles, logger);
            }

            if (_lastProvisioningContext.ThemeBundles.Count > 0)
            {
                Append($"Uploading {_lastProvisioningContext.ThemeBundles.Count} theme bundles…");
                await _wooProvisioningService.UploadThemesAsync(settings, _lastProvisioningContext.ThemeBundles, logger);
            }

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
                progress: logger);
        }
        catch (Exception ex)
        {
            Append($"Provisioning failed: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
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

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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
