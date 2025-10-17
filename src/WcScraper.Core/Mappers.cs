using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace WcScraper.Core;

public static class Mappers
{
    private static double? AsFloatPrice(string? minor, int? minorUnit)
    {
        if (string.IsNullOrWhiteSpace(minor) || minorUnit is null) return null;
        if (!long.TryParse(minor, out var val)) return null;
        var div = Math.Pow(10, minorUnit.Value);
        return val / div;
    }

    private static string JoinCsv(IEnumerable<string?> items)
        => string.Join(", ", items.Where(s => !string.IsNullOrWhiteSpace(s))!);

    public static IEnumerable<GenericRow> ToGenericRows(IEnumerable<StoreProduct> products)
    {
        foreach (var p in products)
        {
            var prices = p.Prices;
            var priceVal = prices?.Price ?? prices?.RegularPrice;
            var categoryNames = p.Categories.Select(c => c.Name).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            var categorySlugs = p.Categories.Select(c => c.Slug).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            var tagNames = p.Tags.Select(t => t.Name).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            var tagSlugs = p.Tags.Select(t => t.Slug).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            var images = p.Images ?? new List<ProductImage>();
            yield return new GenericRow
            {
                Id = p.Id,
                Name = p.Name,
                Slug = p.Slug,
                Permalink = p.Permalink,
                Sku = p.Sku,
                Type = p.Type ?? (p.Type is null ? "simple" : p.Type),
                DescriptionHtml = p.Description,
                ShortDescriptionHtml = !string.IsNullOrWhiteSpace(p.ShortDescription) ? p.ShortDescription : p.Summary,
                SummaryHtml = p.Summary,
                MetaTitle = p.MetaTitle,
                MetaDescription = p.MetaDescription,
                MetaKeywords = p.MetaKeywords,
                RegularPrice = AsFloatPrice(prices?.RegularPrice, prices?.CurrencyMinorUnit),
                SalePrice = AsFloatPrice(prices?.SalePrice, prices?.CurrencyMinorUnit),
                Price = AsFloatPrice(priceVal, prices?.CurrencyMinorUnit),
                Currency = prices?.CurrencyCode,
                InStock = p.IsInStock,
                StockStatus = p.StockStatus,
                AverageRating = p.AverageRating,
                ReviewCount = p.ReviewCount,
                HasOptions = p.HasOptions,
                ParentId = p.ParentId,
                Categories = JoinCsv(categoryNames),
                CategorySlugs = JoinCsv(categorySlugs),
                Tags = JoinCsv(tagNames),
                TagSlugs = JoinCsv(tagSlugs),
                Images = JoinCsv(images.Select(i => i.Src)),
                ImageAlts = JoinCsv(images.Select(i => i.Alt)),
                ImageFilePaths = p.ImageFilePaths
            };
        }
    }

    private static string DomainAsVendor(string baseUrl)
    {
        try
        {
            var host = new Uri(baseUrl).Host;
            return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        }
        catch
        {
            return "";
        }
    }

    public static IEnumerable<Dictionary<string, object?>> ToShopifyCsv(IEnumerable<StoreProduct> products, string baseUrl)
    {
        var vendor = DomainAsVendor(baseUrl);
        foreach (var p in products)
        {
            var prices = p.Prices;
            var priceVal = prices?.Price ?? prices?.RegularPrice;
            var price = AsFloatPrice(priceVal, prices?.CurrencyMinorUnit);
            var imageSrc = p.Images.FirstOrDefault()?.Src ?? "";
            var categoryNames = p.Categories.Select(c => c.Name).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            var categories = JoinCsv(categoryNames);
            var primaryCategory = categoryNames.FirstOrDefault() ?? "";
            var tags = JoinCsv(p.Tags.Select(t => t.Name));

            yield return new Dictionary<string, object?>
            {
                ["Handle"] = string.IsNullOrWhiteSpace(p.Slug) ? $"prod-{p.Id}" : p.Slug,
                ["Title"] = p.Name,
                ["Body (HTML)"] = p.Description ?? p.ShortDescription ?? p.Summary,
                ["Vendor"] = vendor,
                ["Product Category"] = categories,
                ["Type"] = primaryCategory,
                ["Tags"] = tags,
                ["SEO Title"] = p.MetaTitle ?? "",
                ["SEO Description"] = p.MetaDescription ?? "",
                ["SEO Keywords"] = p.MetaKeywords ?? "",
                ["Published"] = "TRUE",
                ["Option1 Name"] = "Title",
                ["Option1 Value"] = "Default Title",
                ["Variant SKU"] = p.Sku ?? "",
                ["Variant Price"] = price is null ? "" : price.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                ["Variant Inventory Qty"] = "",
                ["Variant Requires Shipping"] = "TRUE",
                ["Variant Taxable"] = "TRUE",
                ["Variant Weight Unit"] = "kg",
                ["Image Src"] = imageSrc,
                ["Image Src Local"] = p.ImageFilePaths ?? ""
            };
        }
    }

