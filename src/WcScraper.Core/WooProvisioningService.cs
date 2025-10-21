using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WcScraper.Core;

public sealed class WooProvisioningSettings
{
    public WooProvisioningSettings(
        string baseUrl,
        string consumerKey,
        string consumerSecret,
        string? wordpressUsername = null,
        string? wordpressApplicationPassword = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("Base URL is required.", nameof(baseUrl));
        }
        if (string.IsNullOrWhiteSpace(consumerKey))
        {
            throw new ArgumentException("Consumer key is required.", nameof(consumerKey));
        }
        if (string.IsNullOrWhiteSpace(consumerSecret))
        {
            throw new ArgumentException("Consumer secret is required.", nameof(consumerSecret));
        }

        BaseUrl = WooScraper.CleanBaseUrl(baseUrl);
        ConsumerKey = consumerKey.Trim();
        ConsumerSecret = consumerSecret.Trim();
        WordPressUsername = string.IsNullOrWhiteSpace(wordpressUsername) ? null : wordpressUsername.Trim();
        WordPressApplicationPassword = string.IsNullOrWhiteSpace(wordpressApplicationPassword) ? null : wordpressApplicationPassword.Trim();
    }

    public string BaseUrl { get; }
    public string ConsumerKey { get; }
    public string ConsumerSecret { get; }
    public string? WordPressUsername { get; }
    public string? WordPressApplicationPassword { get; }
    public bool HasWordPressCredentials =>
        !string.IsNullOrWhiteSpace(WordPressUsername) && !string.IsNullOrWhiteSpace(WordPressApplicationPassword);
}

