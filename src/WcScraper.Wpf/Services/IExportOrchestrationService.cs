using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WcScraper.Core;
using WcScraper.Core.Exporters;
using WcScraper.Core.Shopify;
using WcScraper.Wpf.Models;

namespace WcScraper.Wpf.Services;

/// <summary>
/// Defines a contract for coordinating export operations that are currently owned by the main view model.
/// </summary>
public interface IExportOrchestrationService
{
    /// <summary>
    /// Executes the export workflow for the provided store.
    /// </summary>
    Task OnRunAsync(ExportRunRequest request, ILogger logger, IProgress<string> progress, CancellationToken cancellationToken);

    /// <summary>
    /// Replicates the last exported store to a target WooCommerce instance.
    /// </summary>
    Task OnReplicateStoreAsync(StoreReplicationRequest request, ILogger logger, IProgress<string> progress, CancellationToken cancellationToken);

    /// <summary>
    /// Loads filter metadata for the requested store so the UI can present selectable collections and tags.
    /// </summary>
    Task<FilterLoadResult> LoadFiltersForStoreAsync(FilterLoadRequest request, ILogger logger, IProgress<string> progress, CancellationToken cancellationToken);

    /// <summary>
    /// Records an operator approval for the supplied run plan.
    /// </summary>
    void OnApproveRunPlan(RunPlan plan);

    /// <summary>
    /// Records an operator dismissal for the supplied run plan.
    /// </summary>
    void OnDismissRunPlan(RunPlan plan);

    /// <summary>
    /// Exports the provided category and tag metadata to spreadsheets for offline review.
    /// </summary>
    void OnExportCollections(CollectionExportRequest request, ILogger logger);

    /// <summary>
    /// Updates the cached AI recommendation summary for the latest run.
    /// </summary>
    void UpdateAiRecommendations(AiArtifactAnnotation? annotation);
}

/// <summary>
/// Represents the information required to orchestrate an export run.
/// </summary>
public sealed record ExportRunRequest(
    PlatformMode Platform,
    string StoreUrl,
    string OutputFolder,
    ExportRunOptions Options,
    ExportCredentialSet Credentials,
    IReadOnlyCollection<TermItem> SelectedCategories,
    IReadOnlyCollection<TermItem> SelectedTags,
    IReadOnlyCollection<string> AdditionalPublicExtensionEntryUrls,
    IReadOnlyCollection<string> AdditionalDesignSnapshotPageUrls,
    IReadOnlyCollection<DesignScreenshotBreakpoint> DesignScreenshotBreakpoints,
    string? ManualRunGoals);

/// <summary>
/// Describes feature and format options for a single export run.
/// </summary>
public sealed record ExportRunOptions(
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
    PublicExtensionLimits PublicExtensionLimits,
    bool ExportPublicDesignSnapshot,
    bool ExportPublicDesignScreenshots,
    bool ExportStoreConfiguration,
    bool ImportStoreConfiguration,
    bool EnableHttpRetries,
    int HttpRetryAttempts,
    TimeSpan HttpRetryBaseDelay,
    TimeSpan HttpRetryMaxDelay);

/// <summary>
/// Represents the parsed limits for public extension scanning.
/// </summary>
public sealed record PublicExtensionLimits(int? PageLimit, long? ByteLimit);

/// <summary>
/// Collection of credentials used during an export workflow.
/// </summary>
public sealed record ExportCredentialSet(
    WordPressCredentials? WordPress,
    ShopifyCredentials? Shopify,
    TargetStoreCredentials? TargetStore);

/// <summary>
/// WordPress authentication information.
/// </summary>
public sealed record WordPressCredentials(string Username, string ApplicationPassword);

/// <summary>
/// Shopify authentication information.
/// </summary>
public sealed record ShopifyCredentials(
    string BaseUrl,
    string? AdminAccessToken,
    string? StorefrontAccessToken,
    string? ApiKey,
    string? ApiSecret);

/// <summary>
/// WooCommerce target store authentication information.
/// </summary>
public sealed record TargetStoreCredentials(string BaseUrl, string ConsumerKey, string ConsumerSecret);

/// <summary>
/// Represents a parsed screenshot breakpoint definition.
/// </summary>
public sealed record DesignScreenshotBreakpoint(string Label, int Width, int Height);

/// <summary>
/// Input required to load store filter metadata.
/// </summary>
public sealed record FilterLoadRequest(PlatformMode Platform, string StoreUrl, ShopifyCredentials? ShopifyCredentials);

/// <summary>
/// Filter metadata returned for a store.
/// </summary>
public sealed record FilterLoadResult(
    IReadOnlyCollection<CollectionExportSelection> Categories,
    IReadOnlyCollection<TermItem> Tags);

/// <summary>
/// Captures information about a collection export candidate.
/// </summary>
public sealed record CollectionExportSelection(TermItem Term, ShopifyCollectionDetails? ShopifyCollection);

/// <summary>
/// Describes the request to export collection metadata to disk.
/// </summary>
public sealed record CollectionExportRequest(
    PlatformMode Platform,
    string StoreUrl,
    string OutputFolder,
    IReadOnlyCollection<CollectionExportSelection> Categories,
    IReadOnlyCollection<TermItem> Tags,
    DateTimeOffset Timestamp);

/// <summary>
/// Represents the data required to replicate an exported store.
/// </summary>
public sealed record StoreReplicationRequest(
    WooProvisioningSettings Settings,
    ProvisioningExportSnapshot Snapshot,
    bool ApplyConfiguration);

/// <summary>
/// Snapshot of the artifacts produced during an export run used for provisioning.
/// </summary>
public sealed record ProvisioningExportSnapshot(
    IReadOnlyCollection<StoreProduct> Products,
    IReadOnlyCollection<StoreProduct> Variations,
    IReadOnlyCollection<ProvisioningVariableProduct> VariableProducts,
    StoreConfiguration? Configuration,
    IReadOnlyCollection<ExtensionArtifact> PluginBundles,
    IReadOnlyCollection<ExtensionArtifact> ThemeBundles,
    IReadOnlyCollection<WooCustomer> Customers,
    IReadOnlyCollection<WooCoupon> Coupons,
    IReadOnlyCollection<WooOrder> Orders,
    IReadOnlyCollection<WooSubscription> Subscriptions,
    WordPressSiteContent? SiteContent,
    IReadOnlyCollection<TermItem> ProductCategories);