    public static IEnumerable<Dictionary<string, object?>> ToWooImporterCsv(
        IEnumerable<StoreProduct> parents,
        IEnumerable<StoreProduct> variations)
    {
        var parentList = parents?.Where(p => p is not null).Select(p => p!).ToList() ?? new List<StoreProduct>();
        var variationList = variations?.Where(v => v is not null).Select(v => v!).ToList() ?? new List<StoreProduct>();

        var parentLookup = parentList
            .Where(p => p.Id > 0)
            .ToDictionary(p => p.Id, p => p);

        var groupedVariations = variationList
            .Where(v => v.ParentId is int id && id > 0)
            .GroupBy(v => v.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var remainingVariations = new List<StoreProduct>(variationList);

        foreach (var parent in parentList)
        {
            var hasChildren = groupedVariations.TryGetValue(parent.Id, out var children) && children.Count > 0;
            var parentType = !string.IsNullOrWhiteSpace(parent.Type)
                ? parent.Type
                : hasChildren ? "variable" : "simple";

            yield return BuildWooRow(parent, parentType, parent.ParentId, null);

            if (!hasChildren)
            {
                continue;
            }

            foreach (var child in children)
            {
                remainingVariations.Remove(child);
                var parentId = child.ParentId ?? parent.Id;
                var variationType = string.IsNullOrWhiteSpace(child.Type) ? "variation" : child.Type!;
                yield return BuildWooRow(child, variationType, parentId, parent);
            }
        }

        foreach (var leftover in remainingVariations)
        {
            StoreProduct? fallbackParent = null;
            if (leftover.ParentId is int parentId && parentLookup.TryGetValue(parentId, out var parent))
            {
                fallbackParent = parent;
            }

            var type = string.IsNullOrWhiteSpace(leftover.Type)
                ? (leftover.ParentId is null ? "simple" : "variation")
                : leftover.Type!;

            yield return BuildWooRow(leftover, type, leftover.ParentId, fallbackParent);
        }
    }

    private static Dictionary<string, object?> BuildWooRow(
        StoreProduct product,
        string? productType,
        int? parentId,
        StoreProduct? fallback)
    {
        var priceInfo = product.Prices ?? fallback?.Prices;
        var regularRaw = product.Prices?.RegularPrice ?? fallback?.Prices?.RegularPrice;
        var saleRaw = product.Prices?.SalePrice ?? fallback?.Prices?.SalePrice;
        var priceRaw = product.Prices?.Price
            ?? product.Prices?.RegularPrice
            ?? product.Prices?.SalePrice
            ?? fallback?.Prices?.Price
            ?? fallback?.Prices?.RegularPrice
            ?? fallback?.Prices?.SalePrice;

        var regular = AsFloatPrice(regularRaw, priceInfo?.CurrencyMinorUnit);
        var sale = AsFloatPrice(saleRaw, priceInfo?.CurrencyMinorUnit);
        var price = AsFloatPrice(priceRaw, priceInfo?.CurrencyMinorUnit);

        var resolvedType = string.IsNullOrWhiteSpace(productType) ? "simple" : productType;

        var categoriesSource = product.Categories.Count > 0
            ? product.Categories
            : fallback?.Categories ?? Enumerable.Empty<Category>();
        var tagsSource = product.Tags.Count > 0
            ? product.Tags
            : fallback?.Tags ?? Enumerable.Empty<ProductTag>();
        var imagesSource = product.Images.Count > 0
            ? product.Images
            : fallback?.Images ?? Enumerable.Empty<ProductImage>();
        var attributesSource = product.Attributes.Count > 0
            ? product.Attributes
            : fallback?.Attributes ?? Enumerable.Empty<VariationAttribute>();

        var categories = JoinCsv(categoriesSource.Select(c => c.Name));
        var tags = JoinCsv(tagsSource.Select(t => t.Name));
        var images = JoinCsv(imagesSource.Select(i => i.Src));
        var attributes = BuildAttributesString(attributesSource);

        var imageFilePaths = string.IsNullOrWhiteSpace(product.ImageFilePaths)
            ? fallback?.ImageFilePaths ?? ""
            : product.ImageFilePaths;

        var shortDescription = FirstNonEmpty(
            product.ShortDescription,
            product.Summary,
            fallback?.ShortDescription,
            fallback?.Summary) ?? "";

        var description = FirstNonEmpty(product.Description, fallback?.Description) ?? "";

        var seoTitle = FirstNonEmpty(product.MetaTitle, fallback?.MetaTitle) ?? "";
        var seoDescription = FirstNonEmpty(product.MetaDescription, fallback?.MetaDescription) ?? "";
        var seoKeywords = FirstNonEmpty(product.MetaKeywords, fallback?.MetaKeywords) ?? "";

        var stockStatus = FirstNonEmpty(product.StockStatus, fallback?.StockStatus) ?? "";
        var inStock = (product.IsInStock ?? fallback?.IsInStock) == true ? "1" : "0";

        return new Dictionary<string, object?>
        {
            ["ID"] = "",
            ["Type"] = resolvedType,
            ["ParentId"] = parentId,
            ["SKU"] = string.IsNullOrWhiteSpace(product.Sku) ? fallback?.Sku ?? "" : product.Sku,
            ["Name"] = FirstNonEmpty(product.Name, fallback?.Name) ?? "",
            ["Published"] = 1,
            ["Is featured?"] = 0,
            ["Visibility in catalog"] = "visible",
            ["Short description"] = shortDescription,
            ["Description"] = description,
            ["SEO Title"] = seoTitle,
            ["SEO Description"] = seoDescription,
            ["SEO Keywords"] = seoKeywords,
            ["Tax status"] = "taxable",
            ["Regular price"] = regular,
            ["Sale price"] = sale,
            ["Price"] = price,
            ["Currency"] = priceInfo?.CurrencyCode ?? "",
            ["In stock?"] = inStock,
            ["Stock status"] = stockStatus,
            ["Categories"] = categories,
            ["Tags"] = tags,
            ["Images"] = images,
            ["Image File Paths"] = imageFilePaths,
            ["Attributes"] = attributes,
            ["Position"] = 0
        };
    }

    private static string BuildAttributesString(IEnumerable<VariationAttribute> attributes)
    {
        var pairs = attributes
            .Select(ExtractAttr)
            .Where(p => !string.IsNullOrWhiteSpace(p.name) && !string.IsNullOrWhiteSpace(p.value))
            .Select(p => $"{p.name}: {p.value}")
            .ToList();

        return pairs.Count == 0 ? "" : string.Join(" | ", pairs);
    }

    private static string? FirstNonEmpty(params string?[] vals)
        => vals.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static (string? name, string? value) ExtractAttr(VariationAttribute a)
    {
        var name = FirstNonEmpty(a.Name, a.Taxonomy, a.AttributeKey);
        var val = FirstNonEmpty(a.Option, a.Value, a.Term, a.Slug);
        if (!string.IsNullOrWhiteSpace(name))
        {
            // Clean "pa_color" -> "Color"
            if (name.StartsWith("pa_", StringComparison.OrdinalIgnoreCase))
                name = name[3..].Replace("_", " ").Trim();
            name = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name!);
        }
        return (name, val);
    }

