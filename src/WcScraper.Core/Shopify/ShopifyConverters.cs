using System.Globalization;

namespace WcScraper.Core.Shopify;

public static class ShopifyConverters
{
    private static int ToIntId(long id) => unchecked((int)(id % int.MaxValue));

    private static string? ToMinorUnitString(string? price)
    {
        if (string.IsNullOrWhiteSpace(price)) return null;
        if (!decimal.TryParse(price, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
        var minor = (long)Math.Round(value * 100m, MidpointRounding.AwayFromZero);
        return minor.ToString(CultureInfo.InvariantCulture);
    }

    private static IEnumerable<string> SplitTags(string? tags)
        => string.IsNullOrWhiteSpace(tags)
            ? Enumerable.Empty<string>()
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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

    private static IEnumerable<ProductTag> ConvertTags(string? tags)
        => SplitTags(tags).Select((t, i) => new ProductTag
        {
            Id = i + 1,
            Name = t,
            Slug = t.Replace(' ', '-').ToLowerInvariant()
        });

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
        var inStock = true;

        if (firstVariant is not null)
        {
            if (firstVariant.InventoryQuantity is int quantity)
            {
                inStock = quantity > 0;
            }
            else if (firstVariant.Available is bool available)
            {
                inStock = available;
            }
        }

        var hasOptions = product.Options.Count > 1 || product.Variants.Count > 1;

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
            Categories = ConvertCollections(product.Collections).ToList(),
            Images = ConvertImages(product.Images).ToList(),
            Attributes = ConvertOptions(product.Options).ToList()
        };
    }
}
