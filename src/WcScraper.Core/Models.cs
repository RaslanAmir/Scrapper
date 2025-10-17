using System.Text.Json;
using System.Text.Json.Serialization;

namespace WcScraper.Core;

public sealed class StoreProduct
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("slug")] public string? Slug { get; set; }
    [JsonPropertyName("permalink")] public string? Permalink { get; set; }
    [JsonPropertyName("sku")] public string? Sku { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("vendor")] public string? Vendor { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("short_description")] public string? ShortDescription { get; set; }
    // Some stores use "summary" in Store API; we will copy it into ShortDescription if present.
    [JsonPropertyName("summary")] public string? Summary { get; set; }

    [JsonPropertyName("meta_title")] public string? MetaTitle { get; set; }
    [JsonPropertyName("meta_description")] public string? MetaDescription { get; set; }
    [JsonPropertyName("meta_keywords")] public string? MetaKeywords { get; set; }
    [JsonPropertyName("yoast_head_json")] public YoastHead? YoastHead { get; set; }
    [JsonPropertyName("meta_data")] public List<StoreMetaData> MetaData { get; set; } = new();

    [JsonPropertyName("prices")] public PriceInfo? Prices { get; set; }

    [JsonPropertyName("is_in_stock")] public bool? IsInStock { get; set; }
    [JsonPropertyName("stock_status")] public string? StockStatus { get; set; }
    [JsonPropertyName("average_rating")] public double? AverageRating { get; set; }
    [JsonPropertyName("review_count")] public int? ReviewCount { get; set; }
    [JsonPropertyName("has_options")] public bool? HasOptions { get; set; }
    [JsonPropertyName("parent")] public int? ParentId { get; set; }
    [JsonPropertyName("attributes")] public List<VariationAttribute> Attributes { get; set; } = new();


    [JsonPropertyName("categories")] public List<Category> Categories { get; set; } = new();
    [JsonPropertyName("tags")] public List<ProductTag> Tags { get; set; } = new();
    [JsonPropertyName("images")] public List<ProductImage> Images { get; set; } = new();
    public string? ImageFilePaths { get; set; }
    [JsonIgnore] public List<string> LocalImageFilePaths { get; } = new();
}

public sealed class PriceInfo
{
    [JsonPropertyName("currency_code")] public string? CurrencyCode { get; set; }
    [JsonPropertyName("currency_minor_unit")] public int? CurrencyMinorUnit { get; set; }
    [JsonPropertyName("price")] public string? Price { get; set; }
    [JsonPropertyName("regular_price")] public string? RegularPrice { get; set; }
    [JsonPropertyName("sale_price")] public string? SalePrice { get; set; }
}

public sealed class Category
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("slug")] public string? Slug { get; set; }
}

public sealed class ProductTag
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("slug")] public string? Slug { get; set; }
}

public sealed class ProductImage
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("src")] public string? Src { get; set; }
    [JsonPropertyName("alt")] public string? Alt { get; set; }
}

public sealed class StoreReview
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("reviewer")] public string? Reviewer { get; set; }
    [JsonPropertyName("review")] public string? Review { get; set; }
    [JsonPropertyName("rating")] public int? Rating { get; set; }
    [JsonPropertyName("product_id")] public int? ProductId { get; set; }
    [JsonPropertyName("date_created")] public string? DateCreated { get; set; }
}


public sealed class VariationAttribute
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("taxonomy")] public string? Taxonomy { get; set; }
    [JsonPropertyName("attribute")] public string? AttributeKey { get; set; } // e.g., pa_color
    [JsonPropertyName("term")] public string? Term { get; set; } // e.g., "red"
    [JsonPropertyName("slug")] public string? Slug { get; set; }
    [JsonPropertyName("option")] public string? Option { get; set; } // REST style
    [JsonPropertyName("value")] public string? Value { get; set; }   // fallback
}

public sealed class TermItem
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("slug")] public string? Slug { get; set; }
}

public sealed class InstalledPlugin
{
    [JsonPropertyName("plugin")] public string? PluginFile { get; set; }
    [JsonPropertyName("slug")] public string? Slug { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("autoupdate")] public bool? AutoUpdate { get; set; }
    [JsonPropertyName("update_channel")] public string? UpdateChannel { get; set; }
    [JsonPropertyName("update")] public PluginUpdateInfo? Update { get; set; }
    [JsonPropertyName("option_keys")] public List<string> OptionKeys { get; set; } = new();
    [JsonPropertyName("asset_paths")] public List<string> AssetPaths { get; set; } = new();