    public static IEnumerable<Dictionary<string, object?>> ToShopifyCsvWithVariants(
        IEnumerable<StoreProduct> parents,
        IEnumerable<StoreProduct> variations,
        string baseUrl)
    {
        var vendor = DomainAsVendor(baseUrl);
        var byParent = variations.Where(v => v.ParentId is not null)
            .GroupBy(v => v.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var p in parents)
        {
            var categoryNames = p.Categories.Select(c => c.Name).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            var categories = JoinCsv(categoryNames);
            var primaryCategory = categoryNames.FirstOrDefault() ?? "";
            var tags = JoinCsv(p.Tags.Select(t => t.Name));
            if (!byParent.TryGetValue(p.Id, out var vars) || vars.Count == 0)
            {
                // No variations -> fallback to single-row
                foreach (var row in ToShopifyCsv(new[] { p }, baseUrl)) yield return row;
                continue;
            }

            // Determine up to 3 option names (consistent across this parent)
            var optionNames = new List<string?>();
            foreach (var v in vars)
            {
                var pairs = v.Attributes
                    .Select(ExtractAttr)
                    .Where(t => !string.IsNullOrWhiteSpace(t.name) && !string.IsNullOrWhiteSpace(t.value))
                    .ToList();
                foreach (var (n, _) in pairs)
                {
                    if (string.IsNullOrWhiteSpace(n)) continue;
                    if (!optionNames.Contains(n) && optionNames.Count < 3) optionNames.Add(n);
                }
                if (optionNames.Count >= 3) break;
            }
            while (optionNames.Count < 3) optionNames.Add(null);

            foreach (var v in vars)
            {
                var pairs = v.Attributes.Select(ExtractAttr).Where(t => !string.IsNullOrWhiteSpace(t.name)).ToList();
                string? GetOpt(string? name) => pairs
                    .FirstOrDefault(t => string.Equals(t.name, name, StringComparison.OrdinalIgnoreCase))
                    .value;

                var prices = v.Prices ?? p.Prices;
                var priceVal = prices?.Price ?? prices?.RegularPrice;
                var price = AsFloatPrice(priceVal, prices?.CurrencyMinorUnit);

                var imageSrc = v.Images.FirstOrDefault()?.Src ?? p.Images.FirstOrDefault()?.Src ?? "";
                var imageFiles = string.IsNullOrWhiteSpace(v.ImageFilePaths)
                    ? p.ImageFilePaths ?? ""
                    : v.ImageFilePaths;

                yield return new Dictionary<string, object?>
                {
                    ["Handle"] = string.IsNullOrWhiteSpace(p.Slug) ? $"prod-{p.Id}" : p.Slug,
                    ["Title"] = p.Name,
                    ["Body (HTML)"] = p.Description ?? p.ShortDescription ?? p.Summary,
                    ["Vendor"] = vendor,
                    ["Product Category"] = categories,
                    ["Type"] = primaryCategory,
                    ["Tags"] = tags,
                    ["SEO Title"] = p.MetaTitle ?? "",
                    ["SEO Description"] = p.MetaDescription ?? "",
                    ["SEO Keywords"] = p.MetaKeywords ?? "",
                    ["Published"] = "TRUE",
                    ["Option1 Name"] = optionNames[0] ?? "Option1",
                    ["Option1 Value"] = optionNames[0] is null ? "Default Title" : (GetOpt(optionNames[0]) ?? ""),
                    ["Option2 Name"] = optionNames[1] ?? "",
                    ["Option2 Value"] = optionNames[1] is null ? "" : (GetOpt(optionNames[1]) ?? ""),
                    ["Option3 Name"] = optionNames[2] ?? "",
                    ["Option3 Value"] = optionNames[2] is null ? "" : (GetOpt(optionNames[2]) ?? ""),
                    ["Variant SKU"] = string.IsNullOrWhiteSpace(v.Sku) ? p.Sku ?? "" : v.Sku,
                    ["Variant Price"] = price is null
                        ? ""
                        : price.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                    ["Variant Inventory Qty"] = "",
                    ["Variant Requires Shipping"] = "TRUE",
                    ["Variant Taxable"] = "TRUE",
                    ["Variant Weight Unit"] = "kg",
                    ["Image Src"] = imageSrc,
                    ["Image Src Local"] = imageFiles
                };
            }
        }
    }
}
