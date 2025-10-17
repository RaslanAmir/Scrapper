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
    public WooProvisioningSettings(string baseUrl, string consumerKey, string consumerSecret)
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
    }

    public string BaseUrl { get; }
    public string ConsumerKey { get; }
    public string ConsumerSecret { get; }
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
        StoreConfiguration? configuration = null,
        IEnumerable<WooCustomer>? customers = null,
        IEnumerable<WooCoupon>? coupons = null,
        IEnumerable<WooOrder>? orders = null,
        IEnumerable<WooSubscription>? subscriptions = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        if (products is null) throw new ArgumentNullException(nameof(products));

        var productList = products.Where(p => p is not null).ToList();
        var customerList = customers?.Where(c => c is not null).ToList() ?? new List<WooCustomer>();
        var couponList = coupons?.Where(c => c is not null).ToList() ?? new List<WooCoupon>();
        var orderList = orders?.Where(o => o is not null).ToList() ?? new List<WooOrder>();
        var subscriptionList = subscriptions?.Where(s => s is not null).ToList() ?? new List<WooSubscription>();

        if (productList.Count == 0 && customerList.Count == 0 && couponList.Count == 0 && orderList.Count == 0 && subscriptionList.Count == 0)
        {
            progress?.Report("No artifacts to provision.");
            return;
        }

        var baseUrl = settings.BaseUrl;

        if (configuration is not null)
        {
            await ApplyStoreConfigurationAsync(baseUrl, settings, configuration, progress, cancellationToken);
        }

        var productIdMap = new Dictionary<int, int>();
        var customerIdMap = new Dictionary<int, int>();
        var couponIdMap = new Dictionary<int, int>();
        var couponCodeMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var categoryTaxonomyMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var categoryIdMap = new Dictionary<int, int>();

        if (productList.Count > 0)
        {
            progress?.Report($"Preparing taxonomies for {productList.Count} products…");
            var categorySeeds = CollectTaxonomySeeds(productList.SelectMany(p => p.Categories ?? Enumerable.Empty<Category>()), c => c.Name, c => c.Slug);
            var tagSeeds = CollectTaxonomySeeds(productList.SelectMany(p => p.Tags ?? Enumerable.Empty<ProductTag>()), t => t.Name, t => t.Slug);
            var attributeSeeds = CollectAttributeSeeds(productList);

            categoryTaxonomyMap = await EnsureTaxonomiesAsync(baseUrl, settings, "categories", categorySeeds, progress, cancellationToken);
            var tagMap = await EnsureTaxonomiesAsync(baseUrl, settings, "tags", tagSeeds, progress, cancellationToken);
            var attributeMap = await EnsureAttributesAsync(baseUrl, settings, attributeSeeds, progress, cancellationToken);

            progress?.Report("Provisioning products…");
            var mediaCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var product in productList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ensuredId = await EnsureProductAsync(baseUrl, settings, product, categoryTaxonomyMap, tagMap, attributeMap, mediaCache, progress, cancellationToken);
                if (product.Id > 0)
                {
                    productIdMap[product.Id] = ensuredId;
                }
            }

            categoryIdMap = BuildCategoryIdMap(productList, categoryTaxonomyMap);
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

        if (orderList.Count > 0)
        {
            await EnsureOrdersAsync(baseUrl, settings, orderList, productIdMap, customerIdMap, couponCodeMap, progress, cancellationToken);
        }

        if (subscriptionList.Count > 0)
        {
            progress?.Report($"Skipping {subscriptionList.Count} subscriptions (provisioning not implemented).");
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

        foreach (var endpoint in endpoints)
        {
            var uploaded = await TryUploadBundleAsync(settings, bundle, scope, endpoint, archivePath, manifestPath, optionsPath, progress, cancellationToken);
            if (uploaded)
            {
                return;
            }
        }

        progress?.Report($"No {scope} upload endpoint accepted '{bundle.Slug}'.");
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

    private async Task<int> EnsureProductAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        StoreProduct product,
        IReadOnlyDictionary<string, int> categoryMap,
        IReadOnlyDictionary<string, int> tagMap,
        IReadOnlyDictionary<string, int> attributeMap,
        Dictionary<string, int> mediaCache,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var existing = await FindExistingProductAsync(baseUrl, settings, product, cancellationToken);

        var categoryRefs = BuildTaxonomyReferences(product.Categories, categoryMap);
        var tagRefs = BuildTaxonomyReferences(product.Tags, tagMap);
        var attributePayload = BuildAttributePayload(product, attributeMap);
        var imagePayload = await BuildImagePayloadAsync(baseUrl, settings, product, mediaCache, progress, cancellationToken);

        var payload = new Dictionary<string, object?>();
        AddIfValue(payload, "name", product.Name);
        AddIfValue(payload, "slug", NormalizeSlug(product.Slug));
        AddIfValue(payload, "type", string.IsNullOrWhiteSpace(product.Type) ? "simple" : product.Type);
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

        var categoryIds = MapIds(coupon.ProductCategories, categoryMap);
        if (categoryIds.Count > 0)
        {
            payload["product_categories"] = categoryIds;
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

    private async Task EnsureOrdersAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        List<WooOrder> orders,
        IReadOnlyDictionary<int, int> productMap,
        IReadOnlyDictionary<int, int> customerMap,
        IReadOnlyDictionary<string, int> couponCodeMap,
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

            var payload = BuildOrderPayload(order, productMap, customerMap, couponCodeMap, lineItems);
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
        List<Dictionary<string, object?>> lineItems)
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

        var taxLines = BuildTaxLines(order.TaxLines);
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

    private static List<Dictionary<string, object?>> BuildTaxLines(IEnumerable<WooOrderTaxLine> lines)
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
            AddIfValue(entry, "rate_id", line.RateId);
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
        return status is "completed" or "processing" or "on-hold";
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
                    images.Add(new Dictionary<string, object?> { ["id"] = cachedId });
                    continue;
                }

                var uploaded = await UploadMediaAsync(baseUrl, settings, path, progress, cancellationToken);
                if (uploaded.HasValue)
                {
                    mediaCache[path] = uploaded.Value;
                    images.Add(new Dictionary<string, object?> { ["id"] = uploaded.Value });
                }
            }
        }

        if (images.Count == 0 && product.Images is { Count: > 0 })
        {
            foreach (var img in product.Images)
            {
                if (string.IsNullOrWhiteSpace(img.Src))
                {
                    continue;
                }

                images.Add(new Dictionary<string, object?>
                {
                    ["src"] = img.Src,
                    ["alt"] = img.Alt
                });
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
            request.Headers.Authorization = CreateAuthHeader(settings);
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
            if (zonePayload.Count > 0)
            {
                progress?.Report($"Updating shipping zone '{zoneLabel}'…");
                await PutAsync<ShippingZoneSetting>(baseUrl, settings, $"/wp-json/wc/v3/shipping/zones/{zone.Id}", zonePayload, cancellationToken);
            }

            if (zone.Locations is { Count: > 0 })
            {
                progress?.Report($"Updating shipping zone '{zoneLabel}' locations…");
                await PutAsync<List<ShippingZoneLocation>>(baseUrl, settings, $"/wp-json/wc/v3/shipping/zones/{zone.Id}/locations", zone.Locations, cancellationToken);
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
                            await PutAsync<ShippingZoneMethodSetting>(baseUrl, settings, $"/wp-json/wc/v3/shipping/zones/{zone.Id}/methods/{method.InstanceId}", methodPayload, cancellationToken);
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
                        await PostAsync<ShippingZoneMethodSetting>(baseUrl, settings, $"/wp-json/wc/v3/shipping/zones/{zone.Id}/methods", createPayload, cancellationToken);
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
        foreach (var seed in seeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = await EnsureTaxonomyAsync(baseUrl, settings, resource, seed, progress, cancellationToken);
            result[seed.Key] = id;
        }

        return result;
    }

    private async Task<int> EnsureTaxonomyAsync(
        string baseUrl,
        WooProvisioningSettings settings,
        string resource,
        TaxonomySeed seed,
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

    private static AuthenticationHeaderValue CreateAuthHeader(WooProvisioningSettings settings)
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.ConsumerKey}:{settings.ConsumerSecret}"));
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

    private static Dictionary<int, int> BuildCategoryIdMap(IEnumerable<StoreProduct> products, IReadOnlyDictionary<string, int> taxonomyMap)
    {
        var result = new Dictionary<int, int>();
        foreach (var category in products
                     .Where(p => p is not null)
                     .SelectMany(p => p.Categories ?? Enumerable.Empty<Category>()))
        {
            if (category is null || category.Id <= 0)
            {
                continue;
            }

            var key = BuildTaxonomyKey(category.Slug, category.Name);
            if (key is not null && taxonomyMap.TryGetValue(key, out var mappedId))
            {
                result[category.Id] = mappedId;
            }
        }

        return result;
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

    private static List<TaxonomySeed> CollectTaxonomySeeds<T>(IEnumerable<T> items, Func<T, string?> getName, Func<T, string?> getSlug)
    {
        var result = new Dictionary<string, TaxonomySeed>(StringComparer.OrdinalIgnoreCase);
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
                seed = new TaxonomySeed(normalizedSlug, name ?? slug ?? normalizedSlug, normalizedSlug);
                result[normalizedSlug] = seed;
            }
        }

        return result.Values.ToList();
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

    private sealed class WooProvisioningException : Exception
    {
        public WooProvisioningException(HttpStatusCode statusCode, string message)
            : base(message)
        {
            StatusCode = statusCode;
        }

        public HttpStatusCode StatusCode { get; }
    }

    private sealed record TaxonomySeed(string Key, string Name, string Slug);

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

    private sealed class WooMediaResponse
    {
        public int Id { get; set; }
    }
}