    public void Normalize()
    {
        if (!string.IsNullOrWhiteSpace(UpdateChannel))
        {
            UpdateChannel = UpdateChannel?.Trim();
        }
        else if (Update is not null && !string.IsNullOrWhiteSpace(Update.Channel))
        {
            UpdateChannel = Update.Channel?.Trim();
        }
        else if (AutoUpdate is not null)
        {
            UpdateChannel = AutoUpdate == true ? "auto" : "manual";
        }

        if (!string.IsNullOrWhiteSpace(Status))
        {
            Status = Status?.Trim();
        }

        if (string.IsNullOrWhiteSpace(Slug) && !string.IsNullOrWhiteSpace(PluginFile))
        {
            var normalized = PluginFile;
            var slash = normalized.IndexOf('/');
            if (slash > 0)
            {
                normalized = normalized[..slash];
            }
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                Slug = normalized.Trim();
            }
        }
    }
}

public sealed class PluginUpdateInfo
{
    [JsonPropertyName("channel")] public string? Channel { get; set; }
    [JsonPropertyName("new_version")] public string? NewVersion { get; set; }
    [JsonPropertyName("package")] public string? Package { get; set; }
}

public sealed class InstalledTheme
{
    [JsonPropertyName("stylesheet")] public string? Stylesheet { get; set; }
    [JsonPropertyName("template")] public string? Template { get; set; }
    [JsonPropertyName("slug")] public string? Slug { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("autoupdate")] public bool? AutoUpdate { get; set; }
    [JsonPropertyName("update_channel")] public string? UpdateChannel { get; set; }
    [JsonPropertyName("update")] public ThemeUpdateInfo? Update { get; set; }
    [JsonPropertyName("option_keys")] public List<string> OptionKeys { get; set; } = new();
    [JsonPropertyName("asset_paths")] public List<string> AssetPaths { get; set; } = new();

    public void Normalize()
    {
        if (!string.IsNullOrWhiteSpace(UpdateChannel))
        {
            UpdateChannel = UpdateChannel?.Trim();
        }
        else if (Update is not null && !string.IsNullOrWhiteSpace(Update.Channel))
        {
            UpdateChannel = Update.Channel?.Trim();
        }
        else if (AutoUpdate is not null)
        {
            UpdateChannel = AutoUpdate == true ? "auto" : "manual";
        }

        if (!string.IsNullOrWhiteSpace(Status))
        {
            Status = Status?.Trim();
        }

        if (string.IsNullOrWhiteSpace(Slug) && !string.IsNullOrWhiteSpace(Stylesheet))
        {
            Slug = Stylesheet?.Trim();
        }
    }
}

public sealed class ThemeUpdateInfo
{
    [JsonPropertyName("channel")] public string? Channel { get; set; }
    [JsonPropertyName("new_version")] public string? NewVersion { get; set; }
    [JsonPropertyName("package")] public string? Package { get; set; }
}

public sealed class WooStoreSetting
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("group_id")] public string? GroupId { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("value")] public JsonElement? Value { get; set; }
    [JsonPropertyName("default")] public JsonElement? Default { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("tip")] public string? Tip { get; set; }
    [JsonPropertyName("placeholder")] public string? Placeholder { get; set; }
    [JsonPropertyName("options")] public Dictionary<string, JsonElement>? Options { get; set; }
}

public sealed class ShippingZoneSetting
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("order")] public int Order { get; set; }
    [JsonPropertyName("locations")] public List<ShippingZoneLocation> Locations { get; set; } = new();
    [JsonPropertyName("methods")] public List<ShippingZoneMethodSetting> Methods { get; set; } = new();
}

public sealed class ShippingZoneLocation
{
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
}

public sealed class ShippingZoneMethodSetting
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("instance_id")] public int InstanceId { get; set; }
    [JsonPropertyName("method_id")] public string? MethodId { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("order")] public int? Order { get; set; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
    [JsonPropertyName("method_title")] public string? MethodTitle { get; set; }
    [JsonPropertyName("method_description")] public string? MethodDescription { get; set; }
    [JsonPropertyName("settings")] public Dictionary<string, WooStoreSetting>? Settings { get; set; }
}

public sealed class PaymentGatewaySetting
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("order")] public int? Order { get; set; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
    [JsonPropertyName("method_title")] public string? MethodTitle { get; set; }
    [JsonPropertyName("method_description")] public string? MethodDescription { get; set; }
    [JsonPropertyName("settings")] public Dictionary<string, WooStoreSetting>? Settings { get; set; }
}

