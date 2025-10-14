
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using WcScraper.Core;
using WcScraper.Core.Exporters;
using WcScraper.Wpf.Services;

namespace WcScraper.Wpf.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IDialogService _dialogs;
    private readonly WooScraper _scraper = new();
    private bool _isRunning;
    private string _storeUrl = "";
    private string _outputFolder = Path.GetFullPath("output");
    private bool _expCsv = true;
    private bool _expShopify = true;
    private bool _expWoo = true;
    private bool _expReviews = true;
    private bool _expXlsx = false;
    private bool _expJsonl = false;

    public MainViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        BrowseCommand = new RelayCommand(OnBrowse);
        RunCommand = new RelayCommand(async () => await OnRunAsync(), () => !IsRunning);
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

    // Selectable terms for filters
    public ObservableCollection<SelectableTerm> CategoryChoices { get; } = new();
    public ObservableCollection<SelectableTerm> TagChoices { get; } = new();

    public ObservableCollection<string> Logs { get; } = new();

    public RelayCommand BrowseCommand { get; }
    public RelayCommand RunCommand { get; }

    private void OnBrowse()
    {
        var chosen = _dialogs.BrowseForFolder(OutputFolder);
        if (!string.IsNullOrWhiteSpace(chosen))
            OutputFolder = chosen;
    }

    private async Task LoadFiltersForStoreAsync(string baseUrl, IProgress<string> logger)
    {
        try
        {
            var cats = await _scraper.FetchProductCategoriesAsync(baseUrl, logger);
            var tags = await _scraper.FetchProductTagsAsync(baseUrl, logger);
            App.Current?.Dispatcher.Invoke(() =>
            {
                CategoryChoices.Clear();
                foreach (var c in cats) CategoryChoices.Add(new SelectableTerm(c));
                TagChoices.Clear();
                foreach (var t in tags) TagChoices.Add(new SelectableTerm(t));
            });
        }
        catch (Exception ex)
        {
            logger.Report($"Filter load failed: {ex.Message}");
        }
    }

    private async Task OnRunAsync()
    {
        if (string.IsNullOrWhiteSpace(StoreUrl))
        {
            Append("Please enter a store URL (e.g., https://example.com).");
            return;
        }

        try
        {
            IsRunning = true;
            Directory.CreateDirectory(OutputFolder);
            var logger = new Progress<string>(Append);

            // Refresh filters for this store
            await LoadFiltersForStoreAsync(StoreUrl, logger);

            // Build filters
            string? categoryFilter = null;
            var selectedCats = CategoryChoices.Where(x => x.IsSelected).Select(x => x.Term.Id.ToString()).ToList();
            if (selectedCats.Count > 0) categoryFilter = string.Join(",", selectedCats);

            string? tagFilter = null;
            var selectedTags = TagChoices.Where(x => x.IsSelected).Select(x => x.Term.Id.ToString()).ToList();
            if (selectedTags.Count > 0) tagFilter = string.Join(",", selectedTags);

            Append($"Fetching products via Store API… {StoreUrl}");
            var prods = await _scraper.FetchStoreProductsAsync(StoreUrl, log: logger, categoryFilter: categoryFilter, tagFilter: tagFilter);

            if (prods.Count == 0)
            {
                Append("Store API empty. Trying WordPress REST fallback (basic fields)…");
                prods = await _scraper.FetchWpProductsBasicAsync(StoreUrl, log: logger);
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
            var variations = new List<StoreProduct>();
            if (parentIds.Count > 0)
            {
                Append($"Fetching variations for {parentIds.Count} variable products…");
                variations = await _scraper.FetchStoreVariationsAsync(StoreUrl, parentIds, log: logger);
                Append($"Found {variations.Count} variations.");
            }

            // Generic rows projection
            var genericRows = Mappers.ToGenericRows(prods).ToList();
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
                ["regular_price"] = r.RegularPrice,
                ["sale_price"] = r.SalePrice,
                ["price"] = r.Price,
                ["currency"] = r.Currency,
                ["in_stock"] = r.InStock,
                ["stock_status"] = r.StockStatus,
                ["average_rating"] = r.AverageRating,
                ["review_count"] = r.ReviewCount,
                ["categories"] = r.Categories,
                ["images"] = r.Images,
            }).ToList();

            if (ExportCsv)
            {
                var path = Path.Combine(OutputFolder, "products.csv");
                CsvExporter.Write(path, genericDicts);
                Append($"Wrote {path}");
            }
            if (ExportXlsx)
            {
                var path = Path.Combine(OutputFolder, "products.xlsx");
                XlsxExporter.Write(path, genericDicts);
                Append($"Wrote {path}");
            }
            if (ExportJsonl)
            {
                var path = Path.Combine(OutputFolder, "products.jsonl");
                JsonlExporter.Write(path, genericDicts);
                Append($"Wrote {path}");
            }

            if (ExportShopify)
            {
                var rows = (variations.Count > 0)
                    ? Mappers.ToShopifyCsvWithVariants(prods, variations, StoreUrl).ToList()
                    : Mappers.ToShopifyCsv(prods, StoreUrl).ToList();
                var path = Path.Combine(OutputFolder, "shopify_products.csv");
                CsvExporter.Write(path, rows);
                Append($"Wrote {path}");
            }

            if (ExportWoo)
            {
                var rows = Mappers.ToWooImporterCsv(prods).ToList();
                var path = Path.Combine(OutputFolder, "woocommerce_products.csv");
                CsvExporter.Write(path, rows);
                Append($"Wrote {path}");
            }

            if (ExportReviews)
            {
                if (prods.Count > 0 && prods[0].Prices is not null)
                {
                    var ids = prods.Select(p => p.Id).Where(id => id > 0);
                    var revs = await _scraper.FetchStoreReviewsAsync(StoreUrl, ids, log: logger);
                    if (revs.Count > 0)
                    {
                        var path = Path.Combine(OutputFolder, "reviews.csv");
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
                    Append("Reviews skipped (Store API product meta not present).");
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

    private void Append(string message)
    {
        App.Current?.Dispatcher.Invoke(() => Logs.Add(message));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// Helper wrapper for selectable filters
public sealed class SelectableTerm : INotifyPropertyChanged
{
    public TermItem Term { get; }
    private bool _isSelected;

    public SelectableTerm(TermItem term) { Term = term; }
    public string? Name => Term.Name;
    public bool IsSelected { get => _isSelected; set { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } }

    public event PropertyChangedEventHandler? PropertyChanged;
}
