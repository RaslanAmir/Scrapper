using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WcScraper.Core;
using WcScraper.Wpf;
using WcScraper.Wpf.Services;

namespace WcScraper.Wpf.ViewModels;

public sealed class ProvisioningViewModel : INotifyPropertyChanged, IProvisioningWorkflow
{
    private readonly WooProvisioningService _wooProvisioningService;
    private ProvisioningContext? _lastProvisioningContext;
    private bool _isBusy;
    private string _targetStoreUrl = string.Empty;
    private string _targetConsumerKey = string.Empty;
    private string _targetConsumerSecret = string.Empty;
    private bool _importStoreConfiguration;

    public ProvisioningViewModel()
        : this(new WooProvisioningService())
    {
    }

    public ProvisioningViewModel(WooProvisioningService wooProvisioningService)
    {
        _wooProvisioningService = wooProvisioningService ?? throw new ArgumentNullException(nameof(wooProvisioningService));
        ReplicateCommand = new RelayCommand(OnReplicateRequested, CanExecuteReplicate);
    }

    public RelayCommand ReplicateCommand { get; }

    public bool CanReplicate => _lastProvisioningContext is { Products.Count: > 0 };

    public string TargetStoreUrl
    {
        get => _targetStoreUrl;
        set
        {
            var newValue = value?.Trim() ?? string.Empty;
            if (string.Equals(_targetStoreUrl, newValue, StringComparison.Ordinal))
            {
                return;
            }

            _targetStoreUrl = newValue;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasTargetCredentials));
            ReplicateCommand.RaiseCanExecuteChanged();
        }
    }

    public string TargetConsumerKey
    {
        get => _targetConsumerKey;
        set
        {
            var newValue = value?.Trim() ?? string.Empty;
            if (string.Equals(_targetConsumerKey, newValue, StringComparison.Ordinal))
            {
                return;
            }

            _targetConsumerKey = newValue;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasTargetCredentials));
            ReplicateCommand.RaiseCanExecuteChanged();
        }
    }

    public string TargetConsumerSecret
    {
        get => _targetConsumerSecret;
        set
        {
            var newValue = value?.Trim() ?? string.Empty;
            if (string.Equals(_targetConsumerSecret, newValue, StringComparison.Ordinal))
            {
                return;
            }

            _targetConsumerSecret = newValue;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasTargetCredentials));
            ReplicateCommand.RaiseCanExecuteChanged();
        }
    }

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
        }
    }

    public bool HasTargetCredentials
        => !string.IsNullOrWhiteSpace(TargetStoreUrl)
            && !string.IsNullOrWhiteSpace(TargetConsumerKey)
            && !string.IsNullOrWhiteSpace(TargetConsumerSecret);

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
            ReplicateCommand.RaiseCanExecuteChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? ReplicateRequested;

    public async Task ExecuteProvisioningAsync(
        string? wordPressUsername,
        string? wordPressApplicationPassword,
        Func<string, string?, string?, IReadOnlyDictionary<string, object?>?, LogLevel, LoggerProgressAdapter> createOperationLogger,
        Func<ILogger, IProgress<string>> requireProgressLogger,
        Action<string> append,
        CancellationToken cancellationToken)
    {
        if (createOperationLogger is null)
        {
            throw new ArgumentNullException(nameof(createOperationLogger));
        }

        if (requireProgressLogger is null)
        {
            throw new ArgumentNullException(nameof(requireProgressLogger));
        }

        if (append is null)
        {
            throw new ArgumentNullException(nameof(append));
        }

        if (_lastProvisioningContext is null || _lastProvisioningContext.Products.Count == 0)
        {
            append("Run an export before provisioning.");
            return;
        }

        if (!HasTargetCredentials)
        {
            append("Enter the target store URL, consumer key, and consumer secret before provisioning.");
            return;
        }

        try
        {
            var targetStoreUrl = TargetStoreUrl;
            var targetConsumerKey = TargetConsumerKey;
            var targetConsumerSecret = TargetConsumerSecret;
            var provisioningContext = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ProductCount"] = _lastProvisioningContext.Products.Count,
                ["TargetUrl"] = targetStoreUrl
            };

            var operationLogger = createOperationLogger(
                "WooProvisioning",
                targetStoreUrl,
                "WooCommerce",
                provisioningContext,
                LogLevel.Information);
            var progressLogger = requireProgressLogger(operationLogger);
            var settings = new WooProvisioningSettings(
                targetStoreUrl,
                targetConsumerKey,
                targetConsumerSecret,
                wordPressUsername,
                wordPressApplicationPassword);

            append($"Provisioning {_lastProvisioningContext.Products.Count} products to {settings.BaseUrl}…");

            StoreConfiguration? configuration = null;
            if (ImportStoreConfiguration)
            {
                configuration = _lastProvisioningContext.Configuration;
                if (configuration is null || !HasConfigurationData(configuration))
                {
                    append("No stored configuration available. Run an export with configuration enabled if you wish to replicate settings.");
                    configuration = null;
                }
                else
                {
                    append("Applying captured store configuration before provisioning products…");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (_lastProvisioningContext.PluginBundles.Count > 0)
            {
                append($"Uploading {_lastProvisioningContext.PluginBundles.Count} plugin bundles…");
                await _wooProvisioningService.UploadPluginsAsync(settings, _lastProvisioningContext.PluginBundles, progressLogger, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (_lastProvisioningContext.ThemeBundles.Count > 0)
            {
                append($"Uploading {_lastProvisioningContext.ThemeBundles.Count} theme bundles…");
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
            append($"Provisioning failed: {ex.Message}");
        }
    }

    public void ResetProvisioningContext()
    {
        _lastProvisioningContext = null;
        OnPropertyChanged(nameof(CanReplicate));
        ReplicateCommand.RaiseCanExecuteChanged();
    }

    public void SetProvisioningContext(
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

    internal void SetHostBusy(bool isBusy)
    {
        IsBusy = isBusy;
    }

    void IProvisioningWorkflow.SetHostBusy(bool isBusy) => SetHostBusy(isBusy);

    internal static bool HasConfigurationData(StoreConfiguration configuration)
    {
        if (configuration is null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        return configuration.StoreSettings.Count > 0
            || configuration.ShippingZones.Count > 0
            || configuration.PaymentGateways.Count > 0;
    }

    private bool CanExecuteReplicate() => !IsBusy && CanReplicate && HasTargetCredentials;

    private void OnReplicateRequested()
    {
        if (!CanExecuteReplicate())
        {
            return;
        }

        ReplicateRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

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
            clones = list.Select(CloneProduct).ToList();
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
}
