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
        var parentList = parents.ToList();
        var variationList = variations.ToList();
        var variationLookup = variationList
            .Where(v => v.ParentId is not null)
            .GroupBy(v => v.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());
        var processedVariationIds = new HashSet<int>();
        var parentIds = new HashSet<int>(parentList.Select(p => p.Id));

        foreach (var parent in parentList)
        {
            yield return BuildWooRow(parent, parent.ParentId, parent.Type);

            if (!variationLookup.TryGetValue(parent.Id, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (child.Id != 0)
                {
                    processedVariationIds.Add(child.Id);
                }

                yield return BuildWooRow(child, child.ParentId, child.Type ?? "variation");
            }
        }

        foreach (var orphan in variationList)
        {
            if (orphan.Id != 0 && processedVariationIds.Contains(orphan.Id))
            {
                continue;
            }

            if (orphan.ParentId is not null && parentIds.Contains(orphan.ParentId.Value))
            {
                continue;
            }

            yield return BuildWooRow(orphan, orphan.ParentId, orphan.Type ?? "variation");
        }
    }

    private static Dictionary<string, object?> BuildWooRow(StoreProduct product, int? parentId, string? productType)
    {
        var prices = product.Prices;
        var regular = AsFloatPrice(prices?.RegularPrice, prices?.CurrencyMinorUnit);
        var sale = AsFloatPrice(prices?.SalePrice, prices?.CurrencyMinorUnit);
        var priceVal = prices?.Price ?? prices?.RegularPrice ?? prices?.SalePrice;
        var price = AsFloatPrice(priceVal, prices?.CurrencyMinorUnit);
        var type = string.IsNullOrWhiteSpace(productType) ? "simple" : productType;
        var images = string.Join(", ", product.Images.Select(i => i.Src).Where(s => !string.IsNullOrWhiteSpace(s))!);
        var categories = JoinCsv(product.Categories.Select(c => c.Name));
        var tags = JoinCsv(product.Tags.Select(t => t.Name));
        var attributes = BuildAttributesString(product.Attributes);

        return new Dictionary<string, object?>
        {
            ["ID"] = "",
            ["Type"] = type,
            ["ParentId"] = parentId,
            ["SKU"] = product.Sku ?? "",
            ["Name"] = product.Name ?? "",
            ["Published"] = 1,
            ["Is featured?"] = 0,
            ["Visibility in catalog"] = "visible",
            ["Short description"] = FirstNonEmpty(product.ShortDescription, product.Summary) ?? "",
            ["Description"] = product.Description ?? "",
            ["SEO Title"] = product.MetaTitle ?? "",
            ["SEO Description"] = product.MetaDescription ?? "",
            ["SEO Keywords"] = product.MetaKeywords ?? "",
            ["Tax status"] = "taxable",
            ["Regular price"] = regular,
            ["Sale price"] = sale,
            ["Price"] = price,
            ["Currency"] = prices?.CurrencyCode ?? "",
            ["In stock?"] = product.IsInStock == true ? "1" : "0",
            ["Stock status"] = product.StockStatus ?? "",
            ["Categories"] = categories,
            ["Tags"] = tags,
            ["Images"] = images,
            ["Image File Paths"] = product.ImageFilePaths ?? "",
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
