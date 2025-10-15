using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using WcScraper.Core;

namespace WcScraper.Core.Shopify;

public sealed class ShopifyRestProductResponse
{
    [JsonPropertyName("products")]
    public List<ShopifyProduct> Products { get; set; } = new();
}

public sealed class ShopifyProduct
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("body_html")] public string? BodyHtml { get; set; }
    [JsonPropertyName("vendor")] public string? Vendor { get; set; }
    [JsonPropertyName("product_type")] public string? ProductType { get; set; }
    [JsonPropertyName("handle")] public string? Handle { get; set; }
    [JsonPropertyName("tags")]
    [JsonConverter(typeof(ShopifyTagsJsonConverter))]
    public List<string> Tags { get; set; } = new();
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("variants")] public List<ShopifyVariant> Variants { get; set; } = new();
    [JsonPropertyName("options")] public List<ShopifyOption> Options { get; set; } = new();
    [JsonPropertyName("images")] public List<ShopifyImage> Images { get; set; } = new();

    public List<ShopifyCollection> Collections { get; set; } = new();
}

public sealed class ShopifyVariant
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("sku")] public string? Sku { get; set; }
    [JsonPropertyName("price")] public string? Price { get; set; }
    [JsonPropertyName("compare_at_price")] public string? CompareAtPrice { get; set; }
    [JsonPropertyName("inventory_quantity")] public int? InventoryQuantity { get; set; }
    [JsonPropertyName("requires_shipping")] public bool? RequiresShipping { get; set; }
    [JsonPropertyName("weight")] public double? Weight { get; set; }
    [JsonPropertyName("weight_unit")] public string? WeightUnit { get; set; }
    [JsonPropertyName("option1")] public string? Option1 { get; set; }
    [JsonPropertyName("option2")] public string? Option2 { get; set; }
    [JsonPropertyName("option3")] public string? Option3 { get; set; }
}

public sealed class ShopifyOption
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("values")] public List<string> Values { get; set; } = new();
}

public sealed class ShopifyImage
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("src")] public string? Src { get; set; }
    [JsonPropertyName("alt")] public string? Alt { get; set; }
}

public sealed class ShopifyCollection
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("handle")] public string? Handle { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
}

public sealed class ShopifyCollectionDetails
{
    [JsonPropertyName("id")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long? Id { get; set; }

    [JsonPropertyName("handle")]
    public string? Handle { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("body_html")]
    public string? BodyHtml { get; set; }

    [JsonPropertyName("published_at")]
    public string? PublishedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; set; }

    [JsonPropertyName("sort_order")]
    public string? SortOrder { get; set; }

    [JsonPropertyName("template_suffix")]
    public string? TemplateSuffix { get; set; }

    [JsonPropertyName("published_scope")]
    public string? PublishedScope { get; set; }

    [JsonPropertyName("products_count")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? ProductsCount { get; set; }

    [JsonPropertyName("admin_graphql_api_id")]
    public string? AdminGraphqlApiId { get; set; }

    [JsonPropertyName("published")]
    public bool? Published { get; set; }

    [JsonPropertyName("disjunctive")]
    public bool? Disjunctive { get; set; }

    [JsonPropertyName("rules")]
    public List<ShopifyCollectionRule> Rules { get; set; } = new();

    [JsonPropertyName("image")]
    public ShopifyCollectionImage? Image { get; set; }

    public TermItem ToTermItem()
    {
        var slug = !string.IsNullOrWhiteSpace(Handle)
            ? Handle
            : ShopifySlugHelper.Slugify(Title);

        var basis = !string.IsNullOrWhiteSpace(slug)
            ? slug!
            : Title ?? Id?.ToString(CultureInfo.InvariantCulture) ?? Guid.NewGuid().ToString();

        var identifier = Id.HasValue && Id.Value != 0
            ? unchecked((int)(Id.Value % int.MaxValue))
            : Math.Abs(basis.GetHashCode());

        return new TermItem
        {
            Id = identifier,
            Name = Title,
            Slug = slug
        };
    }
}

public sealed class ShopifyCollectionRule
{
    [JsonPropertyName("column")]
    public string? Column { get; set; }

    [JsonPropertyName("relation")]
    public string? Relation { get; set; }