public sealed class StoreConfiguration
{
    [JsonPropertyName("store_settings")] public List<WooStoreSetting> StoreSettings { get; set; } = new();
    [JsonPropertyName("shipping_zones")] public List<ShippingZoneSetting> ShippingZones { get; set; } = new();
    [JsonPropertyName("payment_gateways")] public List<PaymentGatewaySetting> PaymentGateways { get; set; } = new();
}

// Export rows
public sealed class GenericRow
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Slug { get; set; }
    public string? Permalink { get; set; }
    public string? Sku { get; set; }
    public string? Type { get; set; }
    public string? DescriptionHtml { get; set; }
    public string? ShortDescriptionHtml { get; set; }
    public string? SummaryHtml { get; set; }
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
    public string? MetaKeywords { get; set; }
    public double? RegularPrice { get; set; }
    public double? SalePrice { get; set; }
    public double? Price { get; set; }
    public string? Currency { get; set; }
    public bool? InStock { get; set; }
    public string? StockStatus { get; set; }
    public double? AverageRating { get; set; }
    public int? ReviewCount { get; set; }
    public bool? HasOptions { get; set; }
    public int? ParentId { get; set; }
    public string? Categories { get; set; }
    public string? CategorySlugs { get; set; }
    public string? Tags { get; set; }
    public string? TagSlugs { get; set; }
    public string? Images { get; set; }
    public string? ImageAlts { get; set; }
    public string? ImageFilePaths { get; set; }
}

public sealed class YoastHead
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("og_title")] public string? OgTitle { get; set; }
    [JsonPropertyName("og_description")] public string? OgDescription { get; set; }
    [JsonPropertyName("twitter_title")] public string? TwitterTitle { get; set; }
    [JsonPropertyName("twitter_description")] public string? TwitterDescription { get; set; }
    [JsonPropertyName("keywords")] public string? Keywords { get; set; }
}

public sealed class StoreMetaData
{
    [JsonPropertyName("id")] public int? Id { get; set; }
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("value")] public JsonElement? Value { get; set; }

    public string? ValueAsString()
    {
        if (Value is not JsonElement element)
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Array or JsonValueKind.Object => element.GetRawText(),
            _ => null
        };
    }
}

public sealed class WooAddress
{
    [JsonPropertyName("first_name")] public string? FirstName { get; set; }
    [JsonPropertyName("last_name")] public string? LastName { get; set; }
    [JsonPropertyName("company")] public string? Company { get; set; }
    [JsonPropertyName("address_1")] public string? Address1 { get; set; }
    [JsonPropertyName("address_2")] public string? Address2 { get; set; }
    [JsonPropertyName("city")] public string? City { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("postcode")] public string? Postcode { get; set; }
    [JsonPropertyName("country")] public string? Country { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("phone")] public string? Phone { get; set; }
}

public sealed class WooCustomer
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("first_name")] public string? FirstName { get; set; }
    [JsonPropertyName("last_name")] public string? LastName { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("billing")] public WooAddress? Billing { get; set; }
    [JsonPropertyName("shipping")] public WooAddress? Shipping { get; set; }
    [JsonPropertyName("is_paying_customer")] public bool? IsPayingCustomer { get; set; }
    [JsonPropertyName("date_created")] public string? DateCreated { get; set; }
    [JsonPropertyName("date_created_gmt")] public string? DateCreatedGmt { get; set; }
    [JsonPropertyName("date_modified")] public string? DateModified { get; set; }
    [JsonPropertyName("date_modified_gmt")] public string? DateModifiedGmt { get; set; }
    [JsonPropertyName("meta_data")] public List<StoreMetaData> MetaData { get; set; } = new();
}

