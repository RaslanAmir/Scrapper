using System.Text.Json.Serialization;

namespace WcScraper.Core.Shopify;

public sealed class ShopifySettings
{
    private string _baseUrl = string.Empty;

    public ShopifySettings()
    {
        ApiVersion = "2024-01";
        PageSize = 250;
        MaxPages = 10;
    }

    public ShopifySettings(
        string baseUrl,
        string? adminAccessToken = null,
        string? storefrontAccessToken = null,
        string? apiKey = null,
        string? apiSecret = null)
        : this()
    {
        BaseUrl = baseUrl;
        AdminAccessToken = adminAccessToken;
        StorefrontAccessToken = storefrontAccessToken;
        ApiKey = apiKey;
        ApiSecret = apiSecret;
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set => _baseUrl = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().TrimEnd('/');
    }

    public string ApiVersion { get; set; }

    public string? AdminAccessToken { get; set; }

    public string? StorefrontAccessToken { get; set; }

    public string? ApiKey { get; set; }

    public string? ApiSecret { get; set; }

    public int PageSize { get; set; }

    public int MaxPages { get; set; }

    [JsonIgnore]
    public bool HasAdminAccess => !string.IsNullOrWhiteSpace(AdminAccessToken);

    [JsonIgnore]
    public bool HasStorefrontAccess => !string.IsNullOrWhiteSpace(StorefrontAccessToken);

    [JsonIgnore]
    public bool HasPrivateAppCredentials =>
        !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);

    internal Uri BuildRestUri(string path, string? query)
        => new($"{BaseUrl}/admin/api/{ApiVersion}/{path}{(string.IsNullOrWhiteSpace(query) ? string.Empty : $"?{query}")}", UriKind.Absolute);

    internal Uri BuildPublicProductsUri(int pageSize, int page)
        => new($"{BaseUrl}/products.json?limit={pageSize}&page={page}", UriKind.Absolute);

    internal Uri BuildPublicCollectionsUri(int pageSize, int page)
        => new($"{BaseUrl}/collections.json?limit={pageSize}&page={page}", UriKind.Absolute);

    internal Uri BuildGraphUri()
        => new($"{BaseUrl}/api/{ApiVersion}/graphql.json", UriKind.Absolute);
}