    [JsonPropertyName("condition")]
    public string? Condition { get; set; }
}

public sealed class ShopifyCollectionImage
{
    [JsonPropertyName("id")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long? Id { get; set; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }

    [JsonPropertyName("src")]
    public string? Src { get; set; }

    [JsonPropertyName("alt")]
    public string? Alt { get; set; }

    [JsonPropertyName("width")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? Height { get; set; }
}

public sealed class ShopifyCollectionsResult
{
    private readonly IReadOnlyDictionary<int, ShopifyCollectionDetails> _byTermId;

    public ShopifyCollectionsResult(
        IReadOnlyList<TermItem> terms,
        IReadOnlyDictionary<long, ShopifyCollectionDetails> byId,
        IReadOnlyDictionary<string, ShopifyCollectionDetails> byHandle,
        IReadOnlyDictionary<int, ShopifyCollectionDetails> byTermId)
    {
        Terms = terms;
        ById = byId;
        ByHandle = byHandle;
        _byTermId = byTermId;
    }

    public static ShopifyCollectionsResult Empty { get; } = new(
        Array.Empty<TermItem>(),
        new Dictionary<long, ShopifyCollectionDetails>(),
        new Dictionary<string, ShopifyCollectionDetails>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<int, ShopifyCollectionDetails>());

    public IReadOnlyList<TermItem> Terms { get; }

    public IReadOnlyDictionary<long, ShopifyCollectionDetails> ById { get; }

    public IReadOnlyDictionary<string, ShopifyCollectionDetails> ByHandle { get; }

    public ShopifyCollectionDetails? FindByTerm(TermItem? term)
    {
        if (term is null)
        {
            return null;
        }

        if (_byTermId.TryGetValue(term.Id, out var details))
        {
            return details;
        }

        if (!string.IsNullOrWhiteSpace(term.Slug) && ByHandle.TryGetValue(term.Slug!, out details))
        {
            return details;
        }

        if (!string.IsNullOrWhiteSpace(term.Name))
        {
            var slug = ShopifySlugHelper.Slugify(term.Name);
            if (!string.IsNullOrWhiteSpace(slug) && ByHandle.TryGetValue(slug, out details))
            {
                return details;
            }
        }

        return null;
    }
}

public sealed class ShopifyGraphQlResponse<T>
{
    [JsonPropertyName("data")] public T? Data { get; set; }
    [JsonPropertyName("errors")] public List<ShopifyGraphQlError>? Errors { get; set; }
}

public sealed class ShopifyGraphQlError
{
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public sealed class ShopifyGraphQlProducts
{
    [JsonPropertyName("products")] public ShopifyProductConnection? Products { get; set; }
}

public sealed class ShopifyProductConnection
{
    [JsonPropertyName("edges")] public List<ShopifyProductEdge> Edges { get; set; } = new();
}

public sealed class ShopifyProductEdge
{
    [JsonPropertyName("node")] public ShopifyProductNode? Node { get; set; }
}

public sealed class ShopifyProductNode
{
    [JsonPropertyName("handle")] public string? Handle { get; set; }
    [JsonPropertyName("collections")] public ShopifyCollectionConnection Collections { get; set; } = new();
}

public sealed class ShopifyCollectionConnection
{
    [JsonPropertyName("edges")] public List<ShopifyCollectionEdge> Edges { get; set; } = new();
}

public sealed class ShopifyCollectionEdge
{
    [JsonPropertyName("node")] public ShopifyCollectionNode? Node { get; set; }
}

public sealed class ShopifyCollectionNode
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("handle")] public string? Handle { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
}

internal sealed class ShopifyTagsJsonConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return new List<string>();
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var tags = new List<string>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    break;
                }

                if (reader.TokenType == JsonTokenType.String)
                {
                    var value = reader.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        tags.Add(value);
                    }
                }
                else
                {
                    using var _ = JsonDocument.ParseValue(ref reader);
                }
            }

            return tags;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            return string.IsNullOrWhiteSpace(value)
                ? new List<string>()
                : new List<string> { value };
        }

        using var skipped = JsonDocument.ParseValue(ref reader);
        return new List<string>();
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        if (value is not null)
        {
            foreach (var tag in value)
            {
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    writer.WriteStringValue(tag);
                }
            }
        }

        writer.WriteEndArray();
    }
}