public sealed class WooCoupon
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("amount")] public string? Amount { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("discount_type")] public string? DiscountType { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("date_created")] public string? DateCreated { get; set; }
    [JsonPropertyName("date_created_gmt")] public string? DateCreatedGmt { get; set; }
    [JsonPropertyName("date_modified")] public string? DateModified { get; set; }
    [JsonPropertyName("date_modified_gmt")] public string? DateModifiedGmt { get; set; }
    [JsonPropertyName("date_expires")] public string? DateExpires { get; set; }
    [JsonPropertyName("date_expires_gmt")] public string? DateExpiresGmt { get; set; }
    [JsonPropertyName("usage_count")] public int UsageCount { get; set; }
    [JsonPropertyName("individual_use")] public bool? IndividualUse { get; set; }
    [JsonPropertyName("product_ids")] public List<int> ProductIds { get; set; } = new();
    [JsonPropertyName("excluded_product_ids")] public List<int> ExcludedProductIds { get; set; } = new();
    [JsonPropertyName("usage_limit")] public int? UsageLimit { get; set; }
    [JsonPropertyName("usage_limit_per_user")] public int? UsageLimitPerUser { get; set; }
    [JsonPropertyName("limit_usage_to_x_items")] public int? LimitUsageToItems { get; set; }
    [JsonPropertyName("free_shipping")] public bool? FreeShipping { get; set; }
    [JsonPropertyName("product_categories")] public List<int> ProductCategories { get; set; } = new();
    [JsonPropertyName("excluded_product_categories")] public List<int> ExcludedProductCategories { get; set; } = new();
    [JsonPropertyName("exclude_sale_items")] public bool? ExcludeSaleItems { get; set; }
    [JsonPropertyName("minimum_amount")] public string? MinimumAmount { get; set; }
    [JsonPropertyName("maximum_amount")] public string? MaximumAmount { get; set; }
    [JsonPropertyName("email_restrictions")] public List<string> EmailRestrictions { get; set; } = new();
    [JsonPropertyName("meta_data")] public List<StoreMetaData> MetaData { get; set; } = new();
}

public sealed class WooOrder
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("number")] public string? Number { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("currency")] public string? Currency { get; set; }
    [JsonPropertyName("customer_id")] public int? CustomerId { get; set; }
    [JsonPropertyName("order_key")] public string? OrderKey { get; set; }
    [JsonPropertyName("billing")] public WooAddress? Billing { get; set; }
    [JsonPropertyName("shipping")] public WooAddress? Shipping { get; set; }
    [JsonPropertyName("payment_method")] public string? PaymentMethod { get; set; }
    [JsonPropertyName("payment_method_title")] public string? PaymentMethodTitle { get; set; }
    [JsonPropertyName("transaction_id")] public string? TransactionId { get; set; }
    [JsonPropertyName("customer_note")] public string? CustomerNote { get; set; }
    [JsonPropertyName("date_created")] public string? DateCreated { get; set; }
    [JsonPropertyName("date_created_gmt")] public string? DateCreatedGmt { get; set; }
    [JsonPropertyName("date_modified")] public string? DateModified { get; set; }
    [JsonPropertyName("date_modified_gmt")] public string? DateModifiedGmt { get; set; }
    [JsonPropertyName("date_completed")] public string? DateCompleted { get; set; }
    [JsonPropertyName("date_completed_gmt")] public string? DateCompletedGmt { get; set; }
    [JsonPropertyName("date_paid")] public string? DatePaid { get; set; }
    [JsonPropertyName("date_paid_gmt")] public string? DatePaidGmt { get; set; }
    [JsonPropertyName("discount_total")] public string? DiscountTotal { get; set; }
    [JsonPropertyName("discount_tax")] public string? DiscountTax { get; set; }
    [JsonPropertyName("shipping_total")] public string? ShippingTotal { get; set; }
    [JsonPropertyName("shipping_tax")] public string? ShippingTax { get; set; }
    [JsonPropertyName("cart_tax")] public string? CartTax { get; set; }
    [JsonPropertyName("total")] public string? Total { get; set; }
    [JsonPropertyName("total_tax")] public string? TotalTax { get; set; }
    [JsonPropertyName("line_items")] public List<WooOrderLineItem> LineItems { get; set; } = new();
    [JsonPropertyName("shipping_lines")] public List<WooOrderShippingLine> ShippingLines { get; set; } = new();
    [JsonPropertyName("coupon_lines")] public List<WooOrderCouponLine> CouponLines { get; set; } = new();
    [JsonPropertyName("fee_lines")] public List<WooOrderFeeLine> FeeLines { get; set; } = new();
    [JsonPropertyName("tax_lines")] public List<WooOrderTaxLine> TaxLines { get; set; } = new();
    [JsonPropertyName("meta_data")] public List<StoreMetaData> MetaData { get; set; } = new();
}

public sealed class WooOrderLineItem
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("product_id")] public int? ProductId { get; set; }
    [JsonPropertyName("variation_id")] public int? VariationId { get; set; }
    [JsonPropertyName("quantity")] public int Quantity { get; set; }
    [JsonPropertyName("subtotal")] public string? Subtotal { get; set; }
    [JsonPropertyName("subtotal_tax")] public string? SubtotalTax { get; set; }
    [JsonPropertyName("total")] public string? Total { get; set; }
    [JsonPropertyName("total_tax")] public string? TotalTax { get; set; }
    [JsonPropertyName("price")] public string? Price { get; set; }
    [JsonPropertyName("sku")] public string? Sku { get; set; }
    [JsonPropertyName("meta_data")] public List<StoreMetaData> MetaData { get; set; } = new();
}

