using System;
using System.Collections.Generic;
namespace WcScraper.Wpf.Models;

public sealed record AiArtifactIntelligencePayload(
    string StoreUrl,
    IReadOnlyList<AiPublicExtensionInsight> PublicExtensions,
    AiPublicExtensionCrawlContext? Crawl,
    AiDesignSnapshotInsight? Design,
    IReadOnlyList<AiIndexedDatasetReference> IndexedDatasets)
{
    public bool HasContent =>
        (PublicExtensions is { Count: > 0 }) ||
        (Design?.HasContent ?? false) ||
        (IndexedDatasets is { Count: > 0 });
}

public sealed record AiIndexedDatasetReference(
    string Name,
    IReadOnlyList<string> SchemaHighlights,
    string VectorIndexIdentifier);

public sealed record AiPublicExtensionInsight(
    string Slug,
    string Type,
    string? VersionHint,
    string? WordPressVersion,
    string? WooCommerceVersion,
    IReadOnlyList<string> SourceUrls,
    string? AssetUrl,
    string? DirectoryTitle,
    string? DirectoryAuthor,
    string? DirectoryVersion,
    string? DirectoryDownloadUrl,
    string? DirectoryStatus);

public sealed record AiPublicExtensionCrawlContext(
    int? MaxPages,
    long? MaxBytes,
    int ScheduledPageCount,
    int ProcessedPageCount,
    long TotalBytesDownloaded,
    bool PageLimitReached,
    bool ByteLimitReached,
    IReadOnlyList<string> AdditionalEntryUrls,
    string? WordPressVersion,
    string? WooCommerceVersion);

public sealed record AiDesignSnapshotInsight(
    IReadOnlyList<AiDesignAssetReference> Assets,
    IReadOnlyList<AiColorSwatch> ColorPalette,
    string? ManifestJsonPath,
    string? ManifestCsvPath)
{
    public bool HasContent =>
        (Assets is { Count: > 0 }) ||
        (ColorPalette is { Count: > 0 }) ||
        !string.IsNullOrWhiteSpace(ManifestJsonPath) ||
        !string.IsNullOrWhiteSpace(ManifestCsvPath);
}

public sealed record AiDesignAssetReference(
    string Type,
    string File,
    string Sha256,
    string? SourceUrl,
    string? ResolvedUrl,
    string? ReferencedFrom,
    string? ContentType,
    long FileSizeBytes,
    IReadOnlyList<string>? Origins,
    IReadOnlyList<string>? References);

public sealed record AiColorSwatch(string Value, int Count);

public sealed record AiArtifactAnnotation(
    DateTimeOffset GeneratedAt,
    string MarkdownSummary,
    IReadOnlyList<AiRecommendation> Recommendations)
{
    public bool HasRecommendations => Recommendations is { Count: > 0 };
}

public sealed record AiRecommendation(
    string Title,
    string Summary,
    IReadOnlyList<string> SuggestedPrompts,
    IReadOnlyList<string>? RelatedAssets)
{
    public bool HasRelatedAssets => RelatedAssets is { Count: > 0 };
    public bool HasSuggestedPrompts => SuggestedPrompts is { Count: > 0 };
}
