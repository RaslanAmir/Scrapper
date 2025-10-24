using System;

namespace WcScraper.Wpf.Models;

public sealed record OnboardingWizardSettings(
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
    bool EnableHttpRetries,
    int HttpRetryAttempts,
    double HttpRetryBaseDelaySeconds,
    double HttpRetryMaxDelaySeconds,
    string? WordPressUsernamePlaceholder,
    string? WordPressApplicationPasswordPlaceholder,
    string? ShopifyStoreUrlPlaceholder,
    string? ShopifyAdminAccessTokenPlaceholder,
    string? ShopifyStorefrontAccessTokenPlaceholder,
    string? ShopifyApiKeyPlaceholder,
    string? ShopifyApiSecretPlaceholder,
    string? Summary)
{
    public OnboardingWizardSettings EnsureValid()
    {
        if (HttpRetryAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(HttpRetryAttempts), "Retry attempts cannot be negative.");
        }

        if (double.IsNaN(HttpRetryBaseDelaySeconds) || double.IsInfinity(HttpRetryBaseDelaySeconds) || HttpRetryBaseDelaySeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(HttpRetryBaseDelaySeconds), "Retry base delay must be a non-negative number.");
        }

        if (double.IsNaN(HttpRetryMaxDelaySeconds) || double.IsInfinity(HttpRetryMaxDelaySeconds) || HttpRetryMaxDelaySeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(HttpRetryMaxDelaySeconds), "Retry max delay must be a non-negative number.");
        }

        return this;
    }
}