public sealed class WooOrderShippingLine
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("method_title")] public string? MethodTitle { get; set; }
    [JsonPropertyName("method_id")] public string? MethodId { get; set; }
    [JsonPropertyName("total")] public string? Total { get; set; }
    [JsonPropertyName("total_tax")] public string? TotalTax { get; set; }
    [JsonPropertyName("meta_data")] public List<StoreMetaData> MetaData { get; set; } = new();
}

public sealed class WooOrderCouponLine
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("discount")] public string? Discount { get; set; }
    [JsonPropertyName("discount_tax")] public string? DiscountTax { get; set; }
    [JsonPropertyName("meta_data")] public List<StoreMetaData> MetaData { get; set; } = new();
}

public sealed class WooOrderFeeLine
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("tax_class")] public string? TaxClass { get; set; }
    [JsonPropertyName("tax_status")] public string? TaxStatus { get; set; }
    [JsonPropertyName("total")] public string? Total { get; set; }
    [JsonPropertyName("total_tax")] public string? TotalTax { get; set; }
    [JsonPropertyName("meta_data")] public List<StoreMetaData> MetaData { get; set; } = new();
}

public sealed class WooOrderTaxLine
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("rate_code")] public string? RateCode { get; set; }
    [JsonPropertyName("rate_id")] public int? RateId { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("compound")] public bool? Compound { get; set; }
    [JsonPropertyName("tax_total")] public string? TaxTotal { get; set; }
    [JsonPropertyName("shipping_tax_total")] public string? ShippingTaxTotal { get; set; }
    [JsonPropertyName("meta_data")] public List<StoreMetaData> MetaData { get; set; } = new();
}

public sealed class WooSubscription
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("number")] public string? Number { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("customer_id")] public int? CustomerId { get; set; }
    [JsonPropertyName("billing")] public WooAddress? Billing { get; set; }
    [JsonPropertyName("shipping")] public WooAddress? Shipping { get; set; }
    [JsonPropertyName("payment_method")] public string? PaymentMethod { get; set; }
    [JsonPropertyName("payment_method_title")] public string? PaymentMethodTitle { get; set; }
    [JsonPropertyName("start_date")] public string? StartDate { get; set; }
    [JsonPropertyName("trial_end_date")] public string? TrialEndDate { get; set; }
    [JsonPropertyName("next_payment_date")] public string? NextPaymentDate { get; set; }
    [JsonPropertyName("end_date")] public string? EndDate { get; set; }
    [JsonPropertyName("line_items")] public List<WooOrderLineItem> LineItems { get; set; } = new();
    [JsonPropertyName("meta_data")] public List<StoreMetaData> MetaData { get; set; } = new();
}

public sealed class ShopifyRow
{
    public string? Handle { get; set; }
    public string? Title { get; set; }
    public string? BodyHtml { get; set; }
    public string? Vendor { get; set; }
    public string? ProductCategory { get; set; }
    public string? Type { get; set; }
    public string? Tags { get; set; }
    public string Published { get; set; } = "TRUE";
    public string? Option1Name { get; set; } = "Title";
    public string? Option1Value { get; set; } = "Default Title";
    public string? VariantSku { get; set; }
    public string? VariantPrice { get; set; }
    public string? VariantInventoryQty { get; set; }
    public string VariantRequiresShipping { get; set; } = "TRUE";
    public string VariantTaxable { get; set; } = "TRUE";
    public string VariantWeightUnit { get; set; } = "kg";
    public string? ImageSrc { get; set; }
}

public sealed class WooRow
{
    public string? ID { get; set; } = "";
    public string? Type { get; set; } = "simple";
    public string? SKU { get; set; }
    public string? Name { get; set; }
    public int Published { get; set; } = 1;
    public int IsFeatured { get; set; } = 0;
    public string VisibilityInCatalog { get; set; } = "visible";
    public string? ShortDescription { get; set; }
    public string? Description { get; set; }
    public string? TaxStatus { get; set; } = "taxable";
    public string? InStock { get; set; } = "1";
    public string? Categories { get; set; }
    public string? Images { get; set; }
    public int Position { get; set; } = 0;
}
