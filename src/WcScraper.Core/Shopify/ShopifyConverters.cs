using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using WcScraper.Core;

namespace WcScraper.Core.Shopify;

public static class ShopifyConverters
{
    private static readonly JsonSerializerOptions DetailSerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null,
        WriteIndented = false
    };

    private static int ToIntId(long id) => unchecked((int)(id % int.MaxValue));

    private static string? ToMinorUnitString(string? price)
    {
        if (string.IsNullOrWhiteSpace(price)) return null;
        if (!decimal.TryParse(price, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
        var minor = (long)Math.Round(value * 100m, MidpointRounding.AwayFromZero);
        return minor.ToString(CultureInfo.InvariantCulture);
    }

    private static IEnumerable<string> SplitTags(IEnumerable<string>? tags)
    {
        if (tags is null)
        {
            yield break;
        }

        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag)) continue;

            var parts = tag.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            foreach (var part in parts)
            {
                if (!string.IsNullOrWhiteSpace(part))
                {
                    yield return part;
                }
            }
        }
    }

    private static int ParseCollectionId(string? gid)
    {
        if (string.IsNullOrWhiteSpace(gid)) return 0;
        var idx = gid.LastIndexOf('/');
        if (idx >= 0 && idx < gid.Length - 1 && int.TryParse(gid[(idx + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            return numeric;
        }

        return Math.Abs(gid.GetHashCode());
    }

    private static IEnumerable<ProductTag> ConvertTags(IEnumerable<string>? tags)
    {
        var index = 1;
        foreach (var tag in SplitTags(tags))
        {
            yield return new ProductTag
            {
                Id = index++,
                Name = tag,
                Slug = tag.Replace(' ', '-').ToLowerInvariant()
            };
        }
    }

    private static IEnumerable<Category> ConvertCollections(IEnumerable<ShopifyCollection> collections)
    {
        var index = 1;
        foreach (var collection in collections)
        {
            yield return new Category
            {
                Id = ParseCollectionId(collection.Id),
                Name = collection.Title,
                Slug = string.IsNullOrWhiteSpace(collection.Handle)
                    ? $"collection-{index++}"
                    : collection.Handle
            };
        }
    }

    private static IEnumerable<ProductImage> ConvertImages(IEnumerable<ShopifyImage> images)
    {
        var index = 1;
        foreach (var image in images)
        {
            yield return new ProductImage
            {
                Id = image.Id != 0 ? ToIntId(image.Id) : index++,
                Src = image.Src,
                Alt = image.Alt
            };
        }
    }

    private static IEnumerable<VariationAttribute> ConvertOptions(IEnumerable<ShopifyOption> options)
    {
        foreach (var option in options)
        {
            if (string.IsNullOrWhiteSpace(option.Name)) continue;
            yield return new VariationAttribute
            {
                Name = option.Name,
                Option = string.Join(", ", option.Values)
            };
        }
    }

    private static PriceInfo BuildPriceInfo(ShopifyVariant? variant)
    {
        if (variant is null)
        {
            return new PriceInfo();
        }

        return new PriceInfo
        {
            CurrencyMinorUnit = 2,
            Price = ToMinorUnitString(variant.Price),
            RegularPrice = ToMinorUnitString(variant.CompareAtPrice),
            SalePrice = ToMinorUnitString(variant.Price)
        };
    }

    public static StoreProduct ToStoreProduct(ShopifyProduct product, ShopifySettings settings)
    {
        var firstVariant = product.Variants.FirstOrDefault();
        var inStock = firstVariant?.InventoryQuantity is > 0;
        var hasOptions = product.Options.Count > 1 || product.Variants.Count > 1;

        var categories = ConvertCollections(product.Collections).ToList();
        if (categories.Count == 0 && !string.IsNullOrWhiteSpace(product.ProductType))
        {
            var fallbackSlug = ShopifySlugHelper.Slugify(product.ProductType)
                ?? $"category-{Math.Abs(product.ProductType.GetHashCode())}";
            categories.Add(new Category
            {
                Id = Math.Abs(product.ProductType.GetHashCode()),
                Name = product.ProductType,
                Slug = fallbackSlug
            });
        }

        var keywordCandidates = SplitTags(product.Tags).ToList();
        var metaKeywords = keywordCandidates.Count == 0
            ? null
            : string.Join(", ", keywordCandidates);

        return new StoreProduct
        {
            Id = ToIntId(product.Id),
            Name = product.Title,
            Description = product.BodyHtml,
            ShortDescription = product.BodyHtml,
            Summary = product.BodyHtml,
            Permalink = string.IsNullOrWhiteSpace(product.Handle)
                ? null
                : $"{settings.BaseUrl}/products/{product.Handle}",
            Sku = firstVariant?.Sku,
            Type = product.ProductType,
            Vendor = product.Vendor,
            Slug = product.Handle,
            Prices = BuildPriceInfo(firstVariant),
            IsInStock = inStock,
            HasOptions = hasOptions,
            Tags = ConvertTags(product.Tags).ToList(),
            Categories = categories,
            Images = ConvertImages(product.Images).ToList(),
            Attributes = ConvertOptions(product.Options).ToList(),
            MetaTitle = product.MetafieldsGlobalTitleTag,
            MetaDescription = product.MetafieldsGlobalDescriptionTag,
            MetaKeywords = metaKeywords
        };
    }

    private static string? SerializeIfAny<T>(IEnumerable<T>? values)
    {
        if (values is null)
        {
            return null;
        }

        var materialized = values.ToList();
        return materialized.Count == 0 ? null : JsonSerializer.Serialize(materialized, DetailSerializerOptions);
    }

    public static Dictionary<string, object?> ToShopifyDetailDictionary(ShopifyProduct product, StoreProduct? storeProduct = null)
    {
        var tagList = product.Tags ?? new List<string>();
        var collections = product.Collections ?? new List<ShopifyCollection>();
        var collectionHandles = collections
            .Select(c => c.Handle)
            .Where(handle => !string.IsNullOrWhiteSpace(handle))
            .Select(handle => handle!.Trim())
            .Where(handle => handle.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var metaTitle = storeProduct?.MetaTitle ?? product.MetafieldsGlobalTitleTag;
        var metaDescription = storeProduct?.MetaDescription ?? product.MetafieldsGlobalDescriptionTag;
        var metaKeywords = storeProduct?.MetaKeywords;

        var dict = new Dictionary<string, object?>
        {
            ["id"] = product.Id,
            ["title"] = product.Title,
            ["body_html"] = product.BodyHtml,
            ["vendor"] = product.Vendor,
            ["product_type"] = product.ProductType,
            ["handle"] = product.Handle,
            ["status"] = product.Status,
            ["created_at"] = product.CreatedAt,
            ["updated_at"] = product.UpdatedAt,
            ["published_at"] = product.PublishedAt,
            ["template_suffix"] = product.TemplateSuffix,
            ["published_scope"] = product.PublishedScope,
            ["tags"] = tagList.Count == 0 ? null : string.Join(", ", tagList),
            ["tags_json"] = tagList.Count == 0 ? null : JsonSerializer.Serialize(tagList, DetailSerializerOptions),
            ["admin_graphql_api_id"] = product.AdminGraphqlApiId,
            ["metafields_global_title_tag"] = product.MetafieldsGlobalTitleTag,
            ["metafields_global_description_tag"] = product.MetafieldsGlobalDescriptionTag,
            ["meta_title"] = metaTitle,
            ["meta_description"] = metaDescription,
            ["meta_keywords"] = metaKeywords,
            ["options_json"] = SerializeIfAny(product.Options),
            ["variants_json"] = SerializeIfAny(product.Variants),
            ["images_json"] = SerializeIfAny(product.Images),
            ["image_json"] = product.Image is null ? null : JsonSerializer.Serialize(product.Image, DetailSerializerOptions),
            ["collection_handles"] = collectionHandles.Count == 0 ? null : string.Join(", ", collectionHandles),
            ["collection_handles_json"] = collectionHandles.Count == 0 ? null : JsonSerializer.Serialize(collectionHandles, DetailSerializerOptions),
            ["collections_json"] = SerializeIfAny(collections)
        };

        return dict;
    }
}
