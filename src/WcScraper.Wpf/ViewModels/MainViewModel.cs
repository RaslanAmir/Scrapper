
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
using System.Threading.Tasks;
using WcScraper.Core;
using WcScraper.Core.Exporters;
using WcScraper.Core.Shopify;
using WcScraper.Wpf.Services;

namespace WcScraper.Wpf.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IDialogService _dialogs;
    private readonly WooScraper _wooScraper;
    private readonly ShopifyScraper _shopifyScraper;
    private readonly HttpClient _httpClient;
    private bool _isRunning;
    private string _storeUrl = "";
    private string _outputFolder = Path.GetFullPath("output");
    private bool _expCsv = true;
    private bool _expShopify = true;
    private bool _expWoo = true;
    private bool _expReviews = true;
    private bool _expXlsx = false;
    private bool _expJsonl = false;
    private PlatformMode _selectedPlatform = PlatformMode.WooCommerce;
    private string _shopifyStoreUrl = "";
    private string _shopifyAdminAccessToken = "";
    private string _shopifyStorefrontAccessToken = "";
    private string _shopifyApiKey = "";
    private string _shopifyApiSecret = "";

    public MainViewModel(IDialogService dialogs)
        : this(dialogs, new WooScraper(), new ShopifyScraper(), new HttpClient())
    {
    }

    internal MainViewModel(IDialogService dialogs, WooScraper wooScraper, ShopifyScraper shopifyScraper)
        : this(dialogs, wooScraper, shopifyScraper, new HttpClient())
    {
    }

    internal MainViewModel(IDialogService dialogs, WooScraper wooScraper, ShopifyScraper shopifyScraper, HttpClient httpClient)
    {
        _dialogs = dialogs;
        _wooScraper = wooScraper;
        _shopifyScraper = shopifyScraper;
        _httpClient = httpClient;
        BrowseCommand = new RelayCommand(OnBrowse);
        RunCommand = new RelayCommand(async () => await OnRunAsync(), () => !IsRunning);
        SelectAllCategoriesCommand = new RelayCommand(() => SetSelection(CategoryChoices, true));
        ClearCategoriesCommand = new RelayCommand(() => SetSelection(CategoryChoices, false));
        SelectAllTagsCommand = new RelayCommand(() => SetSelection(TagChoices, true));
        ClearTagsCommand = new RelayCommand(() => SetSelection(TagChoices, false));
        ExportCollectionsCommand = new RelayCommand(OnExportCollections);
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
    public bool IsRunning { get => _isRunning; private set { _isRunning = value; OnPropertyChanged(); (RunCommand as RelayCommand)?.RaiseCanExecuteChanged(); } }

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

    private async Task OnRunAsync()
    {
        var targetUrl = SelectedPlatform == PlatformMode.WooCommerce ? StoreUrl : ShopifyStoreUrl;
        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            Append("Please enter a store URL (e.g., https://example.com).");
            return;
        }

        try
        {
            IsRunning = true;
            var baseOutputFolder = ResolveBaseOutputFolder();
            Directory.CreateDirectory(baseOutputFolder);
            var logger = new Progress<string>(Append);

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
                var relativePaths = await DownloadProductImagesAsync(product, imagesFolder, logger);
                product.ImageFilePaths = relativePaths;
                if (rowsById.TryGetValue(product.Id, out var row))
                {
                    row.ImageFilePaths = relativePaths;
                }
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

            if (ExportShopify)
            {
                var rows = (variations.Count > 0)
                    ? Mappers.ToShopifyCsvWithVariants(prods, variations, StoreUrl).ToList()
                    : Mappers.ToShopifyCsv(prods, StoreUrl).ToList();
                var path = Path.Combine(storeOutputFolder, $"{storeId}_{timestamp}_shopify_products.csv");
                CsvExporter.Write(path, rows);
                Append($"Wrote {path}");
            }

            if (ExportWoo)
            {
                var rows = Mappers.ToWooImporterCsv(prods).ToList();
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

    private async Task<string?> DownloadProductImagesAsync(StoreProduct product, string imagesFolder, IProgress<string>? logger)
    {
        if (product.Images is null || product.Images.Count == 0)
        {
            return null;
        }

        var baseName = SanitizeForPath(product.Name ?? product.Slug ?? $"product-{product.Id}");
        var uniquePrefix = $"{baseName}-{product.Id.ToString(CultureInfo.InvariantCulture)}";
        var relativePaths = new List<string>();
        var index = 1;

        foreach (var image in product.Images)
        {
            var src = image.Src;
            if (string.IsNullOrWhiteSpace(src))
            {
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

            var fileName = $"{uniquePrefix}-{index}{extension}";
            var absolutePath = Path.Combine(imagesFolder, fileName);
            var relativePath = Path.Combine("images", fileName).Replace(Path.DirectorySeparatorChar, '/');

            try
            {
                logger?.Report($"Downloading {src} -> {relativePath}");
                var bytes = await _httpClient.GetByteArrayAsync(uri);
                await File.WriteAllBytesAsync(absolutePath, bytes);
                relativePaths.Add(relativePath);
                index++;
            }
            catch (Exception ex)
            {
                logger?.Report($"Failed to download {src}: {ex.Message}");
            }
        }

        return relativePaths.Count > 0 ? string.Join(", ", relativePaths) : null;
    }

    private void Append(string message)
    {
        App.Current?.Dispatcher.Invoke(() => Logs.Add(message));
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