public sealed class WooProvisioningService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly JsonSerializerOptions _readOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
    private readonly JsonSerializerOptions _writeOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public WooProvisioningService(HttpClient? httpClient = null)
    {
        if (httpClient is null)
        {
            _httpClient = new HttpClient();
            _ownsClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _ownsClient = false;
        }

        _httpClient.Timeout = TimeSpan.FromSeconds(60);
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("wc-local-scraper-provisioner/0.1 (+https://localhost)");
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    public async Task ProvisionAsync(
        WooProvisioningSettings settings,
        IEnumerable<StoreProduct> products,
        IEnumerable<ProvisioningVariableProduct>? variableProducts = null,
        IEnumerable<StoreProduct>? variations = null,
        StoreConfiguration? configuration = null,
        IEnumerable<WooCustomer>? customers = null,
        IEnumerable<WooCoupon>? coupons = null,
        IEnumerable<WooOrder>? orders = null,
        IEnumerable<WooSubscription>? subscriptions = null,
        WordPressSiteContent? siteContent = null,
        IReadOnlyDictionary<int, int>? authorIdMap = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        if (products is null) throw new ArgumentNullException(nameof(products));

        var productList = products.Where(p => p is not null).ToList();
        var variableProductList = SanitizeVariableProducts(variableProducts);
        var variationList = new List<StoreProduct>();
        var seenVariations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddVariation(StoreProduct? candidate)
        {
            if (candidate is null)
            {
                return;
            }

            var identity = BuildVariationIdentity(candidate);
            if (identity is not null && !seenVariations.Add(identity))
            {
                return;
            }

            variationList.Add(candidate);
        }

        foreach (var group in variableProductList)
        {
            foreach (var variation in group.Variations)
            {
                AddVariation(variation);
            }
        }

        if (variations is not null)
        {
            foreach (var variation in variations)
            {
                AddVariation(variation);
            }
        }

        var customerList = customers?.Where(c => c is not null).ToList() ?? new List<WooCustomer>();
        var couponList = coupons?.Where(c => c is not null).ToList() ?? new List<WooCoupon>();
        var orderList = orders?.Where(o => o is not null).ToList() ?? new List<WooOrder>();
        var subscriptionList = subscriptions?.Where(s => s is not null).ToList() ?? new List<WooSubscription>();
        var variationsByParentId = variationList
            .Where(v => v?.ParentId is int id && id > 0)
            .GroupBy(v => v!.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.Where(v => v is not null).Select(v => v!).ToList());

        var variationsByParentReference = new Dictionary<StoreProduct, List<StoreProduct>>();

        foreach (var group in variableProductList)
        {
            var parentId = group.Parent.Id > 0
                ? group.Parent.Id
                : group.Variations.FirstOrDefault(v => v?.ParentId is int id && id > 0)?.ParentId ?? 0;
            if (parentId <= 0)
            {
                if (!variationsByParentReference.TryGetValue(group.Parent, out var referenceList))
                {
                    referenceList = new List<StoreProduct>();
                    variationsByParentReference[group.Parent] = referenceList;
                }

                foreach (var variation in group.Variations)
                {
                    if (variation is null)
                    {
                        continue;
                    }

                    if (!referenceList.Contains(variation))
                    {
                        referenceList.Add(variation);
                    }
                }

                continue;
            }

            if (!variationsByParentId.TryGetValue(parentId, out var list))
            {
                list = new List<StoreProduct>();
                variationsByParentId[parentId] = list;
            }

            foreach (var variation in group.Variations)
            {
                if (variation is null)
                {
                    continue;
                }

                if (!list.Contains(variation))
                {
                    list.Add(variation);
                }
            }

            if (!variationsByParentReference.TryGetValue(group.Parent, out var byReference))
            {
                byReference = new List<StoreProduct>();
                variationsByParentReference[group.Parent] = byReference;
            }

            foreach (var variation in group.Variations)
            {
                if (variation is null)
                {
                    continue;
                }

                if (!byReference.Contains(variation))
                {
                    byReference.Add(variation);
                }
            }
        }
        var hasContent = siteContent is not null
                          && ((siteContent.Pages?.Count ?? 0) > 0
                              || (siteContent.Posts?.Count ?? 0) > 0
                              || (siteContent.MediaLibrary?.Count ?? 0) > 0
                              || (siteContent.Menus?.Menus.Count ?? 0) > 0
                              || (siteContent.Widgets?.Widgets.Count ?? 0) > 0);

        if (productList.Count == 0 && customerList.Count == 0 && couponList.Count == 0 && orderList.Count == 0 && subscriptionList.Count == 0 && !hasContent)
        {
            progress?.Report("No artifacts to provision.");
            return;
        }

        var baseUrl = settings.BaseUrl;

        if (configuration is not null)
        {
            await ApplyStoreConfigurationAsync(baseUrl, settings, configuration, progress, cancellationToken);
        }

        MediaProvisioningResult? mediaResult = null;
        Dictionary<int, int>? pageIdMap = null;
        Dictionary<int, int>? postIdMap = null;
        WordPressMenuCollection? menuCollection = null;
        WordPressWidgetSnapshot? widgetSnapshot = null;

        if (siteContent is not null)
        {
            mediaResult = await EnsureMediaLibraryAsync(baseUrl, settings, siteContent.MediaLibrary, siteContent.MediaRootDirectory, progress, cancellationToken);
            pageIdMap = await EnsurePagesAsync(
                baseUrl,
                settings,
                siteContent.Pages,
                mediaResult,
                authorIdMap,
                progress,
                cancellationToken);
            postIdMap = await EnsurePostsAsync(
                baseUrl,
                settings,
                siteContent.Posts,
                mediaResult,
                authorIdMap,
                progress,
                cancellationToken);
            menuCollection = siteContent.Menus;
            widgetSnapshot = siteContent.Widgets;
        }

        var productIdMap = new Dictionary<int, int>();
        var customerIdMap = new Dictionary<int, int>();
        var couponIdMap = new Dictionary<int, int>();
        var categoryIdMap = new Dictionary<int, int>();
        var tagIdMap = new Dictionary<int, int>();
        var couponCodeMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (productList.Count > 0)
        {
            progress?.Report($"Preparing taxonomies for {productList.Count} products…");
            var categorySeeds = CollectTaxonomySeeds(
                productList.SelectMany(p => p.Categories ?? Enumerable.Empty<Category>()),
                c => c.Name,
                c => c.Slug,
                c => c.Id,
                c => c.ParentId,
                c => c.ParentSlug,
                c => c.ParentName);
            var tagSeeds = CollectTaxonomySeeds(productList.SelectMany(p => p.Tags ?? Enumerable.Empty<ProductTag>()), t => t.Name, t => t.Slug);
            var attributeSeeds = CollectAttributeSeeds(productList.Concat(variationList));

            var categoryMap = await EnsureTaxonomiesAsync(baseUrl, settings, "categories", categorySeeds, progress, cancellationToken);
            categoryIdMap = BuildCategoryIdMap(productList, categoryMap);
            var tagMap = await EnsureTaxonomiesAsync(baseUrl, settings, "tags", tagSeeds, progress, cancellationToken);
            tagIdMap = BuildTagIdMap(productList, tagMap);
            var attributeMap = await EnsureAttributesAsync(baseUrl, settings, attributeSeeds, progress, cancellationToken);

            progress?.Report("Provisioning products…");
            var mediaCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (mediaResult is not null)
            {
                foreach (var pair in mediaResult.UrlToIdMap)
                {
                    if (!mediaCache.ContainsKey(pair.Key))
                    {
                        mediaCache[pair.Key] = pair.Value;
                    }
                }

                foreach (var pair in mediaResult.IdMap)
                {
                    var key = pair.Key.ToString(CultureInfo.InvariantCulture);
                    if (!mediaCache.ContainsKey(key))
                    {
                        mediaCache[key] = pair.Value;
                    }
                }
            }

            var productLookup = productList
                .Where(p => p.Id > 0)
                .ToDictionary(p => p.Id, p => p);

            foreach (var product in productList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                variationsByParentId.TryGetValue(product.Id, out var productVariations);
                if ((productVariations is null || productVariations.Count == 0)
                    && variationsByParentReference.TryGetValue(product, out var referenceVariations)
                    && referenceVariations.Count > 0)
                {
                    productVariations = referenceVariations;
                }
                var ensuredId = await EnsureProductAsync(
                    baseUrl,
                    settings,
                    product,
                    categoryMap,
                    tagMap,
                    attributeMap,
                    mediaCache,
                    productVariations,
                    progress,
                    cancellationToken);
                if (product.Id > 0)
                {
                    productIdMap[product.Id] = ensuredId;
                }
            }

            if (variationList.Count > 0)
            {
                await EnsureVariationsAsync(
                    baseUrl,
                    settings,
                    productLookup,
                    variationList,
                    variableProductList,
                    attributeMap,
                    mediaCache,
                    productIdMap,
                    progress,
                    cancellationToken);
            }
        }

        if (customerList.Count > 0)
        {
            await EnsureCustomersAsync(baseUrl, settings, customerList, customerIdMap, progress, cancellationToken);
        }

        if (couponList.Count > 0)
        {
            await EnsureCouponsAsync(baseUrl, settings, couponList, productIdMap, categoryIdMap, couponIdMap, progress, cancellationToken);
            foreach (var coupon in couponList)
            {
                if (!string.IsNullOrWhiteSpace(coupon.Code) && coupon.Id > 0 && couponIdMap.TryGetValue(coupon.Id, out var mapped))
                {
                    couponCodeMap[coupon.Code!] = mapped;
                }
            }
        }

        var taxRateLookup = TaxRateLookup.Empty;
        if (orderList.Count > 0 || subscriptionList.Count > 0)
        {
            taxRateLookup = await BuildTaxRateLookupAsync(baseUrl, settings, orderList, subscriptionList, cancellationToken);
        }

        if (orderList.Count > 0)
        {
            await EnsureOrdersAsync(
                baseUrl,
                settings,
                orderList,
                productIdMap,
                customerIdMap,
                couponCodeMap,
                taxRateLookup,
                progress,
                cancellationToken);
        }

        if (subscriptionList.Count > 0)
        {
            await EnsureSubscriptionsAsync(
                baseUrl,
                settings,
                subscriptionList,
                productIdMap,
                customerIdMap,
                taxRateLookup,
                progress,
                cancellationToken);
        }

        if (menuCollection is not null)
        {
            await EnsureMenusAsync(
                baseUrl,
                settings,
                menuCollection,
                pageIdMap,
                postIdMap,
                mediaResult,
                productIdMap,
                categoryIdMap,
                tagIdMap,
                progress,
                cancellationToken);
        }

        if (widgetSnapshot is not null)
        {
            await EnsureWidgetsAsync(baseUrl, settings, widgetSnapshot, progress, cancellationToken);
        }

        progress?.Report("Provisioning complete.");
    }

    public Task UploadPluginsAsync(
        WooProvisioningSettings settings,
        IEnumerable<ExtensionArtifact> bundles,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
        => UploadExtensionsAsync(settings, bundles, "plugin", progress, cancellationToken);

    public Task UploadThemesAsync(
        WooProvisioningSettings settings,
        IEnumerable<ExtensionArtifact> bundles,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
        => UploadExtensionsAsync(settings, bundles, "theme", progress, cancellationToken);

    private async Task UploadExtensionsAsync(
        WooProvisioningSettings settings,
        IEnumerable<ExtensionArtifact> bundles,
        string scope,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        if (bundles is null) throw new ArgumentNullException(nameof(bundles));

        var list = bundles.Where(b => b is not null).ToList();
        if (list.Count == 0)
        {
            progress?.Report($"No {scope} bundles to upload.");
            return;
        }

        foreach (var bundle in list)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await UploadExtensionAsync(settings, bundle, scope, progress, cancellationToken);
        }
    }

    private async Task UploadExtensionAsync(
        WooProvisioningSettings settings,
        ExtensionArtifact bundle,
        string scope,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(bundle.DirectoryPath))
        {
            progress?.Report($"Skipping {scope} '{bundle.Slug}': directory not found ({bundle.DirectoryPath}).");
            return;
        }

        var archivePath = File.Exists(bundle.ArchivePath) ? bundle.ArchivePath : null;
        var manifestPath = File.Exists(bundle.ManifestPath) ? bundle.ManifestPath : null;
        var optionsPath = File.Exists(bundle.OptionsPath) ? bundle.OptionsPath : null;

        if (archivePath is null && manifestPath is null && optionsPath is null)
        {
            progress?.Report($"Skipping {scope} '{bundle.Slug}': no bundle files found in {bundle.DirectoryPath}.");
            return;
        }

        var endpoints = scope == "plugin"
            ? new[] { "/wp-json/wc-scraper/v1/plugins/install", "/?rest_route=/wc-scraper/v1/plugins/install" }
            : new[] { "/wp-json/wc-scraper/v1/themes/install", "/?rest_route=/wc-scraper/v1/themes/install" };

        var uploaded = false;
        foreach (var endpoint in endpoints)
        {
            var success = await TryUploadBundleAsync(settings, bundle, scope, endpoint, archivePath, manifestPath, optionsPath, progress, cancellationToken);
            if (success)
            {
                uploaded = true;
                break;
            }
        }

        if (!uploaded)
        {
            progress?.Report($"No {scope} upload endpoint accepted '{bundle.Slug}'.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(optionsPath) && File.Exists(optionsPath))
        {
            await TryRestoreOptionsAsync(settings, scope, bundle.Slug, optionsPath, progress, cancellationToken);
        }
    }

    private async Task<bool> TryUploadBundleAsync(
        WooProvisioningSettings settings,
        ExtensionArtifact bundle,
        string scope,
        string path,
        string? archivePath,
        string? manifestPath,
        string? optionsPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var url = BuildUrl(settings.BaseUrl, path);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(bundle.Slug), "slug");

        if (!string.IsNullOrWhiteSpace(optionsPath) && File.Exists(optionsPath))
        {
            var json = await File.ReadAllTextAsync(optionsPath, Encoding.UTF8, cancellationToken);
            form.Add(new StringContent(json, Encoding.UTF8, "application/json"), "options");
        }

        if (!string.IsNullOrWhiteSpace(manifestPath) && File.Exists(manifestPath))
        {
            var json = await File.ReadAllTextAsync(manifestPath, Encoding.UTF8, cancellationToken);
            form.Add(new StringContent(json, Encoding.UTF8, "application/json"), "manifest");
        }

        StreamContent? archiveContent = null;
        if (!string.IsNullOrWhiteSpace(archivePath) && File.Exists(archivePath))
        {
            var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            archiveContent = new StreamContent(stream);
            archiveContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            form.Add(archiveContent, "archive", Path.GetFileName(archivePath));
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = CreateAuthHeader(settings);
            request.Content = form;

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                progress?.Report($"Uploaded {scope} bundle '{bundle.Slug}'.");
                return true;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            progress?.Report($"{scope} upload failed ({(int)response.StatusCode} {response.ReasonPhrase}): {body}");
            return false;
        }
        finally
        {
            archiveContent?.Dispose();
        }
    }

    private async Task TryRestoreOptionsAsync(
        WooProvisioningSettings settings,
        string scope,
        string slug,
        string optionsPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(optionsPath))
        {
            return;
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(optionsPath, Encoding.UTF8, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            progress?.Report($"Skipping {scope} options restore for '{slug}': {ex.Message}.");
            return;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        foreach (var endpoint in BuildOptionWriteEndpoints(scope, slug))
        {
            var url = BuildUrl(settings.BaseUrl, endpoint);
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = CreateAuthHeader(settings);
                request.Content = content;

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    progress?.Report($"Restored {scope} options for '{slug}'.");
                    return;
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                progress?.Report($"{scope} options restore failed for '{slug}' ({(int)response.StatusCode} {response.ReasonPhrase}): {body}");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                progress?.Report($"{scope} options restore timed out for '{slug}' via {endpoint}.");
            }
            catch (HttpRequestException ex)
            {
                progress?.Report($"{scope} options restore failed for '{slug}' via {endpoint}: {ex.Message}");
            }
        }

        progress?.Report($"No {scope} options endpoint accepted captured data for '{slug}'.");
    }

    private sealed class MediaProvisioningResult
    {
        public Dictionary<int, int> IdMap { get; } = new();
        public Dictionary<string, string> UrlMap { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> UrlToIdMap { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<MediaProvisioningResult> EnsureMediaLibraryAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        IReadOnlyList<WordPressMediaItem>? mediaItems,
        string? mediaRootDirectory,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var result = new MediaProvisioningResult();

        if (mediaItems is null || mediaItems.Count == 0)
        {
            return result;
        }

        if (!settings.HasWordPressCredentials)
        {
            progress?.Report("Skipping media library upload: missing WordPress credentials.");
            return result;
        }

        if (string.IsNullOrWhiteSpace(mediaRootDirectory))
        {
            progress?.Report("Skipping media library upload: media root directory not provided.");
            return result;
        }

        var fullMediaRoot = Path.GetFullPath(mediaRootDirectory);

        foreach (var item in mediaItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(item.SourceUrl))
            {
                continue;
            }

            var localPath = item.LocalFilePath;
            if (string.IsNullOrWhiteSpace(localPath) && !string.IsNullOrWhiteSpace(item.RelativeFilePath))
            {
                var relative = item.RelativeFilePath.Replace('/', Path.DirectorySeparatorChar);
                if (relative.StartsWith("media" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    relative = relative[("media" + Path.DirectorySeparatorChar).Length..];
                }
                localPath = Path.Combine(fullMediaRoot, relative);
            }

            if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
            {
                progress?.Report($"Skipping media {item.Id} ({item.SourceUrl}): local file not found.");
                continue;
            }

            var existing = await FindExistingMediaAsync(baseUrl, settings, item, progress, cancellationToken);
            WordPressMediaItem? ensured = existing;

            if (ensured is null)
            {
                ensured = await UploadMediaAsync(baseUrl, settings, item, localPath, progress, cancellationToken);
            }

            if (ensured is null)
            {
                continue;
            }

            if (item.Id > 0 && ensured.Id > 0)
            {
                result.IdMap[item.Id] = ensured.Id;
            }

            if (!string.IsNullOrWhiteSpace(item.SourceUrl) && !string.IsNullOrWhiteSpace(ensured.SourceUrl))
            {
                result.UrlMap[item.SourceUrl] = ensured.SourceUrl;
                result.UrlToIdMap[item.SourceUrl] = ensured.Id;
            }
        }

        return result;
    }

    private async Task<Dictionary<int, int>> EnsurePagesAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        IReadOnlyList<WordPressPage>? pages,
        MediaProvisioningResult? mediaResult,
        IReadOnlyDictionary<int, int>? authorIdMap,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<int, int>();
        if (pages is null || pages.Count == 0)
        {
            return map;
        }

        if (!settings.HasWordPressCredentials)
        {
            progress?.Report("Skipping WordPress pages: missing credentials.");
            return map;
        }

        var ordered = pages
            .OrderBy(p => p.ParentId.HasValue ? 1 : 0)
            .ThenBy(p => p.Id)
            .ToList();

        foreach (var page in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ensuredId = await EnsureContentItemAsync(
                baseUrl,
                settings,
                page,
                "pages",
                mediaResult,
                map,
                authorIdMap,
                progress,
                cancellationToken);
            if (page.Id > 0 && ensuredId > 0)
            {
                map[page.Id] = ensuredId;
            }
        }

        return map;
    }

    private async Task<Dictionary<int, int>> EnsurePostsAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        IReadOnlyList<WordPressPost>? posts,
        MediaProvisioningResult? mediaResult,
        IReadOnlyDictionary<int, int>? authorIdMap,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<int, int>();
        if (posts is null || posts.Count == 0)
        {
            return map;
        }

        if (!settings.HasWordPressCredentials)
        {
            progress?.Report("Skipping WordPress posts: missing credentials.");
            return map;
        }

        foreach (var post in posts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ensuredId = await EnsureContentItemAsync(
                baseUrl,
                settings,
                post,
                "posts",
                mediaResult,
                null,
                authorIdMap,
                progress,
                cancellationToken);
            if (post.Id > 0 && ensuredId > 0)
            {
                map[post.Id] = ensuredId;
            }
        }

        return map;
    }

    private async Task EnsureMenusAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        WordPressMenuCollection? menuCollection,
        IReadOnlyDictionary<int, int>? pageIdMap,
        IReadOnlyDictionary<int, int>? postIdMap,
        MediaProvisioningResult? mediaResult,
        IReadOnlyDictionary<int, int>? productIdMap,
        IReadOnlyDictionary<int, int>? categoryIdMap,
        IReadOnlyDictionary<int, int>? tagIdMap,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (menuCollection is null || menuCollection.Menus.Count == 0)
        {
            return;
        }

        if (!settings.HasWordPressCredentials)
        {
            progress?.Report("Skipping WordPress menus: missing credentials.");
            return;
        }

        var menuIdMap = new Dictionary<int, int>();

        foreach (var menu in menuCollection.Menus)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ensuredId = await EnsureMenuAsync(baseUrl, settings, menu, progress, cancellationToken);
            if (menu.Id > 0 && ensuredId > 0)
            {
                menuIdMap[menu.Id] = ensuredId;
            }

            await DeleteMenuItemsAsync(baseUrl, settings, ensuredId, progress, cancellationToken);

            if (menu.Items is { Count: > 0 })
            {
                var menuItemIdMap = new Dictionary<int, int>();
                var orderedItems = menu.Items.OrderBy(i => i.Order ?? 0).ToList();
                foreach (var item in orderedItems)
                {
                    await EnsureMenuItemRecursive(
                        baseUrl,
                        settings,
                        item,
                        ensuredId,
                        null,
                        pageIdMap,
                        postIdMap,
                        mediaResult,
                        productIdMap,
                        categoryIdMap,
                        tagIdMap,
                        menuItemIdMap,
                        progress,
                        cancellationToken);
                }
            }
        }

        if (menuCollection.Locations.Count > 0)
        {
            await EnsureMenuLocationsAsync(baseUrl, settings, menuCollection.Locations, menuIdMap, progress, cancellationToken);
        }
    }

    private async Task EnsureWidgetsAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        WordPressWidgetSnapshot? widgets,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (widgets is null || (widgets.Widgets.Count == 0 && widgets.Areas.Count == 0))
        {
            return;
        }

        if (!settings.HasWordPressCredentials)
        {
            progress?.Report("Skipping WordPress widgets: missing credentials.");
            return;
        }

        await ClearWidgetsAsync(baseUrl, settings, progress, cancellationToken);

        foreach (var widget in widgets.Widgets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CreateWidgetAsync(baseUrl, settings, widget, progress, cancellationToken);
        }
    }

    private async Task<int> EnsureContentItemAsync<T>(
        string baseUrl,
        WooProvisioningSettings settings,
        T item,
        string resource,
        MediaProvisioningResult? mediaResult,
        IReadOnlyDictionary<int, int>? parentIdMap,
        IReadOnlyDictionary<int, int>? authorIdMap,
        IProgress<string>? progress,
        CancellationToken cancellationToken) where T : WordPressContentBase
    {
        if (item is null)
        {
            return 0;
        }

        var slug = string.IsNullOrWhiteSpace(item.Slug)
            ? CreateSlug(item.Title?.Rendered)
            : item.Slug!;

        var replacements = mediaResult?.UrlMap;
        var contentHtml = RewriteHtml(item.Content?.Rendered, replacements);
        var excerptHtml = RewriteHtml(item.Excerpt?.Rendered, replacements);

        var payload = new Dictionary<string, object?>
        {
            ["title"] = item.Title?.Rendered,
            ["content"] = contentHtml,
            ["status"] = item.Status ?? "publish",
            ["slug"] = slug,
            ["excerpt"] = excerptHtml,
            ["template"] = item.Template
        };

        if (item.Author.HasValue
            && authorIdMap is not null
            && authorIdMap.TryGetValue(item.Author.Value, out var mappedAuthor))
        {
            payload["author"] = mappedAuthor;
        }

        if (item.Date.HasValue)
        {
            payload["date"] = item.Date.Value.ToString("o");
        }
        if (item.DateGmt.HasValue)
        {
            payload["date_gmt"] = item.DateGmt.Value.ToString("o");
        }
        if (item.Modified.HasValue)
        {
            payload["modified"] = item.Modified.Value.ToString("o");
        }
        if (item.ModifiedGmt.HasValue)
        {
            payload["modified_gmt"] = item.ModifiedGmt.Value.ToString("o");
        }

        if (item.Meta is JsonElement metaElement)
        {
            payload["meta"] = metaElement;
        }

        if (item is WordPressPage page)
        {
            if (page.MenuOrder.HasValue)
            {
                payload["menu_order"] = page.MenuOrder.Value;
            }

            if (page.ParentId.HasValue && parentIdMap is not null && parentIdMap.TryGetValue(page.ParentId.Value, out var newParent))
            {
                payload["parent"] = newParent;
            }
        }

        if (item is WordPressPost post)
        {
            if (!string.IsNullOrWhiteSpace(post.CommentStatus))
            {
                payload["comment_status"] = post.CommentStatus;
            }
            if (!string.IsNullOrWhiteSpace(post.PingStatus))
            {
                payload["ping_status"] = post.PingStatus;
            }
            if (post.Sticky is not null)
            {
                payload["sticky"] = post.Sticky;
            }
        }

        if (item.FeaturedMediaId.HasValue && mediaResult is not null && mediaResult.IdMap.TryGetValue(item.FeaturedMediaId.Value, out var mappedFeatured))
        {
            payload["featured_media"] = mappedFeatured;
        }
        else if (!string.IsNullOrWhiteSpace(item.FeaturedMediaUrl) && mediaResult is not null && mediaResult.UrlToIdMap.TryGetValue(item.FeaturedMediaUrl, out var mappedFromUrl))
        {
            payload["featured_media"] = mappedFromUrl;
        }

        var existing = await FindExistingContentAsync(baseUrl, settings, resource, slug, progress, cancellationToken);
        var url = existing is not null
            ? $"{baseUrl}/wp-json/wp/v2/{resource}/{existing.Id}"
            : $"{baseUrl}/wp-json/wp/v2/{resource}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = CreateBasicAuthHeader(settings.WordPressUsername!, settings.WordPressApplicationPassword!);
        var json = JsonSerializer.Serialize(payload, _writeOptions);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                progress?.Report($"Failed to provision WordPress {resource.TrimEnd('s')} '{slug}': {(int)response.StatusCode} ({response.ReasonPhrase}).");
                return existing?.Id ?? 0;
            }

            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(responseText);
            if (doc.RootElement.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number)
            {
                return idElement.GetInt32();
            }
        }
        catch (Exception ex)
        {
            progress?.Report($"Failed to provision WordPress {resource.TrimEnd('s')} '{slug}': {ex.Message}");
        }

        return existing?.Id ?? 0;
    }

    private async Task<T?> FindExistingContentAsync<T>(
        string baseUrl,
        WooProvisioningSettings settings,
        string resource,
        string slug,
        IProgress<string>? progress,
        CancellationToken cancellationToken) where T : WordPressContentBase
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        var url = BuildUrl(baseUrl, $"/wp-json/wp/v2/{resource}?slug={Uri.EscapeDataString(slug)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = CreateBasicAuthHeader(settings.WordPressUsername!, settings.WordPressApplicationPassword!);

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var items = JsonSerializer.Deserialize<List<T>>(text, _readOptions);
            return items?.FirstOrDefault();
        }
        catch (Exception ex)
        {
            progress?.Report($"Failed to query existing WordPress {resource.TrimEnd('s')} '{slug}': {ex.Message}");
            return null;
        }
    }

    private static string CreateSlug(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return $"content-{Guid.NewGuid():N}";
        }

        var lower = input.ToLowerInvariant();
        var builder = new StringBuilder(lower.Length);
        var lastDash = false;

        foreach (var ch in lower)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastDash = false;
            }
            else if (!lastDash)
            {
                builder.Append('-');
                lastDash = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? $"content-{Guid.NewGuid():N}" : slug;
    }

    private static string? RewriteHtml(string? html, IDictionary<string, string>? replacements)
    {
        if (string.IsNullOrWhiteSpace(html) || replacements is null || replacements.Count == 0)
        {
            return html;
        }

        var result = html;
        foreach (var pair in replacements)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            {
                result = result.Replace(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase);
            }
        }

        return result;
    }

    private async Task<int> EnsureMenuAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        WordPressMenu menu,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (menu is null)
        {
            return 0;
        }

        var slug = string.IsNullOrWhiteSpace(menu.Slug) ? CreateSlug(menu.Name) : menu.Slug!;
        var existingId = await FindMenuIdBySlugAsync(baseUrl, settings, slug, cancellationToken);

        var payload = new Dictionary<string, object?>
        {
            ["name"] = string.IsNullOrWhiteSpace(menu.Name) ? slug : menu.Name,
            ["slug"] = slug,
            ["description"] = menu.Description,
            ["auto_add"] = menu.AutoAdd
        };

        var url = existingId > 0
            ? BuildUrl(baseUrl, $"/wp-json/wp/v2/menus/{existingId}")
            : BuildUrl(baseUrl, "/wp-json/wp/v2/menus");

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = CreateBasicAuthHeader(settings.WordPressUsername!, settings.WordPressApplicationPassword!);
        var json = JsonSerializer.Serialize(payload, _writeOptions);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                progress?.Report($"Failed to provision menu '{slug}': {(int)response.StatusCode} ({response.ReasonPhrase}).");
                return existingId;
            }

            if (existingId > 0)
            {
                return existingId;
            }

            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(responseText);
            if (doc.RootElement.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number)
            {
                return idElement.GetInt32();
            }
        }
        catch (Exception ex)
        {
            progress?.Report($"Failed to provision menu '{slug}': {ex.Message}");
        }

        return existingId;
    }

    private async Task<int> FindMenuIdBySlugAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        string slug,
        CancellationToken cancellationToken)
    {
        var url = BuildUrl(baseUrl, $"/wp-json/wp/v2/menus?slug={Uri.EscapeDataString(slug)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = CreateBasicAuthHeader(settings.WordPressUsername!, settings.WordPressApplicationPassword!);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return 0;
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var menus = JsonSerializer.Deserialize<List<WordPressMenu>>(text, _readOptions);
        return menus?.FirstOrDefault()?.Id ?? 0;
    }

    private async Task DeleteMenuItemsAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        int menuId,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (menuId <= 0)
        {
            return;
        }

        for (var page = 1; page <= 10; page++)
        {
            var url = BuildUrl(baseUrl, $"/wp-json/wp/v2/menu-items?menu={menuId}&per_page=100&page={page}");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = CreateBasicAuthHeader(settings.WordPressUsername!, settings.WordPressApplicationPassword!);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                break;
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            var items = JsonSerializer.Deserialize<List<WordPressMenuItem>>(text, _readOptions);
            if (items is null || items.Count == 0)
            {
                break;
            }

            foreach (var item in items)
            {
                if (item.Id <= 0)
                {
                    continue;
                }

                var deleteUrl = BuildUrl(baseUrl, $"/wp-json/wp/v2/menu-items/{item.Id}?force=true");
                using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, deleteUrl);
                deleteRequest.Headers.Authorization = CreateBasicAuthHeader(settings.WordPressUsername!, settings.WordPressApplicationPassword!);
                await _httpClient.SendAsync(deleteRequest, cancellationToken);
            }

            if (items.Count < 100)
            {
                break;
            }
        }
    }

    private async Task EnsureMenuItemRecursive(
        string baseUrl,
        WooProvisioningSettings settings,
        WordPressMenuItem item,
        int menuId,
        int? parentId,
        IReadOnlyDictionary<int, int>? pageIdMap,
        IReadOnlyDictionary<int, int>? postIdMap,
        MediaProvisioningResult? mediaResult,
        IReadOnlyDictionary<int, int>? productIdMap,
        IReadOnlyDictionary<int, int>? categoryIdMap,
        IReadOnlyDictionary<int, int>? tagIdMap,
        Dictionary<int, int> menuItemIdMap,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (item is null)
        {
            return;
        }

        var payload = new Dictionary<string, object?>
        {
            ["title"] = item.Title?.Rendered,
            ["menu"] = menuId,
            ["status"] = item.Status ?? "publish",
            ["description"] = item.Description,
            ["attr_title"] = item.AttrTitle,
            ["target"] = item.Target,
            ["type"] = item.Type,
            ["object"] = item.Object,
            ["url"] = RewriteMenuItemUrl(item.Url, mediaResult)
        };

        if (parentId.HasValue)
        {
            payload["parent"] = parentId.Value;
        }
        else if (item.ParentId.HasValue && menuItemIdMap.TryGetValue(item.ParentId.Value, out var mappedParent))
        {
            payload["parent"] = mappedParent;
        }

        if (item.ObjectId.HasValue && item.ObjectId.Value > 0)
        {
            var objectType = item.Object;
            int? resolvedObjectId = null;

            if (!string.IsNullOrWhiteSpace(objectType))
            {
                if (pageIdMap is not null
                    && objectType.Equals("page", StringComparison.OrdinalIgnoreCase)
                    && pageIdMap.TryGetValue(item.ObjectId.Value, out var mappedPage))
                {
                    resolvedObjectId = mappedPage;
                }
                else if (postIdMap is not null
                    && objectType.Equals("post", StringComparison.OrdinalIgnoreCase)
                    && postIdMap.TryGetValue(item.ObjectId.Value, out var mappedPost))
                {
                    resolvedObjectId = mappedPost;
                }
                else if (productIdMap is not null
                    && (objectType.Equals("product", StringComparison.OrdinalIgnoreCase)
                        || objectType.Equals("product_variation", StringComparison.OrdinalIgnoreCase))
                    && productIdMap.TryGetValue(item.ObjectId.Value, out var mappedProduct))
                {
                    resolvedObjectId = mappedProduct;
                }
                else if (categoryIdMap is not null
                    && objectType.Equals("product_cat", StringComparison.OrdinalIgnoreCase)
                    && categoryIdMap.TryGetValue(item.ObjectId.Value, out var mappedCategory))
                {
                    resolvedObjectId = mappedCategory;
                }
                else if (tagIdMap is not null
                    && objectType.Equals("product_tag", StringComparison.OrdinalIgnoreCase)
                    && tagIdMap.TryGetValue(item.ObjectId.Value, out var mappedTag))
                {
                    resolvedObjectId = mappedTag;
                }
            }

            payload["object_id"] = resolvedObjectId ?? item.ObjectId.Value;
        }

        if (item.Classes is { Count: > 0 })
        {
            payload["classes"] = item.Classes;
        }

        if (item.Relationship is { Count: > 0 })
        {
            payload["xfn"] = item.Relationship;
        }

        if (item.Meta is JsonElement metaElement)
        {
            payload["meta"] = metaElement;
        }

        var url = BuildUrl(baseUrl, "/wp-json/wp/v2/menu-items");
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = CreateBasicAuthHeader(settings.WordPressUsername!, settings.WordPressApplicationPassword!);
        var json = JsonSerializer.Serialize(payload, _writeOptions);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                progress?.Report($"Failed to create menu item '{item.Title?.Rendered ?? item.Url}': {(int)response.StatusCode} ({response.ReasonPhrase}).");
                return;
            }

            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(responseText);
            if (doc.RootElement.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number)
            {
                var newId = idElement.GetInt32();
                if (item.Id > 0)
                {
                    menuItemIdMap[item.Id] = newId;
                }

                if (item.Children is { Count: > 0 })
                {
                    foreach (var child in item.Children.OrderBy(i => i.Order ?? 0))
                    {
                        await EnsureMenuItemRecursive(
                            baseUrl,
                            settings,
                            child,
                            menuId,
                            newId,
                            pageIdMap,
                            postIdMap,
                            mediaResult,
                            productIdMap,
                            categoryIdMap,
                            tagIdMap,
                            menuItemIdMap,
                            progress,
                            cancellationToken);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            progress?.Report($"Failed to create menu item '{item.Title?.Rendered ?? item.Url}': {ex.Message}");
        }
    }

    private static string? RewriteMenuItemUrl(string? url, MediaProvisioningResult? mediaResult)
    {
        if (string.IsNullOrWhiteSpace(url) || mediaResult is null || mediaResult.UrlMap.Count == 0)
        {
            return url;
        }

        var result = url;
        foreach (var pair in mediaResult.UrlMap)
        {
            result = result.Replace(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private async Task EnsureMenuLocationsAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        IReadOnlyList<WordPressMenuLocation> locations,
        IReadOnlyDictionary<int, int> menuIdMap,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var location in locations)
        {
            if (location.MenuId.HasValue
                && menuIdMap.TryGetValue(location.MenuId.Value, out var mappedMenu)
                && !string.IsNullOrWhiteSpace(location.Slug))
            {
                await AssignMenuToLocationAsync(baseUrl, settings, location.Slug!, mappedMenu, progress, cancellationToken);
            }
        }
    }

    private async Task AssignMenuToLocationAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        string locationSlug,
        int menuId,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var url = BuildUrl(baseUrl, $"/wp-json/wp/v2/menu-locations/{locationSlug}");
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = CreateBasicAuthHeader(settings.WordPressUsername!, settings.WordPressApplicationPassword!);
        var payload = new Dictionary<string, object?> { ["menu"] = menuId };
        var json = JsonSerializer.Serialize(payload, _writeOptions);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                progress?.Report($"Failed to assign menu {menuId} to location '{locationSlug}': {(int)response.StatusCode} ({response.ReasonPhrase}).");
            }
        }
        catch (Exception ex)
        {
            progress?.Report($"Failed to assign menu {menuId} to location '{locationSlug}': {ex.Message}");
        }
    }

    private async Task ClearWidgetsAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        for (var page = 1; page <= 10; page++)
        {
            var url = BuildUrl(baseUrl, $"/wp-json/wp/v2/widgets?per_page=100&page={page}");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = CreateBasicAuthHeader(settings.WordPressUsername!, settings.WordPressApplicationPassword!);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                break;
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            var items = JsonSerializer.Deserialize<List<WordPressWidget>>(text, _readOptions);
            if (items is null || items.Count == 0)
            {
                break;
            }

            foreach (var widget in items)
            {
                if (string.IsNullOrWhiteSpace(widget.Id))
                {
                    continue;
                }

                var deleteUrl = BuildUrl(baseUrl, $"/wp-json/wp/v2/widgets/{widget.Id}?force=true");
                using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, deleteUrl);
                deleteRequest.Headers.Authorization = CreateBasicAuthHeader(settings.WordPressUsername!, settings.WordPressApplicationPassword!);
                await _httpClient.SendAsync(deleteRequest, cancellationToken);
            }

            if (items.Count < 100)
            {
                break;
            }
        }
    }

    private async Task CreateWidgetAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        WordPressWidget widget,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["id_base"] = widget.IdBase,
            ["sidebar"] = widget.Sidebar,
            ["title"] = widget.Title,
            ["status"] = widget.Status ?? "publish"
        };

        if (widget.Instance is JsonElement instanceElement)
        {
            payload["instance"] = instanceElement;
        }

        var url = BuildUrl(baseUrl, "/wp-json/wp/v2/widgets");
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = CreateBasicAuthHeader(settings.WordPressUsername!, settings.WordPressApplicationPassword!);
        var json = JsonSerializer.Serialize(payload, _writeOptions);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                progress?.Report($"Failed to create widget '{widget.IdBase}': {(int)response.StatusCode} ({response.ReasonPhrase}).");
            }
        }
        catch (Exception ex)
        {
            progress?.Report($"Failed to create widget '{widget.IdBase}': {ex.Message}");
        }
    }

    private async Task<WordPressMediaItem?> FindExistingMediaAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        WordPressMediaItem item,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var queries = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Slug))
        {
            queries.Add($"slug={Uri.EscapeDataString(item.Slug)}");
        }

        var fileName = item.GuessFileName();
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            queries.Add($"search={Uri.EscapeDataString(fileName)}");
        }

        foreach (var query in queries.Distinct())
        {
            var url = BuildUrl(baseUrl, $"/wp-json/wp/v2/media?per_page=1&{query}");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = CreateBasicAuthHeader(settings.WordPressUsername!, settings.WordPressApplicationPassword!);

            try
            {
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                var matches = JsonSerializer.Deserialize<List<WordPressMediaItem>>(text, _readOptions);
                if (matches is { Count: > 0 })
                {
                    return matches.First();
                }
            }
            catch (Exception ex)
            {
                progress?.Report($"Failed to query existing media '{item.SourceUrl}': {ex.Message}");
            }
        }

        return null;
    }

    private async Task<WordPressMediaItem?> UploadMediaAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        WordPressMediaItem item,
        string localPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(localPath);
            using var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue(item.MimeType ?? "application/octet-stream");
            var fileName = Path.GetFileName(localPath);
            content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileNameStar = fileName
            };

            var url = BuildUrl(baseUrl, "/wp-json/wp/v2/media");
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = CreateMediaAuthHeader(settings);
            if (!string.IsNullOrWhiteSpace(item.Slug))
            {
                request.Headers.Add("Slug", item.Slug);
            }

            request.Content = content;

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                progress?.Report($"Failed to upload media {item.SourceUrl}: {(int)response.StatusCode} ({response.ReasonPhrase}).");
                return null;
            }

            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            var created = JsonSerializer.Deserialize<WordPressMediaItem>(responseText, _readOptions);
            if (created is null)
            {
                return null;
            }

            await UpdateMediaMetadataAsync(baseUrl, settings, created.Id, item, cancellationToken);

            return created;
        }
        catch (Exception ex)
        {
            progress?.Report($"Failed to upload media {item.SourceUrl}: {ex.Message}");
            return null;
        }
    }

    private async Task UpdateMediaMetadataAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        int mediaId,
        WordPressMediaItem source,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["title"] = source.Title?.Rendered,
            ["caption"] = source.Caption?.Rendered,
            ["alt_text"] = source.AltText,
            ["description"] = source.Description?.Rendered,
            ["slug"] = source.Slug
        };

        if (payload.Values.All(value => value is null))
        {
            return;
        }

        var url = BuildUrl(baseUrl, $"/wp-json/wp/v2/media/{mediaId}");
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = CreateBasicAuthHeader(settings.WordPressUsername!, settings.WordPressApplicationPassword!);
        var json = JsonSerializer.Serialize(payload, _writeOptions);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        try
        {
            await _httpClient.SendAsync(request, cancellationToken);
        }
        catch
        {
            // Ignore metadata update failures
        }
    }

    private async Task<int> EnsureProductAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        StoreProduct product,
        IReadOnlyDictionary<string, int> categoryMap,
        IReadOnlyDictionary<string, int> tagMap,
        IReadOnlyDictionary<string, int> attributeMap,
        Dictionary<string, int> mediaCache,
        IReadOnlyList<StoreProduct>? childVariations,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var existing = await FindExistingProductAsync(baseUrl, settings, product, cancellationToken);

        var categoryRefs = BuildTaxonomyReferences(product.Categories, categoryMap);
        var tagRefs = BuildTaxonomyReferences(product.Tags, tagMap);
        HashSet<int>? variationAttributeIds = null;
        if (childVariations is { Count: > 0 })
        {
            foreach (var variation in childVariations)
            {
                if (variation?.Attributes is null || variation.Attributes.Count == 0)
                {
                    continue;
                }

                foreach (var attribute in variation.Attributes)
                {
                    if (attribute is null)
                    {
                        continue;
                    }

                    var key = NormalizeAttributeKey(attribute);
                    if (key is null || !attributeMap.TryGetValue(key, out var attributeId))
                    {
                        continue;
                    }

                    variationAttributeIds ??= new HashSet<int>();
                    variationAttributeIds.Add(attributeId);
                }
            }
        }

        var attributePayload = BuildAttributePayload(product, attributeMap);
        if (variationAttributeIds is { Count: > 0 })
        {
            for (var index = 0; index < attributePayload.Count; index++)
            {
                var attribute = attributePayload[index];
                if (!attribute.TryGetValue("id", out var idValue) || idValue is not int attributeId)
                {
                    continue;
                }

                if (!variationAttributeIds.Contains(attributeId))
                {
                    continue;
                }

                attribute["variation"] = true;
                if (!attribute.ContainsKey("visible"))
                {
                    attribute["visible"] = true;
                }

                if (!attribute.ContainsKey("position"))
                {
                    attribute["position"] = index;
                }
            }
        }
        var imagePayload = await BuildImagePayloadAsync(baseUrl, settings, product, mediaCache, progress, cancellationToken);

        var payload = new Dictionary<string, object?>();
        AddIfValue(payload, "name", product.Name);
        AddIfValue(payload, "slug", NormalizeSlug(product.Slug));
        var hasChildVariations = childVariations is { Count: > 0 };
        if (hasChildVariations)
        {
            payload["type"] = "variable";
        }
        else
        {
            AddIfValue(payload, "type", string.IsNullOrWhiteSpace(product.Type) ? "simple" : product.Type);
        }
        AddIfValue(payload, "sku", string.IsNullOrWhiteSpace(product.Sku) ? null : product.Sku);
        AddIfValue(payload, "status", "publish");
        AddIfValue(payload, "regular_price", ResolvePrice(product.Prices));
        AddIfValue(payload, "sale_price", ResolveSalePrice(product.Prices));
        AddIfValue(payload, "description", product.Description);
        AddIfValue(payload, "short_description", product.ShortDescription);
        AddIfValue(payload, "stock_status", ResolveStockStatus(product));

        if (categoryRefs.Count > 0)
        {
            payload["categories"] = categoryRefs;
        }

        if (tagRefs.Count > 0)
        {
            payload["tags"] = tagRefs;
        }

        if (attributePayload.Count > 0)
        {
            payload["attributes"] = attributePayload;
        }

        if (imagePayload.Count > 0)
        {
            payload["images"] = imagePayload;
        }

        if (existing is not null)
        {
            progress?.Report($"Updating product '{product.Name ?? product.Slug ?? product.Id.ToString()}' (ID {existing.Id}).");
            var updated = await PutAsync<WooProductSummary>(baseUrl, settings, $"/wp-json/wc/v3/products/{existing.Id}", payload, cancellationToken);
            return updated.Id;
        }

        progress?.Report($"Creating product '{product.Name ?? product.Slug ?? product.Id.ToString()}'.");
        var created = await PostAsync<WooProductSummary>(baseUrl, settings, "/wp-json/wc/v3/products", payload, cancellationToken);
        return created.Id;
    }

    private async Task EnsureVariationsAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        IReadOnlyDictionary<int, StoreProduct> productLookup,
        List<StoreProduct> variations,
        List<ProvisioningVariableProduct> variableProducts,
        IReadOnlyDictionary<string, int> attributeMap,
        Dictionary<string, int> mediaCache,
        Dictionary<int, int> productIdMap,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var grouped = BuildVariationGroups(productLookup, variableProducts, variations);

        foreach (var group in grouped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!productIdMap.TryGetValue(group.ParentId, out var mappedParentId))
            {
                progress?.Report($"Skipping {group.Variations.Count} variations for parent {group.ParentId}: parent was not provisioned.");
                continue;
            }

            var parentProduct = group.Parent ?? (productLookup.TryGetValue(group.ParentId, out var existing) ? existing : null);
            var parentLabel = BuildParentLabel(parentProduct, group.ParentId);
            progress?.Report($"Provisioning {group.Variations.Count} variations for '{parentLabel}' (ID {mappedParentId}).");

            var existing = await FetchExistingVariationsAsync(baseUrl, settings, mappedParentId, cancellationToken);
            var existingBySku = new Dictionary<string, WooVariationResponse>(StringComparer.OrdinalIgnoreCase);
            var existingByAttributes = new Dictionary<string, WooVariationResponse>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in existing)
            {
                if (!string.IsNullOrWhiteSpace(item.Sku))
                {
                    existingBySku[item.Sku!] = item;
                }

                var key = BuildVariationAttributeKey(item.Attributes);
                if (key is not null)
                {
                    existingByAttributes[key] = item;
                }
            }

            foreach (var variation in group.Variations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributePayload = BuildVariationAttributePayload(variation, attributeMap);
                var attributeKey = BuildVariationAttributeKey(attributePayload);
                WooVariationResponse? existingMatch = null;

                if (!string.IsNullOrWhiteSpace(variation.Sku) && existingBySku.TryGetValue(variation.Sku!, out var bySku))
                {
                    existingMatch = bySku;
                }
                else if (attributeKey is not null && existingByAttributes.TryGetValue(attributeKey, out var byAttributes))
                {
                    existingMatch = byAttributes;
                }

                var payload = await BuildVariationPayloadAsync(baseUrl, settings, variation, parentProduct, attributePayload, mediaCache, progress, cancellationToken);
                var label = BuildVariationLabel(variation, parentProduct);

                if (existingMatch is not null)
                {
                    progress?.Report($"Updating variation '{label}' (Parent {mappedParentId}, Variation {existingMatch.Id}).");
                    var updated = await PutAsync<WooVariationResponse>(
                        baseUrl,
                        settings,
                        $"/wp-json/wc/v3/products/{mappedParentId}/variations/{existingMatch.Id}",
                        payload,
                        cancellationToken);

                    if (!string.IsNullOrWhiteSpace(updated.Sku))
                    {
                        existingBySku[updated.Sku!] = updated;
                    }

                    var updatedKey = BuildVariationAttributeKey(updated.Attributes);
                    if (updatedKey is not null)
                    {
                        existingByAttributes[updatedKey] = updated;
                    }

                    if (variation.Id > 0)
                    {
                        productIdMap[variation.Id] = updated.Id;
                    }

                    continue;
                }

                progress?.Report($"Creating variation '{label}' (Parent {mappedParentId}).");
                var created = await PostAsync<WooVariationResponse>(
                    baseUrl,
                    settings,
                    $"/wp-json/wc/v3/products/{mappedParentId}/variations",
                    payload,
                    cancellationToken);

                if (!string.IsNullOrWhiteSpace(created.Sku))
                {
                    existingBySku[created.Sku!] = created;
                }

                var createdKey = BuildVariationAttributeKey(created.Attributes);
                if (createdKey is not null)
                {
                    existingByAttributes[createdKey] = created;
                }

                if (variation.Id > 0)
                {
                    productIdMap[variation.Id] = created.Id;
                }
            }
        }
    }

    private async Task<List<WooVariationResponse>> FetchExistingVariationsAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        int parentId,
        CancellationToken cancellationToken)
    {
        var result = new List<WooVariationResponse>();
        var page = 1;
        while (true)
        {
            var path = $"/wp-json/wc/v3/products/{parentId}/variations?per_page=100&page={page}";
            var list = await GetAsync<List<WooVariationResponse>>(baseUrl, settings, path, cancellationToken) ?? new List<WooVariationResponse>();
            if (list.Count == 0)
            {
                break;
            }

            result.AddRange(list);
            if (list.Count < 100)
            {
                break;
            }

            page++;
        }

        return result;
    }

    private static List<ProvisioningVariableProduct> SanitizeVariableProducts(IEnumerable<ProvisioningVariableProduct>? variableProducts)
    {
        var result = new List<ProvisioningVariableProduct>();
        if (variableProducts is null)
        {
            return result;
        }

        foreach (var group in variableProducts)
        {
            if (group is null || group.Parent is null)
            {
                continue;
            }

            var sanitized = group.Variations?
                .Where(v => v is not null)
                .Select(v => v!)
                .ToList() ?? new List<StoreProduct>();

            result.Add(new ProvisioningVariableProduct(group.Parent, sanitized));
        }

        return result;
    }

    private static List<VariationProvisionGroup> BuildVariationGroups(
        IReadOnlyDictionary<int, StoreProduct> productLookup,
        List<ProvisioningVariableProduct> variableProducts,
        List<StoreProduct> variations)
    {
        var result = new List<VariationProvisionGroup>();
        if (variableProducts.Count > 0)
        {
            foreach (var group in variableProducts)
            {
                var sanitized = group.Variations
                    .Where(v => v is not null && v.ParentId is int id && id > 0)
                    .Select(v => v!)
                    .ToList();

                if (sanitized.Count == 0)
                {
                    continue;
                }

                var parentId = group.Parent.Id > 0
                    ? group.Parent.Id
                    : sanitized[0].ParentId!.Value;

                result.Add(new VariationProvisionGroup(parentId, group.Parent, sanitized));
            }

            return result;
        }

        var grouped = variations
            .Where(v => v?.ParentId is int id && id > 0)
            .GroupBy(v => v.ParentId!.Value, v => v);

        foreach (var group in grouped)
        {
            var list = group
                .Where(v => v is not null)
                .Select(v => v!)
                .ToList();

            if (list.Count == 0)
            {
                continue;
            }

            productLookup.TryGetValue(group.Key, out var parent);
            result.Add(new VariationProvisionGroup(group.Key, parent, list));
        }

        return result;
    }

    private static string? BuildVariationIdentity(StoreProduct variation)
    {
        if (variation is null)
        {
            return null;
        }

        var parentId = variation.ParentId ?? 0;

        if (variation.Id > 0)
        {
            return $"id:{parentId}:{variation.Id}";
        }

        if (!string.IsNullOrWhiteSpace(variation.Sku))
        {
            return $"sku:{parentId}:{variation.Sku!.Trim()}";
        }

        if (parentId <= 0)
        {
            return null;
        }

        if (variation.Attributes is null || variation.Attributes.Count == 0)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var attribute in variation.Attributes)
        {
            var key = NormalizeAttributeKey(attribute);
            var value = ResolveAttributeValue(attribute);
            if (key is null || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            parts.Add($"{key}:{value.Trim()}");
        }

        return parts.Count == 0 ? null : $"attr:{parentId}:{string.Join('|', parts)}";
    }

    private async Task<Dictionary<string, object?>> BuildVariationPayloadAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        StoreProduct variation,
        StoreProduct? parent,
        List<Dictionary<string, object?>> attributePayload,
        Dictionary<string, int> mediaCache,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>();
        AddIfValue(payload, "sku", string.IsNullOrWhiteSpace(variation.Sku) ? null : variation.Sku);
        AddIfValue(payload, "description", variation.Description ?? variation.ShortDescription);
        AddIfValue(payload, "regular_price", ResolveVariationPrice(variation, parent));
        AddIfValue(payload, "sale_price", ResolveVariationSalePrice(variation, parent));
        AddIfValue(payload, "stock_status", ResolveStockStatus(variation));

        if (attributePayload.Count > 0)
        {
            payload["attributes"] = attributePayload;
        }

        var imagePayload = await BuildImagePayloadAsync(baseUrl, settings, variation, mediaCache, progress, cancellationToken);
        var firstImage = imagePayload.FirstOrDefault();
        if (firstImage is not null)
        {
            payload["image"] = firstImage;
        }

        return payload;
    }

    private sealed class VariationProvisionGroup
    {
        public VariationProvisionGroup(int parentId, StoreProduct? parent, List<StoreProduct> variations)
        {
            ParentId = parentId;
            Parent = parent;
            Variations = variations;
        }

        public int ParentId { get; }

        public StoreProduct? Parent { get; }

        public List<StoreProduct> Variations { get; }
    }

    private static string? ResolveVariationPrice(StoreProduct variation, StoreProduct? parent)
    {
        return ResolvePrice(variation.Prices) ?? ResolvePrice(parent?.Prices);
    }

    private static string? ResolveVariationSalePrice(StoreProduct variation, StoreProduct? parent)
    {
        return ResolveSalePrice(variation.Prices) ?? ResolveSalePrice(parent?.Prices);
    }

    private static List<Dictionary<string, object?>> BuildVariationAttributePayload(StoreProduct variation, IReadOnlyDictionary<string, int> attributeMap)
    {
        var payload = new List<Dictionary<string, object?>>();
        if (variation.Attributes is null || variation.Attributes.Count == 0)
        {
            return payload;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in variation.Attributes)
        {
            var key = NormalizeAttributeKey(attribute);
            if (key is null || !attributeMap.TryGetValue(key, out var attributeId))
            {
                continue;
            }

            var option = ResolveAttributeValue(attribute);
            if (string.IsNullOrWhiteSpace(option))
            {
                continue;
            }

            var normalized = option.Trim();
            if (!seen.Add($"{attributeId}:{normalized}"))
            {
                continue;
            }

            payload.Add(new Dictionary<string, object?>
            {
                ["id"] = attributeId,
                ["option"] = normalized
            });
        }

        return payload;
    }

    private static string? BuildVariationAttributeKey(IEnumerable<Dictionary<string, object?>> attributes)
    {
        if (attributes is null)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var attribute in attributes)
        {
            if (!attribute.TryGetValue("id", out var idValue) || !attribute.TryGetValue("option", out var optionValue))
            {
                continue;
            }

            if (idValue is not int id)
            {
                continue;
            }

            if (optionValue is not string option || string.IsNullOrWhiteSpace(option))
            {
                continue;
            }

            parts.Add($"{id}:{option.Trim().ToLowerInvariant()}");
        }

        if (parts.Count == 0)
        {
            return null;
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join("|", parts);
    }

    private static string? BuildVariationAttributeKey(IEnumerable<WooVariationAttributeResponse>? attributes)
    {
        if (attributes is null)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var attribute in attributes)
        {
            if (attribute is null || attribute.Id <= 0 || string.IsNullOrWhiteSpace(attribute.Option))
            {
                continue;
            }

            parts.Add($"{attribute.Id}:{attribute.Option!.Trim().ToLowerInvariant()}");
        }

        if (parts.Count == 0)
        {
            return null;
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join("|", parts);
    }

    private static string BuildVariationLabel(StoreProduct variation, StoreProduct? parent)
    {
        if (!string.IsNullOrWhiteSpace(variation.Sku))
        {
            return variation.Sku!;
        }

        if (!string.IsNullOrWhiteSpace(variation.Name))
        {
            return variation.Name!;
        }

        if (!string.IsNullOrWhiteSpace(variation.Slug))
        {
            return variation.Slug!;
        }

        if (variation.Id > 0)
        {
            return variation.Id.ToString(CultureInfo.InvariantCulture);
        }

        if (parent is not null && parent.Id > 0)
        {
            return $"{parent.Id}-variation";
        }

        return "variation";
    }

    private static string BuildParentLabel(StoreProduct? parent, int parentId)
    {
        if (parent is not null)
        {
            if (!string.IsNullOrWhiteSpace(parent.Name))
            {
                return parent.Name!;
            }

            if (!string.IsNullOrWhiteSpace(parent.Slug))
            {
                return parent.Slug!;
            }
        }

        return parentId.ToString(CultureInfo.InvariantCulture);
    }

    private async Task EnsureCustomersAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        List<WooCustomer> customers,
        Dictionary<int, int> customerMap,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var customer in customers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identifier = GetCustomerIdentifier(customer);
            var payload = BuildCustomerPayload(customer);
            if (payload.Count == 0)
            {
                progress?.Report($"Skipping customer '{identifier}': no importable fields.");
                continue;
            }

            var existing = await FindCustomerAsync(baseUrl, settings, customer, cancellationToken);
            if (existing is not null)
            {
                progress?.Report($"Updating customer '{identifier}' (ID {existing.Id}).");
                var updated = await PutAsync<WooCustomer>(baseUrl, settings, $"/wp-json/wc/v3/customers/{existing.Id}", payload, cancellationToken);
                if (customer.Id > 0)
                {
                    customerMap[customer.Id] = updated.Id;
                }
                continue;
            }

            progress?.Report($"Creating customer '{identifier}'.");
            try
            {
                var created = await PostAsync<WooCustomer>(baseUrl, settings, "/wp-json/wc/v3/customers", payload, cancellationToken);
                if (customer.Id > 0)
                {
                    customerMap[customer.Id] = created.Id;
                }
            }
            catch (WooProvisioningException ex) when (ex.StatusCode == HttpStatusCode.BadRequest || ex.StatusCode == HttpStatusCode.Conflict)
            {
                progress?.Report($"Customer '{identifier}' may already exist ({(int)ex.StatusCode}). Retrying fetch.");
                existing = await FindCustomerAsync(baseUrl, settings, customer, cancellationToken);
                if (existing is not null)
                {
                    if (customer.Id > 0)
                    {
                        customerMap[customer.Id] = existing.Id;
                    }
                    await PutAsync<WooCustomer>(baseUrl, settings, $"/wp-json/wc/v3/customers/{existing.Id}", payload, cancellationToken);
                }
                else
                {
                    throw;
                }
            }
        }
    }

    private async Task<WooCustomer?> FindCustomerAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        WooCustomer customer,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(customer.Email))
        {
            var list = await GetAsync<List<WooCustomer>>(baseUrl, settings, $"/wp-json/wc/v3/customers?per_page=1&email={Uri.EscapeDataString(customer.Email)}", cancellationToken);
            if (list is { Count: > 0 })
            {
                return list[0];
            }
        }

        if (!string.IsNullOrWhiteSpace(customer.Username))
        {
            var list = await GetAsync<List<WooCustomer>>(baseUrl, settings, $"/wp-json/wc/v3/customers?per_page=1&search={Uri.EscapeDataString(customer.Username)}", cancellationToken);
            if (list is { Count: > 0 })
            {
                return list.FirstOrDefault(c => string.Equals(c.Username, customer.Username, StringComparison.OrdinalIgnoreCase));
            }
        }

        return null;
    }

    private static string GetCustomerIdentifier(WooCustomer customer)
    {
        if (!string.IsNullOrWhiteSpace(customer.Email))
        {
            return customer.Email!;
        }

        if (!string.IsNullOrWhiteSpace(customer.Username))
        {
            return customer.Username!;
        }

        return customer.Id > 0 ? customer.Id.ToString(CultureInfo.InvariantCulture) : "customer";
    }

    private static Dictionary<string, object?> BuildCustomerPayload(WooCustomer customer)
    {
        var payload = new Dictionary<string, object?>();
        AddIfValue(payload, "email", customer.Email);
        AddIfValue(payload, "first_name", customer.FirstName);
        AddIfValue(payload, "last_name", customer.LastName);
        AddIfValue(payload, "username", customer.Username);
        if (customer.Billing is not null)
        {
            payload["billing"] = customer.Billing;
        }
        if (customer.Shipping is not null)
        {
            payload["shipping"] = customer.Shipping;
        }

        var meta = BuildMetaDataPayload(customer.MetaData);
        if (meta.Count > 0)
        {
            payload["meta_data"] = meta;
        }

        return payload;
    }

    private async Task EnsureCouponsAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        List<WooCoupon> coupons,
        IReadOnlyDictionary<int, int> productMap,
        IReadOnlyDictionary<int, int> categoryMap,
        Dictionary<int, int> couponMap,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var coupon in coupons)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identifier = GetCouponIdentifier(coupon);
            var payload = BuildCouponPayload(coupon, productMap, categoryMap);
            if (payload.Count == 0)
            {
                progress?.Report($"Skipping coupon '{identifier}': no importable fields.");
                continue;
            }

            var existing = await FindCouponAsync(baseUrl, settings, coupon, cancellationToken);
            if (existing is not null)
            {
                progress?.Report($"Updating coupon '{identifier}' (ID {existing.Id}).");
                var updated = await PutAsync<WooCoupon>(baseUrl, settings, $"/wp-json/wc/v3/coupons/{existing.Id}", payload, cancellationToken);
                if (coupon.Id > 0)
                {
                    couponMap[coupon.Id] = updated.Id;
                }
                continue;
            }

            progress?.Report($"Creating coupon '{identifier}'.");
            try
            {
                var created = await PostAsync<WooCoupon>(baseUrl, settings, "/wp-json/wc/v3/coupons", payload, cancellationToken);
                if (coupon.Id > 0)
                {
                    couponMap[coupon.Id] = created.Id;
                }
            }
            catch (WooProvisioningException ex) when (ex.StatusCode == HttpStatusCode.BadRequest || ex.StatusCode == HttpStatusCode.Conflict)
            {
                progress?.Report($"Coupon '{identifier}' may already exist ({(int)ex.StatusCode}). Retrying fetch.");
                existing = await FindCouponAsync(baseUrl, settings, coupon, cancellationToken);
                if (existing is not null)
                {
                    if (coupon.Id > 0)
                    {
                        couponMap[coupon.Id] = existing.Id;
                    }
                    await PutAsync<WooCoupon>(baseUrl, settings, $"/wp-json/wc/v3/coupons/{existing.Id}", payload, cancellationToken);
                }
                else
                {
                    throw;
                }
            }
        }
    }

    private async Task<WooCoupon?> FindCouponAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        WooCoupon coupon,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(coupon.Code))
        {
            return null;
        }

        var list = await GetAsync<List<WooCoupon>>(baseUrl, settings, $"/wp-json/wc/v3/coupons?per_page=1&code={Uri.EscapeDataString(coupon.Code)}", cancellationToken);
        if (list is { Count: > 0 })
        {
            return list[0];
        }

        return null;
    }

    private static string GetCouponIdentifier(WooCoupon coupon)
        => !string.IsNullOrWhiteSpace(coupon.Code)
            ? coupon.Code!
            : (coupon.Id > 0 ? coupon.Id.ToString(CultureInfo.InvariantCulture) : "coupon");

    private static Dictionary<string, object?> BuildCouponPayload(
        WooCoupon coupon,
        IReadOnlyDictionary<int, int> productMap,
        IReadOnlyDictionary<int, int> categoryMap)
    {
        var payload = new Dictionary<string, object?>();
        AddIfValue(payload, "code", coupon.Code);
        AddIfValue(payload, "amount", coupon.Amount);
        AddIfValue(payload, "discount_type", coupon.DiscountType);
        AddIfValue(payload, "description", coupon.Description);
        AddIfValue(payload, "individual_use", coupon.IndividualUse);
        AddIfValue(payload, "usage_limit", coupon.UsageLimit);
        AddIfValue(payload, "usage_limit_per_user", coupon.UsageLimitPerUser);
        AddIfValue(payload, "limit_usage_to_x_items", coupon.LimitUsageToItems);
        AddIfValue(payload, "free_shipping", coupon.FreeShipping);
        AddIfValue(payload, "exclude_sale_items", coupon.ExcludeSaleItems);
        AddIfValue(payload, "minimum_amount", coupon.MinimumAmount);
        AddIfValue(payload, "maximum_amount", coupon.MaximumAmount);
        AddIfValue(payload, "date_expires", coupon.DateExpires);
        AddIfValue(payload, "date_expires_gmt", coupon.DateExpiresGmt);

        var productIds = MapIds(coupon.ProductIds, productMap);
        if (productIds.Count > 0)
        {
            payload["product_ids"] = productIds;
        }

        var excluded = MapIds(coupon.ExcludedProductIds, productMap);
        if (excluded.Count > 0)
        {
            payload["excluded_product_ids"] = excluded;
        }

        var productCategories = MapIds(coupon.ProductCategories, categoryMap);
        if (productCategories.Count > 0)
        {
            payload["product_categories"] = productCategories;
        }

        var excludedCategories = MapIds(coupon.ExcludedProductCategories, categoryMap);
        if (excludedCategories.Count > 0)
        {
            payload["excluded_product_categories"] = excludedCategories;
        }

        if (coupon.EmailRestrictions.Count > 0)
        {
            payload["email_restrictions"] = coupon.EmailRestrictions;
        }

        var meta = BuildMetaDataPayload(coupon.MetaData);
        if (meta.Count > 0)
        {
            payload["meta_data"] = meta;
        }

        return payload;
    }

    private async Task<TaxRateLookup> BuildTaxRateLookupAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        List<WooOrder> orders,
        List<WooSubscription> subscriptions,
        CancellationToken cancellationToken)
    {
        var requiredCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requiredIds = new HashSet<int>();

        static void Collect(IEnumerable<WooOrderTaxLine> lines, HashSet<string> codes, HashSet<int> ids)
        {
            foreach (var line in lines ?? Enumerable.Empty<WooOrderTaxLine>())
            {
                if (line is null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(line.RateCode))
                {
                    codes.Add(line.RateCode);
                }

                if (line.RateId is int rateId && rateId > 0)
                {
                    ids.Add(rateId);
                }
            }
        }

        foreach (var order in orders)
        {
            if (order is null)
            {
                continue;
            }

            Collect(order.TaxLines, requiredCodes, requiredIds);
        }

        foreach (var subscription in subscriptions)
        {
            if (subscription is null)
            {
                continue;
            }

            Collect(subscription.TaxLines, requiredCodes, requiredIds);
        }

        if (requiredCodes.Count == 0 && requiredIds.Count == 0)
        {
            return TaxRateLookup.Empty;
        }

        var mappedByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var mappedById = new Dictionary<int, int>();
        const int pageSize = 100;
        var page = 1;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = $"/wp-json/wc/v3/taxes?per_page={pageSize}&page={page}";
            var pageRates = await GetAsync<List<WooTaxRate>>(baseUrl, settings, path, cancellationToken);
            if (pageRates is null || pageRates.Count == 0)
            {
                break;
            }

            foreach (var rate in pageRates)
            {
                if (rate is null)
                {
                    continue;
                }

                foreach (var rateCode in rate.GetCandidateRateCodes())
                {
                    if (string.IsNullOrWhiteSpace(rateCode)
                        || !requiredCodes.Contains(rateCode)
                        || mappedByCode.ContainsKey(rateCode))
                    {
                        continue;
                    }

                    mappedByCode[rateCode] = rate.Id;
                }

                if (requiredIds.Contains(rate.Id) && !mappedById.ContainsKey(rate.Id))
                {
                    mappedById[rate.Id] = rate.Id;
                }
            }

            if (pageRates.Count < pageSize)
            {
                break;
            }

            page++;
        }

        if (mappedByCode.Count == 0 && mappedById.Count == 0)
        {
            return TaxRateLookup.Empty;
        }

        if (mappedByCode.Count > 0 && requiredIds.Count > 0)
        {
            IEnumerable<WooOrderTaxLine> EnumerateLines()
            {
                foreach (var order in orders)
                {
                    if (order?.TaxLines is null)
                    {
                        continue;
                    }

                    foreach (var line in order.TaxLines)
                    {
                        if (line is not null)
                        {
                            yield return line;
                        }
                    }
                }

                foreach (var subscription in subscriptions)
                {
                    if (subscription?.TaxLines is null)
                    {
                        continue;
                    }

                    foreach (var line in subscription.TaxLines)
                    {
                        if (line is not null)
                        {
                            yield return line;
                        }
                    }
                }
            }

            foreach (var line in EnumerateLines())
            {
                if (line.RateId is int sourceId
                    && !mappedById.ContainsKey(sourceId)
                    && !string.IsNullOrWhiteSpace(line.RateCode)
                    && mappedByCode.TryGetValue(line.RateCode, out var mappedId))
                {
                    mappedById[sourceId] = mappedId;
                }
            }
        }

        return new TaxRateLookup(mappedByCode, mappedById);
    }

    private async Task EnsureOrdersAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        List<WooOrder> orders,
        IReadOnlyDictionary<int, int> productMap,
        IReadOnlyDictionary<int, int> customerMap,
        IReadOnlyDictionary<string, int> couponCodeMap,
        TaxRateLookup taxRateLookup,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var order in orders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identifier = GetOrderIdentifier(order);
            var lineItems = BuildOrderLineItems(order.LineItems, productMap);
            if (lineItems.Count == 0)
            {
                progress?.Report($"Skipping order '{identifier}': no line items could be mapped.");
                continue;
            }

            var payload = BuildOrderPayload(order, productMap, customerMap, couponCodeMap, lineItems, taxRateLookup);
            progress?.Report($"Creating order '{identifier}'.");
            try
            {
                await PostAsync<WooOrder>(baseUrl, settings, "/wp-json/wc/v3/orders", payload, cancellationToken);
            }
            catch (WooProvisioningException ex) when (ex.StatusCode == HttpStatusCode.BadRequest || ex.StatusCode == HttpStatusCode.Conflict)
            {
                progress?.Report($"Order '{identifier}' may already exist ({(int)ex.StatusCode}). Retrying fetch.");
                var existing = await FindOrderAsync(baseUrl, settings, order, cancellationToken);
                if (existing is not null)
                {
                    progress?.Report($"Order '{identifier}' already present as ID {existing.Id}. Skipping creation.");
                }
                else
                {
                    throw;
                }
            }
        }
    }

    private async Task EnsureSubscriptionsAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        List<WooSubscription> subscriptions,
        IReadOnlyDictionary<int, int> productMap,
        IReadOnlyDictionary<int, int> customerMap,
        TaxRateLookup taxRateLookup,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var endpointUnavailable = false;
        foreach (var subscription in subscriptions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (endpointUnavailable)
            {
                progress?.Report($"Skipping subscription '{GetSubscriptionIdentifier(subscription)}': endpoint unavailable.");
                continue;
            }

            var identifier = GetSubscriptionIdentifier(subscription);
            var lineItems = BuildOrderLineItems(subscription.LineItems, productMap);
            if (lineItems.Count == 0)
            {
                progress?.Report($"Skipping subscription '{identifier}': no line items could be mapped.");
                continue;
            }

            var payload = BuildSubscriptionPayload(subscription, customerMap, lineItems, taxRateLookup);
            if (payload.Count == 0)
            {
                progress?.Report($"Skipping subscription '{identifier}': no importable fields.");
                continue;
            }

            progress?.Report($"Creating subscription '{identifier}'.");
            try
            {
                await PostAsync<WooSubscription>(baseUrl, settings, "/wp-json/wc/v1/subscriptions", payload, cancellationToken);
            }
            catch (WooProvisioningException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                progress?.Report("Subscriptions endpoint not available (404). Skipping remaining subscriptions.");
                endpointUnavailable = true;
            }
            catch (WooProvisioningException ex) when (ex.StatusCode == HttpStatusCode.BadRequest || ex.StatusCode == HttpStatusCode.Conflict)
            {
                progress?.Report($"Subscription '{identifier}' may already exist ({(int)ex.StatusCode}). Retrying fetch.");
                var existing = await FindSubscriptionAsync(baseUrl, settings, subscription, cancellationToken);
                if (existing is not null)
                {
                    progress?.Report($"Updating subscription '{identifier}' (ID {existing.Id}).");
                    await PutAsync<WooSubscription>(baseUrl, settings, $"/wp-json/wc/v1/subscriptions/{existing.Id}", payload, cancellationToken);
                }
                else
                {
                    throw;
                }
            }
        }
    }

    private async Task<WooOrder?> FindOrderAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        WooOrder order,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(order.Number))
        {
            var list = await GetAsync<List<WooOrder>>(baseUrl, settings, $"/wp-json/wc/v3/orders?per_page=1&search={Uri.EscapeDataString(order.Number)}", cancellationToken);
            if (list is { Count: > 0 })
            {
                var match = list.FirstOrDefault(o => string.Equals(o.Number, order.Number, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return match;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(order.OrderKey))
        {
            var list = await GetAsync<List<WooOrder>>(baseUrl, settings, $"/wp-json/wc/v3/orders?per_page=1&search={Uri.EscapeDataString(order.OrderKey)}", cancellationToken);
            if (list is { Count: > 0 })
            {
                var match = list.FirstOrDefault(o => string.Equals(o.OrderKey, order.OrderKey, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return match;
                }
            }
        }

        return null;
    }

    private async Task<WooSubscription?> FindSubscriptionAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        WooSubscription subscription,
        CancellationToken cancellationToken)
    {
        if (subscription.Id > 0)
        {
            var existing = await GetAsync<WooSubscription>(baseUrl, settings, $"/wp-json/wc/v1/subscriptions/{subscription.Id}", cancellationToken);
            if (existing is not null)
            {
                return existing;
            }
        }

        if (!string.IsNullOrWhiteSpace(subscription.Number))
        {
            var list = await GetAsync<List<WooSubscription>>(baseUrl, settings, $"/wp-json/wc/v1/subscriptions?per_page=1&search={Uri.EscapeDataString(subscription.Number)}", cancellationToken);
            if (list is { Count: > 0 })
            {
                var match = list.FirstOrDefault(s => string.Equals(s.Number, subscription.Number, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return match;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(subscription.OrderKey))
        {
            var list = await GetAsync<List<WooSubscription>>(baseUrl, settings, $"/wp-json/wc/v1/subscriptions?per_page=1&search={Uri.EscapeDataString(subscription.OrderKey)}", cancellationToken);
            if (list is { Count: > 0 })
            {
                var match = list.FirstOrDefault(s => string.Equals(s.OrderKey, subscription.OrderKey, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return match;
                }
            }
        }

        return null;
    }

    private static string GetSubscriptionIdentifier(WooSubscription subscription)
    {
        if (!string.IsNullOrWhiteSpace(subscription.Number))
        {
            return subscription.Number!;
        }

        if (!string.IsNullOrWhiteSpace(subscription.OrderKey))
        {
            return subscription.OrderKey!;
        }

        return subscription.Id > 0 ? subscription.Id.ToString(CultureInfo.InvariantCulture) : "subscription";
    }

    private Dictionary<string, object?> BuildSubscriptionPayload(
        WooSubscription subscription,
        IReadOnlyDictionary<int, int> customerMap,
        List<Dictionary<string, object?>> lineItems,
        TaxRateLookup taxRateLookup)
    {
        var payload = new Dictionary<string, object?>();
        AddIfValue(payload, "status", subscription.Status);
        AddIfValue(payload, "currency", subscription.Currency);
        AddIfValue(payload, "customer_note", subscription.CustomerNote);
        AddIfValue(payload, "order_key", subscription.OrderKey);
        AddIfValue(payload, "billing_period", subscription.BillingPeriod);
        AddIfValue(payload, "billing_interval", subscription.BillingInterval);
        AddIfValue(payload, "payment_method", subscription.PaymentMethod);
        AddIfValue(payload, "payment_method_title", subscription.PaymentMethodTitle);
        AddIfValue(payload, "start_date", subscription.StartDate);
        AddIfValue(payload, "trial_end_date", subscription.TrialEndDate);
        AddIfValue(payload, "next_payment_date", subscription.NextPaymentDate);
        AddIfValue(payload, "end_date", subscription.EndDate);
        AddIfValue(payload, "date_created", subscription.DateCreated);
        AddIfValue(payload, "date_created_gmt", subscription.DateCreatedGmt);
        AddIfValue(payload, "date_modified", subscription.DateModified);
        AddIfValue(payload, "date_modified_gmt", subscription.DateModifiedGmt);

        var mappedCustomer = MapId(subscription.CustomerId, customerMap);
        if (mappedCustomer.HasValue)
        {
            payload["customer_id"] = mappedCustomer.Value;
        }

        if (subscription.Billing is not null)
        {
            payload["billing"] = subscription.Billing;
        }

        if (subscription.Shipping is not null)
        {
            payload["shipping"] = subscription.Shipping;
        }

        if (lineItems.Count > 0)
        {
            payload["line_items"] = lineItems;
        }

        var shippingLines = BuildShippingLines(subscription.ShippingLines);
        if (shippingLines.Count > 0)
        {
            payload["shipping_lines"] = shippingLines;
        }

        var feeLines = BuildFeeLines(subscription.FeeLines);
        if (feeLines.Count > 0)
        {
            payload["fee_lines"] = feeLines;
        }

        var taxLines = BuildTaxLines(subscription.TaxLines, taxRateLookup);
        if (taxLines.Count > 0)
        {
            payload["tax_lines"] = taxLines;
        }

        var meta = BuildMetaDataPayload(subscription.MetaData);
        if (meta.Count > 0)
        {
            payload["meta_data"] = meta;
        }

        return payload;
    }

    private static string GetOrderIdentifier(WooOrder order)
    {
        if (!string.IsNullOrWhiteSpace(order.Number))
        {
            return order.Number!;
        }

        if (!string.IsNullOrWhiteSpace(order.OrderKey))
        {
            return order.OrderKey!;
        }

        return order.Id > 0 ? order.Id.ToString(CultureInfo.InvariantCulture) : "order";
    }

    private Dictionary<string, object?> BuildOrderPayload(
        WooOrder order,
        IReadOnlyDictionary<int, int> productMap,
        IReadOnlyDictionary<int, int> customerMap,
        IReadOnlyDictionary<string, int> couponCodeMap,
        List<Dictionary<string, object?>> lineItems,
        TaxRateLookup taxRateLookup)
    {
        var payload = new Dictionary<string, object?>();
        AddIfValue(payload, "status", order.Status);
        AddIfValue(payload, "currency", order.Currency);

        var mappedCustomer = MapId(order.CustomerId, customerMap);
        if (mappedCustomer.HasValue)
        {
            payload["customer_id"] = mappedCustomer.Value;
        }

        if (order.Billing is not null)
        {
            payload["billing"] = order.Billing;
        }

        if (order.Shipping is not null)
        {
            payload["shipping"] = order.Shipping;
        }

        AddIfValue(payload, "payment_method", order.PaymentMethod);
        AddIfValue(payload, "payment_method_title", order.PaymentMethodTitle);
        AddIfValue(payload, "transaction_id", order.TransactionId);
        AddIfValue(payload, "customer_note", order.CustomerNote);
        AddIfValue(payload, "date_created", order.DateCreated);
        AddIfValue(payload, "date_created_gmt", order.DateCreatedGmt);
        AddIfValue(payload, "date_paid", order.DatePaid);
        AddIfValue(payload, "date_paid_gmt", order.DatePaidGmt);
        AddIfValue(payload, "date_completed", order.DateCompleted);
        AddIfValue(payload, "date_completed_gmt", order.DateCompletedGmt);
        AddIfValue(payload, "discount_total", order.DiscountTotal);
        AddIfValue(payload, "discount_tax", order.DiscountTax);
        AddIfValue(payload, "shipping_total", order.ShippingTotal);
        AddIfValue(payload, "shipping_tax", order.ShippingTax);
        AddIfValue(payload, "cart_tax", order.CartTax);
        AddIfValue(payload, "total", order.Total);
        AddIfValue(payload, "total_tax", order.TotalTax);
        payload["set_paid"] = DetermineIsPaid(order);

        if (lineItems.Count > 0)
        {
            payload["line_items"] = lineItems;
        }

        var couponLines = BuildOrderCouponLines(order.CouponLines, couponCodeMap);
        if (couponLines.Count > 0)
        {
            payload["coupon_lines"] = couponLines;
        }

        var shippingLines = BuildShippingLines(order.ShippingLines);
        if (shippingLines.Count > 0)
        {
            payload["shipping_lines"] = shippingLines;
        }

        var feeLines = BuildFeeLines(order.FeeLines);
        if (feeLines.Count > 0)
        {
            payload["fee_lines"] = feeLines;
        }

        var taxLines = BuildTaxLines(order.TaxLines, taxRateLookup);
        if (taxLines.Count > 0)
        {
            payload["tax_lines"] = taxLines;
        }

        var meta = BuildMetaDataPayload(order.MetaData);
        if (meta.Count > 0)
        {
            payload["meta_data"] = meta;
        }

        return payload;
    }

    private static List<Dictionary<string, object?>> BuildOrderLineItems(
        IEnumerable<WooOrderLineItem> items,
        IReadOnlyDictionary<int, int> productMap)
    {
        var result = new List<Dictionary<string, object?>>();
        foreach (var item in items ?? Enumerable.Empty<WooOrderLineItem>())
        {
            if (item is null)
            {
                continue;
            }

            var entry = new Dictionary<string, object?>();
            AddIfValue(entry, "name", item.Name);
            var mappedProduct = MapId(item.ProductId, productMap);
            if (mappedProduct.HasValue)
            {
                entry["product_id"] = mappedProduct.Value;
            }
            var mappedVariation = MapId(item.VariationId, productMap);
            if (mappedVariation.HasValue)
            {
                entry["variation_id"] = mappedVariation.Value;
            }
            if (item.Quantity > 0)
            {
                entry["quantity"] = item.Quantity;
            }
            AddIfValue(entry, "subtotal", item.Subtotal);
            AddIfValue(entry, "subtotal_tax", item.SubtotalTax);
            AddIfValue(entry, "total", item.Total);
            AddIfValue(entry, "total_tax", item.TotalTax);
            AddIfValue(entry, "price", item.Price);
            AddIfValue(entry, "sku", item.Sku);

            var meta = BuildMetaDataPayload(item.MetaData);
            if (meta.Count > 0)
            {
                entry["meta_data"] = meta;
            }

            if (entry.Count > 0)
            {
                result.Add(entry);
            }
        }

        return result;
    }

    private static List<Dictionary<string, object?>> BuildOrderCouponLines(
        IEnumerable<WooOrderCouponLine> coupons,
        IReadOnlyDictionary<string, int> couponCodeMap)
    {
        var result = new List<Dictionary<string, object?>>();
        foreach (var coupon in coupons ?? Enumerable.Empty<WooOrderCouponLine>())
        {
            if (coupon is null)
            {
                continue;
            }

            var entry = new Dictionary<string, object?>();
            AddIfValue(entry, "code", coupon.Code);
            AddIfValue(entry, "discount", coupon.Discount);
            AddIfValue(entry, "discount_tax", coupon.DiscountTax);

            if (!string.IsNullOrWhiteSpace(coupon.Code) && couponCodeMap.TryGetValue(coupon.Code, out var mappedCoupon))
            {
                entry["coupon_id"] = mappedCoupon;
            }

            var meta = BuildMetaDataPayload(coupon.MetaData);
            if (meta.Count > 0)
            {
                entry["meta_data"] = meta;
            }

            if (entry.Count > 0)
            {
                result.Add(entry);
            }
        }

        return result;
    }

    private static List<Dictionary<string, object?>> BuildShippingLines(IEnumerable<WooOrderShippingLine> lines)
    {
        var result = new List<Dictionary<string, object?>>();
        foreach (var line in lines ?? Enumerable.Empty<WooOrderShippingLine>())
        {
            if (line is null)
            {
                continue;
            }

            var entry = new Dictionary<string, object?>();
            AddIfValue(entry, "method_title", line.MethodTitle);
            AddIfValue(entry, "method_id", line.MethodId);
            AddIfValue(entry, "total", line.Total);
            AddIfValue(entry, "total_tax", line.TotalTax);

            var meta = BuildMetaDataPayload(line.MetaData);
            if (meta.Count > 0)
            {
                entry["meta_data"] = meta;
            }

            if (entry.Count > 0)
            {
                result.Add(entry);
            }
        }

        return result;
    }

    private static List<Dictionary<string, object?>> BuildFeeLines(IEnumerable<WooOrderFeeLine> lines)
    {
        var result = new List<Dictionary<string, object?>>();
        foreach (var line in lines ?? Enumerable.Empty<WooOrderFeeLine>())
        {
            if (line is null)
            {
                continue;
            }

            var entry = new Dictionary<string, object?>();
            AddIfValue(entry, "name", line.Name);
            AddIfValue(entry, "tax_class", line.TaxClass);
            AddIfValue(entry, "tax_status", line.TaxStatus);
            AddIfValue(entry, "total", line.Total);
            AddIfValue(entry, "total_tax", line.TotalTax);

            var meta = BuildMetaDataPayload(line.MetaData);
            if (meta.Count > 0)
            {
                entry["meta_data"] = meta;
            }

            if (entry.Count > 0)
            {
                result.Add(entry);
            }
        }

        return result;
    }

    private static List<Dictionary<string, object?>> BuildTaxLines(
        IEnumerable<WooOrderTaxLine> lines,
        TaxRateLookup taxRateLookup)
    {
        var result = new List<Dictionary<string, object?>>();
        foreach (var line in lines ?? Enumerable.Empty<WooOrderTaxLine>())
        {
            if (line is null)
            {
                continue;
            }

            var entry = new Dictionary<string, object?>();
            AddIfValue(entry, "rate_code", line.RateCode);
            if (taxRateLookup.TryMap(line, out var mappedRateId))
            {
                entry["rate_id"] = mappedRateId;
            }
            AddIfValue(entry, "label", line.Label);
            AddIfValue(entry, "compound", line.Compound);
            AddIfValue(entry, "tax_total", line.TaxTotal);
            AddIfValue(entry, "shipping_tax_total", line.ShippingTaxTotal);

            var meta = BuildMetaDataPayload(line.MetaData);
            if (meta.Count > 0)
            {
                entry["meta_data"] = meta;
            }

            if (entry.Count > 0)
            {
                result.Add(entry);
            }
        }

        return result;
    }

    private static bool DetermineIsPaid(WooOrder order)
    {
        if (!string.IsNullOrWhiteSpace(order.DatePaid) || !string.IsNullOrWhiteSpace(order.DatePaidGmt))
        {
            return true;
        }

        var status = order.Status?.Trim().ToLowerInvariant();
        return status is "completed" or "processing";
    }

    private static List<Dictionary<string, object?>> BuildMetaDataPayload(IEnumerable<StoreMetaData>? metaData)
    {
        var result = new List<Dictionary<string, object?>>();
        if (metaData is null)
        {
            return result;
        }

        foreach (var meta in metaData)
        {
            if (meta is null || string.IsNullOrWhiteSpace(meta.Key))
            {
                continue;
            }

            var value = meta.ValueAsString();
            if (value is null)
            {
                continue;
            }

            result.Add(new Dictionary<string, object?>
            {
                ["key"] = meta.Key!,
                ["value"] = value
            });
        }

        return result;
    }

    private static List<int> MapIds(IEnumerable<int>? source, IReadOnlyDictionary<int, int> map)
    {
        var result = new List<int>();
        if (source is null)
        {
            return result;
        }

        foreach (var id in source)
        {
            if (map.TryGetValue(id, out var mapped))
            {
                result.Add(mapped);
            }
        }

        return result;
    }

    private static int? MapId(int? sourceId, IReadOnlyDictionary<int, int> map)
    {
        if (sourceId is int value && value > 0 && map.TryGetValue(value, out var mapped))
        {
            return mapped;
        }

        return null;
    }

    private async Task<List<Dictionary<string, object?>>> BuildImagePayloadAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        StoreProduct product,
        Dictionary<string, int> mediaCache,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var images = new List<Dictionary<string, object?>>();
        var seenIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddImageById(int id)
        {
            if (id <= 0)
            {
                return;
            }

            if (!seenIdentifiers.Add($"id:{id}"))
            {
                return;
            }

            images.Add(new Dictionary<string, object?> { ["id"] = id });
        }

        void AddImageBySource(string? src, string? alt)
        {
            if (string.IsNullOrWhiteSpace(src))
            {
                return;
            }

            if (!seenIdentifiers.Add($"src:{src}"))
            {
                return;
            }

            images.Add(new Dictionary<string, object?>
            {
                ["src"] = src,
                ["alt"] = alt
            });
        }

        if (product.LocalImageFilePaths.Count > 0)
        {
            foreach (var path in product.LocalImageFilePaths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (!File.Exists(path))
                {
                    progress?.Report($"Image file missing: {path}");
                    continue;
                }

                if (mediaCache.TryGetValue(path, out var cachedId))
                {
                    AddImageById(cachedId);
                    continue;
                }

                var uploaded = await UploadMediaAsync(baseUrl, settings, path, progress, cancellationToken);
                if (uploaded.HasValue)
                {
                    mediaCache[path] = uploaded.Value;
                    AddImageById(uploaded.Value);
                }
            }
        }

        if (product.Images is { Count: > 0 })
        {
            foreach (var img in product.Images)
            {
                if (img is null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(img.Src)
                    && mediaCache.TryGetValue(img.Src, out var cachedFromSrc))
                {
                    AddImageById(cachedFromSrc);
                    continue;
                }

                if (img.Id > 0)
                {
                    var idKey = img.Id.ToString(CultureInfo.InvariantCulture);
                    if (mediaCache.TryGetValue(idKey, out var cachedFromId))
                    {
                        AddImageById(cachedFromId);
                        continue;
                    }
                }

                AddImageBySource(img.Src, img.Alt);
            }
        }

        return images;
    }

    private async Task<int?> UploadMediaAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        string filePath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            progress?.Report($"Uploading media {Path.GetFileName(filePath)}…");
            using var stream = File.OpenRead(filePath);
            using var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue(ResolveMimeType(filePath));
            using var form = new MultipartFormDataContent();
            form.Add(content, "file", Path.GetFileName(filePath));

            var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(baseUrl, "/wp-json/wp/v2/media"));
            request.Headers.Authorization = CreateMediaAuthHeader(settings);
            request.Headers.Accept.ParseAdd("application/json");
            request.Content = form;

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                progress?.Report($"Media upload failed ({(int)response.StatusCode}): {error}");
                return null;
            }

            await using var streamResult = await response.Content.ReadAsStreamAsync(cancellationToken);
            var media = await JsonSerializer.DeserializeAsync<WooMediaResponse>(streamResult, _readOptions, cancellationToken);
            return media?.Id;
        }
        catch (Exception ex)
        {
            progress?.Report($"Media upload failed: {ex.Message}");
            return null;
        }
    }

    private static string ResolveMimeType(string filePath)
        => Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream"
        };

    private async Task<WooProductSummary?> FindExistingProductAsync(string baseUrl, WooProvisioningSettings settings, StoreProduct product, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(product.Sku))
        {
            var bySku = await GetAsync<List<WooProductSummary>>(baseUrl, settings, $"/wp-json/wc/v3/products?per_page=1&sku={Uri.EscapeDataString(product.Sku)}", cancellationToken);
            if (bySku is { Count: > 0 })
            {
                return bySku[0];
            }
        }

        var slug = NormalizeSlug(product.Slug);
        if (!string.IsNullOrWhiteSpace(slug))
        {
            var bySlug = await GetAsync<List<WooProductSummary>>(baseUrl, settings, $"/wp-json/wc/v3/products?per_page=1&slug={Uri.EscapeDataString(slug)}", cancellationToken);
            if (bySlug is { Count: > 0 })
            {
                return bySlug[0];
            }
        }

        return null;
    }

    private async Task ApplyStoreConfigurationAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        StoreConfiguration configuration,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (configuration.StoreSettings is { Count: > 0 })
        {
            await ApplyStoreSettingsAsync(baseUrl, settings, configuration.StoreSettings, progress, cancellationToken);
        }

        if (configuration.ShippingZones is { Count: > 0 })
        {
            await ApplyShippingZonesAsync(baseUrl, settings, configuration.ShippingZones, progress, cancellationToken);
        }

        if (configuration.PaymentGateways is { Count: > 0 })
        {
            await ApplyPaymentGatewaysAsync(baseUrl, settings, configuration.PaymentGateways, progress, cancellationToken);
        }
    }

    private async Task ApplyStoreSettingsAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        List<WooStoreSetting> storeSettings,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var grouped = storeSettings
            .Where(s => !string.IsNullOrWhiteSpace(s.Id) && !string.IsNullOrWhiteSpace(s.GroupId))
            .GroupBy(s => s.GroupId!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = group
                .Select(setting => new SettingUpdateRequest
                {
                    Id = setting.Id!,
                    Value = setting.Value is JsonElement element ? ConvertSettingValue(element) : null
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .ToList();

            if (payload.Count == 0)
            {
                continue;
            }

            var path = $"/wp-json/wc/v3/settings/{Uri.EscapeDataString(group.Key)}";
            progress?.Report($"Applying settings group '{group.Key}' ({payload.Count} fields)…");
            await PutAsync<List<WooStoreSetting>>(baseUrl, settings, path, payload, cancellationToken);
        }
    }

    private async Task ApplyShippingZonesAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        List<ShippingZoneSetting> zones,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var zone in zones)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (zone.Id <= 0)
            {
                continue;
            }

            var zoneLabel = zone.Name ?? zone.Id.ToString(CultureInfo.InvariantCulture);
            var zonePayload = new Dictionary<string, object?>();
            AddIfValue(zonePayload, "name", zone.Name);
            AddIfValue(zonePayload, "order", zone.Order);
            var targetZoneId = zone.Id;
            if (zonePayload.Count > 0)
            {
                progress?.Report($"Updating shipping zone '{zoneLabel}'…");
                try
                {
                    await PutAsync<ShippingZoneSetting>(baseUrl, settings, $"/wp-json/wc/v3/shipping/zones/{zone.Id}", zonePayload, cancellationToken);
                }
                catch (WooProvisioningException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    var createPayload = new Dictionary<string, object?>(zonePayload);
                    if (!createPayload.ContainsKey("name"))
                    {
                        createPayload["name"] = zone.Name ?? zoneLabel;
                    }

                    progress?.Report($"Creating shipping zone '{zoneLabel}'…");
                    var createdZone = await PostAsync<ShippingZoneSetting>(baseUrl, settings, "/wp-json/wc/v3/shipping/zones", createPayload, cancellationToken);
                    targetZoneId = createdZone.Id;
                    zone.Id = createdZone.Id;
                }
            }

            if (zone.Locations is { Count: > 0 })
            {
                progress?.Report($"Updating shipping zone '{zoneLabel}' locations…");
                await PutAsync<List<ShippingZoneLocation>>(baseUrl, settings, $"/wp-json/wc/v3/shipping/zones/{targetZoneId}/locations", zone.Locations, cancellationToken);
            }

            if (zone.Methods is { Count: > 0 })
            {
                foreach (var method in zone.Methods)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (method is null)
                    {
                        continue;
                    }

                    var methodLabel = method.Title
                        ?? method.MethodTitle
                        ?? method.Id
                        ?? method.MethodId
                        ?? method.InstanceId.ToString(CultureInfo.InvariantCulture);

                    var methodPayload = new Dictionary<string, object?>();
                    AddIfValue(methodPayload, "title", method.Title);
                    AddIfValue(methodPayload, "order", method.Order);
                    AddIfValue(methodPayload, "enabled", method.Enabled);
                    var methodSettings = BuildSettingsValueDictionary(method.Settings);
                    if (methodSettings is not null && methodSettings.Count > 0)
                    {
                        methodPayload["settings"] = methodSettings;
                    }

                    if (method.InstanceId <= 0 && string.IsNullOrWhiteSpace(method.Id) && string.IsNullOrWhiteSpace(method.MethodId))
                    {
                        continue;
                    }

                    if (method.InstanceId > 0 && methodPayload.Count > 0)
                    {
                        progress?.Report($"Updating shipping method '{methodLabel}' in zone '{zoneLabel}'…");
                        try
                        {
                            await PutAsync<ShippingZoneMethodSetting>(baseUrl, settings, $"/wp-json/wc/v3/shipping/zones/{targetZoneId}/methods/{method.InstanceId}", methodPayload, cancellationToken);
                            continue;
                        }
                        catch (WooProvisioningException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                        {
                            // Fall back to creation when the instance is missing on the target.
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(method.MethodId ?? method.Id))
                    {
                        var createPayload = new Dictionary<string, object?>
                        {
                            ["method_id"] = method.MethodId ?? method.Id
                        };
                        foreach (var kvp in methodPayload)
                        {
                            createPayload[kvp.Key] = kvp.Value;
                        }

                        progress?.Report($"Creating shipping method '{methodLabel}' in zone '{zoneLabel}'…");
                        await PostAsync<ShippingZoneMethodSetting>(baseUrl, settings, $"/wp-json/wc/v3/shipping/zones/{targetZoneId}/methods", createPayload, cancellationToken);
                    }
                }
            }
        }
    }

    private async Task ApplyPaymentGatewaysAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        List<PaymentGatewaySetting> gateways,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var gateway in gateways)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (gateway is null || string.IsNullOrWhiteSpace(gateway.Id))
            {
                continue;
            }

            var payload = new Dictionary<string, object?>();
            AddIfValue(payload, "title", gateway.Title);
            AddIfValue(payload, "description", gateway.Description);
            AddIfValue(payload, "order", gateway.Order);
            AddIfValue(payload, "enabled", gateway.Enabled);

            var settingsPayload = BuildSettingsValueDictionary(gateway.Settings);
            if (settingsPayload is not null && settingsPayload.Count > 0)
            {
                payload["settings"] = settingsPayload;
            }

            if (payload.Count == 0)
            {
                continue;
            }

            var label = gateway.Title ?? gateway.MethodTitle ?? gateway.Id;
            progress?.Report($"Updating payment gateway '{label}'…");
            await PutAsync<PaymentGatewaySetting>(baseUrl, settings, $"/wp-json/wc/v3/payment_gateways/{gateway.Id}", payload, cancellationToken);
        }
    }

    private Dictionary<string, object?>? BuildSettingsValueDictionary(Dictionary<string, WooStoreSetting>? settings)
    {
        if (settings is null || settings.Count == 0)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in settings)
        {
            if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value is null)
            {
                continue;
            }

            var value = kvp.Value.Value is JsonElement element ? ConvertSettingValue(element) : null;
            result[kvp.Key] = new Dictionary<string, object?>
            {
                ["value"] = value
            };
        }

        return result.Count > 0 ? result : null;
    }

    private object? ConvertSettingValue(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var l))
                {
                    return l;
                }
                if (element.TryGetDecimal(out var dec))
                {
                    return dec;
                }
                break;
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.Array:
            case JsonValueKind.Object:
                return JsonSerializer.Deserialize<object>(element.GetRawText(), _readOptions);
        }

        return element.GetRawText();
    }

    private async Task<Dictionary<string, int>> EnsureTaxonomiesAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        string resource,
        List<TaxonomySeed> seeds,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var orderedSeeds = resource.Equals("categories", StringComparison.OrdinalIgnoreCase)
            ? OrderTaxonomySeedsByHierarchy(seeds)
            : seeds;

        foreach (var seed in orderedSeeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int? parentId = null;
            if (!string.IsNullOrWhiteSpace(seed.ParentKey)
                && resource.Equals("categories", StringComparison.OrdinalIgnoreCase))
            {
                if (!result.TryGetValue(seed.ParentKey, out var mappedParent))
                {
                    var existingParent = await FindTaxonomyAsync(baseUrl, settings, resource, seed.ParentKey, cancellationToken);
                    if (existingParent is not null)
                    {
                        mappedParent = existingParent.Id;
                        result[seed.ParentKey] = mappedParent;
                    }
                }

                if (result.TryGetValue(seed.ParentKey, out var ensuredParent))
                {
                    parentId = ensuredParent;
                }
            }

            var id = await EnsureTaxonomyAsync(baseUrl, settings, resource, seed, parentId, progress, cancellationToken);
            result[seed.Key] = id;
        }

        return result;
    }

    private async Task<int> EnsureTaxonomyAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        string resource,
        TaxonomySeed seed,
        int? parentId,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var existing = await FindTaxonomyAsync(baseUrl, settings, resource, seed.Slug, cancellationToken);
        if (existing is not null)
        {
            return existing.Id;
        }

        progress?.Report($"Creating {resource.TrimEnd('s')} '{seed.Name}'…");
        var payload = new Dictionary<string, object?>
        {
            ["name"] = seed.Name,
            ["slug"] = seed.Slug
        };

        if (parentId is > 0)
        {
            payload["parent"] = parentId.Value;
        }

        try
        {
            var created = await PostAsync<WooTaxonomyResponse>(baseUrl, settings, $"/wp-json/wc/v3/products/{resource}", payload, cancellationToken);
            return created.Id;
        }
        catch (WooProvisioningException ex) when (ex.StatusCode == HttpStatusCode.BadRequest || ex.StatusCode == HttpStatusCode.Conflict)
        {
            progress?.Report($"{resource[..1].ToUpper() + resource[1..]} '{seed.Name}' may already exist ({(int)ex.StatusCode}). Retrying fetch.");
            existing = await FindTaxonomyAsync(baseUrl, settings, resource, seed.Slug, cancellationToken);
            if (existing is not null)
            {
                return existing.Id;
            }

            throw;
        }
    }

    private async Task<WooTaxonomyResponse?> FindTaxonomyAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        string resource,
        string slug,
        CancellationToken cancellationToken)
    {
        var list = await GetAsync<List<WooTaxonomyResponse>>(baseUrl, settings, $"/wp-json/wc/v3/products/{resource}?per_page=1&slug={Uri.EscapeDataString(slug)}", cancellationToken);
        if (list is { Count: > 0 })
        {
            return list[0];
        }
        return null;
    }

    private async Task<IReadOnlyDictionary<string, int>> EnsureAttributesAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        Dictionary<string, AttributeSeed> seeds,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var seed in seeds.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = await EnsureAttributeAsync(baseUrl, settings, seed, progress, cancellationToken);
            result[seed.Key] = id;

            if (seed.Terms.Count > 0)
            {
                await EnsureAttributeTermsAsync(baseUrl, settings, id, seed, progress, cancellationToken);
            }
        }

        return result;
    }

    private async Task<int> EnsureAttributeAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        AttributeSeed seed,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var existing = await FindAttributeAsync(baseUrl, settings, seed.Slug, cancellationToken);
        if (existing is not null)
        {
            return existing.Id;
        }

        progress?.Report($"Creating attribute '{seed.Name}'…");
        var payload = new Dictionary<string, object?>
        {
            ["name"] = seed.Name,
            ["slug"] = seed.Slug
        };

        try
        {
            var created = await PostAsync<WooAttributeResponse>(baseUrl, settings, "/wp-json/wc/v3/products/attributes", payload, cancellationToken);
            return created.Id;
        }
        catch (WooProvisioningException ex) when (ex.StatusCode == HttpStatusCode.BadRequest || ex.StatusCode == HttpStatusCode.Conflict)
        {
            progress?.Report($"Attribute '{seed.Name}' create returned {(int)ex.StatusCode}. Retrying fetch.");
            existing = await FindAttributeAsync(baseUrl, settings, seed.Slug, cancellationToken);
            if (existing is not null)
            {
                return existing.Id;
            }

            throw;
        }
    }

    private async Task EnsureAttributeTermsAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        int attributeId,
        AttributeSeed seed,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var term in seed.Terms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slug = NormalizeSlug(term);
            if (slug is null)
            {
                continue;
            }
            var existing = await FindAttributeTermAsync(baseUrl, settings, attributeId, slug, cancellationToken);
            if (existing is not null)
            {
                continue;
            }

            progress?.Report($"Creating attribute term '{term}' for '{seed.Name}'…");
            var payload = new Dictionary<string, object?>
            {
                ["name"] = term,
                ["slug"] = slug
            };

            try
            {
                await PostAsync<WooTermResponse>(baseUrl, settings, $"/wp-json/wc/v3/products/attributes/{attributeId}/terms", payload, cancellationToken);
            }
            catch (WooProvisioningException ex) when (ex.StatusCode == HttpStatusCode.BadRequest || ex.StatusCode == HttpStatusCode.Conflict)
            {
                progress?.Report($"Attribute term '{term}' may already exist ({(int)ex.StatusCode}).");
            }
        }
    }

    private async Task<WooAttributeResponse?> FindAttributeAsync(string baseUrl, WooProvisioningSettings settings, string slug, CancellationToken cancellationToken)
    {
        var list = await GetAsync<List<WooAttributeResponse>>(baseUrl, settings, $"/wp-json/wc/v3/products/attributes?per_page=100", cancellationToken);
        if (list is null)
        {
            return null;
        }

        return list.FirstOrDefault(a => string.Equals(a.Slug, slug, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<WooTermResponse?> FindAttributeTermAsync(string baseUrl, WooProvisioningSettings settings, int attributeId, string slug, CancellationToken cancellationToken)
    {
        var list = await GetAsync<List<WooTermResponse>>(baseUrl, settings, $"/wp-json/wc/v3/products/attributes/{attributeId}/terms?per_page=100", cancellationToken);
        if (list is null)
        {
            return null;
        }

        return list.FirstOrDefault(t => string.Equals(t.Slug, slug, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<T> PostAsync<T>(string baseUrl, WooProvisioningSettings settings, string path, object payload, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(baseUrl, path, settings));
        request.Headers.Authorization = CreateAuthHeader(settings);
        request.Headers.Accept.ParseAdd("application/json");
        var json = JsonSerializer.Serialize(payload, _writeOptions);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return await HandleResponseAsync<T>(response, cancellationToken);
    }

    private async Task<T> PutAsync<T>(string baseUrl, WooProvisioningSettings settings, string path, object payload, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, BuildUrl(baseUrl, path, settings));
        request.Headers.Authorization = CreateAuthHeader(settings);
        request.Headers.Accept.ParseAdd("application/json");
        var json = JsonSerializer.Serialize(payload, _writeOptions);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return await HandleResponseAsync<T>(response, cancellationToken);
    }

    private async Task<T?> GetAsync<T>(string baseUrl, WooProvisioningSettings settings, string path, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(baseUrl, path, settings));
        request.Headers.Authorization = CreateAuthHeader(settings);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return default;
        }

        if (!response.IsSuccessStatusCode)
        {
            var message = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new WooProvisioningException(response.StatusCode, message);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, _readOptions, cancellationToken);
    }

    private async Task<T> HandleResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var message = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new WooProvisioningException(response.StatusCode, message);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<T>(stream, _readOptions, cancellationToken);
        if (result is null)
        {
            throw new WooProvisioningException(response.StatusCode, "Empty response from WooCommerce API.");
        }

        return result;
    }

    private static AuthenticationHeaderValue CreateMediaAuthHeader(WooProvisioningSettings settings)
    {
        if (settings.HasWordPressCredentials)
        {
            return CreateBasicAuthHeader(settings.WordPressUsername!, settings.WordPressApplicationPassword!);
        }

        return CreateAuthHeader(settings);
    }

    private static AuthenticationHeaderValue CreateAuthHeader(WooProvisioningSettings settings)
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.ConsumerKey}:{settings.ConsumerSecret}"));
        return new AuthenticationHeaderValue("Basic", credentials);
    }

    private static AuthenticationHeaderValue CreateBasicAuthHeader(string username, string password)
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        return new AuthenticationHeaderValue("Basic", credentials);
    }

    private static string BuildUrl(string baseUrl, string path) => baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');

    private static string BuildUrl(string baseUrl, string path, WooProvisioningSettings settings)
    {
        var includeKeys = path.Contains("/wc/");
        var builder = new StringBuilder();
        builder.Append(baseUrl.TrimEnd('/'));
        builder.Append('/');
        builder.Append(path.TrimStart('/'));
        if (includeKeys)
        {
            builder.Append(path.Contains('?') ? '&' : '?');
            builder.Append("consumer_key=");
            builder.Append(Uri.EscapeDataString(settings.ConsumerKey));
            builder.Append("&consumer_secret=");
            builder.Append(Uri.EscapeDataString(settings.ConsumerSecret));
        }
        return builder.ToString();
    }

    private static IEnumerable<string> BuildOptionWriteEndpoints(string scope, string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            yield break;
        }

        var esc = Uri.EscapeDataString(slug);
        if (string.Equals(scope, "plugin", StringComparison.OrdinalIgnoreCase))
        {
            yield return $"/wp-json/wc-scraper/v1/plugins/{esc}/options";
            yield return $"/wp-json/wc-scraper/v1/plugin-options?slug={esc}";
            yield return $"/?rest_route=/wc-scraper/v1/plugin-options&slug={esc}";
            yield return $"/wp-json/{slug}/v1/options";
            yield return $"/wp-json/{slug}/v1/settings";
        }
        else
        {
            yield return $"/wp-json/wc-scraper/v1/themes/{esc}/options";
            yield return $"/wp-json/wc-scraper/v1/theme-options?slug={esc}";
            yield return $"/?rest_route=/wc-scraper/v1/theme-options&slug={esc}";
            yield return $"/wp-json/{slug}/v1/options";
            yield return $"/wp-json/{slug}/v1/settings";
        }
    }

    private static void AddIfValue(IDictionary<string, object?> dict, string key, object? value)
    {
        if (value is null)
        {
            return;
        }

        if (value is string s && string.IsNullOrWhiteSpace(s))
        {
            return;
        }

        dict[key] = value;
    }

    private sealed class SettingUpdateRequest
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("value")] public object? Value { get; set; }
    }

    private static string? ResolvePrice(PriceInfo? price)
    {
        if (price is null)
        {
            return null;
        }

        var candidates = new[] { price.RegularPrice, price.Price, price.SalePrice };
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (decimal.TryParse(candidate, NumberStyles.Any, CultureInfo.InvariantCulture, out var dec))
            {
                if (price.CurrencyMinorUnit is int minor && minor > 0 && candidate.All(char.IsDigit))
                {
                    var divisor = (decimal)Math.Pow(10, minor);
                    dec /= divisor;
                }
                return dec.ToString("0.00", CultureInfo.InvariantCulture);
            }

            if (long.TryParse(candidate, out var integer) && price.CurrencyMinorUnit is int unit && unit > 0)
            {
                var divisor = (decimal)Math.Pow(10, unit);
                var result = integer / divisor;
                return result.ToString("0.00", CultureInfo.InvariantCulture);
            }

            return candidate;
        }

        return null;
    }

    private static string? ResolveSalePrice(PriceInfo? price)
    {
        if (price?.SalePrice is null)
        {
            return null;
        }

        var info = new PriceInfo
        {
            SalePrice = price.SalePrice,
            CurrencyMinorUnit = price.CurrencyMinorUnit
        };

        return ResolvePrice(info);
    }

    private static string? ResolveStockStatus(StoreProduct product)
    {
        if (!string.IsNullOrWhiteSpace(product.StockStatus))
        {
            return product.StockStatus.Trim();
        }

        if (product.IsInStock is bool inStock)
        {
            return inStock ? "instock" : "outofstock";
        }

        return null;
    }

    private static List<Dictionary<string, object?>> BuildTaxonomyReferences<T>(IEnumerable<T>? items, IReadOnlyDictionary<string, int> map)
        where T : class
    {
        var references = new List<Dictionary<string, object?>>();
        if (items is null)
        {
            return references;
        }

        foreach (var item in items)
        {
            switch (item)
            {
                case Category category:
                {
                    var key = BuildTaxonomyKey(category.Slug, category.Name);
                    if (key is not null && map.TryGetValue(key, out var id))
                    {
                        references.Add(new Dictionary<string, object?> { ["id"] = id });
                    }
                    break;
                }
                case ProductTag tag:
                {
                    var key = BuildTaxonomyKey(tag.Slug, tag.Name);
                    if (key is not null && map.TryGetValue(key, out var id))
                    {
                        references.Add(new Dictionary<string, object?> { ["id"] = id });
                    }
                    break;
                }
            }
        }

        return references;
    }

    private static Dictionary<int, int> BuildCategoryIdMap(
        IEnumerable<StoreProduct> products,
        IReadOnlyDictionary<string, int> categoryMap)
    {
        var result = new Dictionary<int, int>();
        foreach (var product in products)
        {
            if (product?.Categories is null)
            {
                continue;
            }

            foreach (var category in product.Categories)
            {
                if (category is null || category.Id <= 0)
                {
                    continue;
                }

                var key = BuildTaxonomyKey(category.Slug, category.Name);
                if (key is null || !categoryMap.TryGetValue(key, out var mappedId))
                {
                    continue;
                }

                result[category.Id] = mappedId;
            }
        }

        return result;
    }

    private static Dictionary<int, int> BuildTagIdMap(
        IEnumerable<StoreProduct> products,
        IReadOnlyDictionary<string, int> tagMap)
    {
        var result = new Dictionary<int, int>();
        foreach (var product in products)
        {
            if (product?.Tags is null || product.Tags.Count == 0)
            {
                continue;
            }

            foreach (var tag in product.Tags)
            {
                if (tag is null || tag.Id <= 0)
                {
                    continue;
                }

                var key = BuildTaxonomyKey(tag.Slug, tag.Name);
                if (key is null || !tagMap.TryGetValue(key, out var mappedId))
                {
                    continue;
                }

                result[tag.Id] = mappedId;
            }
        }

        return result;
    }

    private static List<Dictionary<string, object?>> BuildAttributePayload(StoreProduct product, IReadOnlyDictionary<string, int> attributeMap)
    {
        var payload = new List<Dictionary<string, object?>>();
        if (product.Attributes is null || product.Attributes.Count == 0)
        {
            return payload;
        }

        var grouped = product.Attributes
            .Select(attr => (Attribute: attr, Key: NormalizeAttributeKey(attr)))
            .Where(tuple => tuple.Key is not null && attributeMap.ContainsKey(tuple.Key))
            .GroupBy(tuple => tuple.Key!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            var options = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tuple in group)
            {
                var value = ResolveAttributeValue(tuple.Attribute);
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (seen.Add(value))
                {
                    options.Add(value);
                }
            }

            if (options.Count == 0)
            {
                continue;
            }

            var attributeId = attributeMap[group.Key];
            payload.Add(new Dictionary<string, object?>
            {
                ["id"] = attributeId,
                ["options"] = options
            });
        }

        return payload;
    }

    private static Dictionary<string, AttributeSeed> CollectAttributeSeeds(IEnumerable<StoreProduct> products)
    {
        var seeds = new Dictionary<string, AttributeSeed>(StringComparer.OrdinalIgnoreCase);
        foreach (var product in products)
        {
            if (product.Attributes is null)
            {
                continue;
            }

            foreach (var attr in product.Attributes)
            {
                var key = NormalizeAttributeKey(attr);
                if (key is null)
                {
                    continue;
                }

                var slug = NormalizeAttributeSlug(attr) ?? key;
                if (!seeds.TryGetValue(key, out var seed))
                {
                    seed = new AttributeSeed(key, attr.Name ?? attr.AttributeKey ?? key, slug);
                    seeds[key] = seed;
                }

                var value = ResolveAttributeValue(attr);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    seed.Terms.Add(value);
                }
            }
        }

        return seeds;
    }

    private static List<TaxonomySeed> CollectTaxonomySeeds<T>(
        IEnumerable<T> items,
        Func<T, string?> getName,
        Func<T, string?> getSlug,
        Func<T, int?>? getId = null,
        Func<T, int?>? getParentId = null,
        Func<T, string?>? getParentSlug = null,
        Func<T, string?>? getParentName = null)
    {
        var result = new Dictionary<string, TaxonomySeed>(StringComparer.OrdinalIgnoreCase);
        var idLookup = new Dictionary<int, string>();
        var pendingParents = new Dictionary<string, int>();
        foreach (var item in items)
        {
            var name = getName(item);
            var slug = getSlug(item);
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(slug))
            {
                continue;
            }

            var normalizedSlug = NormalizeSlug(slug) ?? NormalizeSlug(name);
            if (normalizedSlug is null)
            {
                continue;
            }

            if (!result.TryGetValue(normalizedSlug, out var seed))
            {
                var displayName = string.IsNullOrWhiteSpace(name)
                    ? (string.IsNullOrWhiteSpace(slug) ? normalizedSlug : slug!)
                    : name!;
                seed = new TaxonomySeed(normalizedSlug, displayName, normalizedSlug);
                result[normalizedSlug] = seed;
            }

            if (getId is not null)
            {
                var id = getId(item);
                if (id is > 0)
                {
                    idLookup[id.Value] = seed.Key;
                }
            }

            string? parentKey = null;
            if (getParentId is not null)
            {
                var parentId = getParentId(item);
                if (parentId is > 0)
                {
                    if (idLookup.TryGetValue(parentId.Value, out var mappedParent))
                    {
                        parentKey = mappedParent;
                    }
                    else if (!pendingParents.ContainsKey(seed.Key))
                    {
                        pendingParents[seed.Key] = parentId.Value;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(parentKey) && getParentSlug is not null)
            {
                var candidate = NormalizeSlug(getParentSlug(item));
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    parentKey = candidate;
                }
            }

            if (!string.IsNullOrWhiteSpace(parentKey))
            {
                seed.ParentKey = parentKey;
                if (!result.ContainsKey(parentKey))
                {
                    var parentDisplayName = getParentName?.Invoke(item);
                    var displayName = string.IsNullOrWhiteSpace(parentDisplayName)
                        ? parentKey
                        : parentDisplayName!;
                    result[parentKey] = new TaxonomySeed(parentKey, displayName, parentKey);
                }
            }
        }

        foreach (var kvp in pendingParents)
        {
            if (idLookup.TryGetValue(kvp.Value, out var parentKey) && result.TryGetValue(kvp.Key, out var seed))
            {
                seed.ParentKey = parentKey;
            }
        }

        return result.Values.ToList();
    }

    private static List<TaxonomySeed> OrderTaxonomySeedsByHierarchy(List<TaxonomySeed> seeds)
    {
        if (seeds.Count <= 1)
        {
            return seeds;
        }

        var byKey = seeds.ToDictionary(s => s.Key, s => s, StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<TaxonomySeed>();

        void Visit(TaxonomySeed seed)
        {
            if (!visiting.Add(seed.Key))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(seed.ParentKey)
                && byKey.TryGetValue(seed.ParentKey, out var parent)
                && !visited.Contains(parent.Key))
            {
                Visit(parent);
            }

            visiting.Remove(seed.Key);

            if (visited.Add(seed.Key))
            {
                ordered.Add(seed);
            }
        }

        foreach (var seed in seeds.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!visited.Contains(seed.Key))
            {
                Visit(seed);
            }
        }

        return ordered;
    }

    private static string? NormalizeAttributeKey(VariationAttribute attribute)
    {
        if (!string.IsNullOrWhiteSpace(attribute.AttributeKey))
        {
            return attribute.AttributeKey.Trim().ToLowerInvariant();
        }

        if (!string.IsNullOrWhiteSpace(attribute.Taxonomy))
        {
            return attribute.Taxonomy.Trim().ToLowerInvariant();
        }

        return NormalizeSlug(attribute.Name);
    }

    private static string? NormalizeAttributeSlug(VariationAttribute attribute)
    {
        var key = NormalizeAttributeKey(attribute);
        if (key is null)
        {
            return null;
        }

        if (key.StartsWith("pa_", StringComparison.OrdinalIgnoreCase))
        {
            return key[3..];
        }

        return NormalizeSlug(key);
    }

    private static string? ResolveAttributeValue(VariationAttribute attribute)
    {
        if (!string.IsNullOrWhiteSpace(attribute.Option))
        {
            return attribute.Option.Trim();
        }

        if (!string.IsNullOrWhiteSpace(attribute.Value))
        {
            return attribute.Value.Trim();
        }

        if (!string.IsNullOrWhiteSpace(attribute.Term))
        {
            return attribute.Term.Trim();
        }

        if (!string.IsNullOrWhiteSpace(attribute.Slug))
        {
            return attribute.Slug.Trim();
        }

        return null;
    }

    private static string? BuildTaxonomyKey(string? slug, string? name)
    {
        return NormalizeSlug(slug) ?? NormalizeSlug(name);
    }

    private static string? NormalizeSlug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        var lower = value.Trim().ToLowerInvariant();
        var lastDash = false;
        foreach (var ch in lower)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastDash = false;
            }
            else
            {
                if (!lastDash)
                {
                    builder.Append('-');
                    lastDash = true;
                }
            }
        }

        var result = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private sealed class TaxRateLookup
    {
        public static TaxRateLookup Empty { get; } = new(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), new Dictionary<int, int>());

        private readonly IReadOnlyDictionary<string, int> _byCode;
        private readonly IReadOnlyDictionary<int, int> _byId;

        public TaxRateLookup(
            IReadOnlyDictionary<string, int> byCode,
            IReadOnlyDictionary<int, int> byId)
        {
            _byCode = byCode ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _byId = byId ?? new Dictionary<int, int>();
        }

        public bool TryMap(WooOrderTaxLine? line, out int mappedId)
        {
            if (line is null)
            {
                mappedId = default;
                return false;
            }

            if (!string.IsNullOrWhiteSpace(line.RateCode)
                && _byCode.TryGetValue(line.RateCode, out mappedId))
            {
                return true;
            }

            if (line.RateId is int sourceId && _byId.TryGetValue(sourceId, out mappedId))
            {
                return true;
            }

            mappedId = default;
            return false;
        }
    }

    private sealed class WooTaxRate
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("postcode")] public string? Postcode { get; set; }
        [JsonPropertyName("city")] public string? City { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }

        public IEnumerable<string> GetCandidateRateCodes()
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                yield break;
            }

            var primary = BuildCode(
                Normalize,
                Normalize,
                NormalizeWildcard,
                NormalizeWildcard,
                Normalize);

            if (!string.IsNullOrWhiteSpace(primary))
            {
                yield return primary;
            }

            if (string.IsNullOrWhiteSpace(Country) || string.IsNullOrWhiteSpace(State))
            {
                var alternate = BuildCode(
                    NormalizeWildcard,
                    NormalizeWildcard,
                    NormalizeWildcard,
                    NormalizeWildcard,
                    Normalize);

                if (!string.IsNullOrWhiteSpace(alternate)
                    && !string.Equals(primary, alternate, StringComparison.Ordinal))
                {
                    yield return alternate;
                }
            }
        }

        private string BuildCode(
            Func<string?, string> countrySelector,
            Func<string?, string> stateSelector,
            Func<string?, string> postcodeSelector,
            Func<string?, string> citySelector,
            Func<string?, string> nameSelector)
        {
            var countrySegment = countrySelector(Country);
            var stateSegment = stateSelector(State);
            var postcodeSegment = postcodeSelector(Postcode);
            var citySegment = citySelector(City);
            var nameSegment = nameSelector(Name);

            if (string.IsNullOrWhiteSpace(nameSegment))
            {
                return string.Empty;
            }

            return string.Join('-', new[]
            {
                countrySegment,
                stateSegment,
                postcodeSegment,
                citySegment,
                nameSegment
            });
        }

        private static string Normalize(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToUpperInvariant();
        }

        private static string NormalizeWildcard(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? "*"
                : value.Trim().ToUpperInvariant();
        }
    }

    private sealed class WooProvisioningException : Exception
    {
        public WooProvisioningException(HttpStatusCode statusCode, string message)
            : base(message)
        {
            StatusCode = statusCode;
        }

        public HttpStatusCode StatusCode { get; }
    }

    private sealed class TaxonomySeed
    {
        public TaxonomySeed(string key, string name, string slug)
        {
            Key = key;
            Name = name;
            Slug = slug;
        }

        public string Key { get; }
        public string Name { get; }
        public string Slug { get; }
        public string? ParentKey { get; set; }
    }

    private sealed class AttributeSeed
    {
        public AttributeSeed(string key, string name, string slug)
        {
            Key = key;
            Name = name;
            Slug = slug;
        }

        public string Key { get; }
        public string Name { get; }
        public string Slug { get; }
        public HashSet<string> Terms { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class WooTaxonomyResponse
    {
        public int Id { get; set; }
        public string? Slug { get; set; }
        public string? Name { get; set; }
    }

    private sealed class WooAttributeResponse
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Slug { get; set; }
    }

    private sealed class WooTermResponse
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Slug { get; set; }
    }

    private sealed class WooProductSummary
    {
        public int Id { get; set; }
        public string? Sku { get; set; }
        public string? Slug { get; set; }
    }

    private sealed class WooVariationResponse
    {
        public int Id { get; set; }
        public string? Sku { get; set; }
        public List<WooVariationAttributeResponse> Attributes { get; set; } = new();
    }

    private sealed class WooVariationAttributeResponse
    {
        public int Id { get; set; }
        public string? Option { get; set; }
    }

    private sealed class WooMediaResponse
    {
        public int Id { get; set; }
    }
}
