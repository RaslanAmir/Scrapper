using System;
using WcScraper.Core.Shopify;
using Xunit;

namespace WcScraper.Core.Tests;

public sealed class ShopifyMapperTests
{
    [Fact]
    public void ToStoreProduct_PopulatesCollectionsTagsAndImagesForExports()
    {
        var shopifyProduct = new ShopifyProduct
        {
            Id = 123456789,
            Title = "Sample Product",
            BodyHtml = "<p>Example</p>",
            ProductType = "Accessories",
            Vendor = "Example Vendor",
            Handle = "sample-product",
            MetafieldsGlobalTitleTag = "Sample Meta Title",
            MetafieldsGlobalDescriptionTag = "Sample Meta Description",
            Tags = { "Tag One, Tag Two" },
            Variants =
            {
                new ShopifyVariant
                {
                    Id = 987,
                    Title = "Default Title",
                    Sku = "SKU-1",
                    Price = "19.99",
                    CompareAtPrice = "24.99",
                    InventoryQuantity = 3
                }
            },
            Options =
            {
                new ShopifyOption { Name = "Title", Values = { "Default Title" } }
            },
            Images =
            {
                new ShopifyImage { Id = 1, Src = "https://cdn.example/front.jpg", Alt = "Front shot" },
                new ShopifyImage { Id = 2, Src = "https://cdn.example/back.jpg", Alt = "Back shot" }
            },
            Collections =
            {
                new ShopifyCollection
                {
                    Id = "gid://shopify/Collection/111",
                    Handle = "home",
                    Title = "Home"
                },
                new ShopifyCollection
                {
                    Id = "gid://shopify/Collection/222",
                    Handle = "sale",
                    Title = "Sale"
                }
            }
        };

        var settings = new ShopifySettings("https://example.myshopify.com");
        var storeProduct = ShopifyConverters.ToStoreProduct(shopifyProduct, settings);

        Assert.Equal("Example Vendor", storeProduct.Vendor);
        Assert.Equal("Sample Meta Title", storeProduct.MetaTitle);
        Assert.Equal("Sample Meta Description", storeProduct.MetaDescription);
        Assert.Equal("Tag One, Tag Two", storeProduct.MetaKeywords);
        Assert.Collection(storeProduct.Categories,
            c =>
            {
                Assert.Equal("Home", c.Name);
                Assert.Equal("home", c.Slug);
            },
            c =>
            {
                Assert.Equal("Sale", c.Name);
                Assert.Equal("sale", c.Slug);
            });
        Assert.Collection(storeProduct.Tags,
            t =>
            {
                Assert.Equal("Tag One", t.Name);
                Assert.Equal("tag-one", t.Slug);
            },
            t =>
            {
                Assert.Equal("Tag Two", t.Name);
                Assert.Equal("tag-two", t.Slug);
            });
        Assert.Collection(storeProduct.Images,
            i =>
            {
                Assert.Equal("https://cdn.example/front.jpg", i.Src);
                Assert.Equal("Front shot", i.Alt);
            },
            i =>
            {
                Assert.Equal("https://cdn.example/back.jpg", i.Src);
                Assert.Equal("Back shot", i.Alt);
            });

        var genericRow = Assert.Single(Mappers.ToGenericRows(new[] { storeProduct }));
        Assert.Equal("Home, Sale", genericRow.Categories);
        Assert.Equal("home, sale", genericRow.CategorySlugs);
        Assert.Equal("Tag One, Tag Two", genericRow.Tags);
        Assert.Equal("tag-one, tag-two", genericRow.TagSlugs);
        Assert.Equal("https://cdn.example/front.jpg, https://cdn.example/back.jpg", genericRow.Images);
        Assert.Equal("Front shot, Back shot", genericRow.ImageAlts);
        Assert.Equal("Sample Meta Title", genericRow.MetaTitle);
        Assert.Equal("Sample Meta Description", genericRow.MetaDescription);
        Assert.Equal("Tag One, Tag Two", genericRow.MetaKeywords);

        var shopifyRow = Assert.Single(Mappers.ToShopifyCsv(new[] { storeProduct }, settings.BaseUrl));
        Assert.Equal("Home, Sale", shopifyRow["Product Category"]);
        Assert.Equal("Home", shopifyRow["Type"]);
        Assert.Equal("Tag One, Tag Two", shopifyRow["Tags"]);
        Assert.Equal("https://cdn.example/front.jpg", shopifyRow["Image Src"]);
        Assert.Equal("19.99", shopifyRow["Variant Price"]);
        Assert.Equal("Sample Meta Title", shopifyRow["SEO Title"]);
        Assert.Equal("Sample Meta Description", shopifyRow["SEO Description"]);
        Assert.Equal("Tag One, Tag Two", shopifyRow["SEO Keywords"]);

        var wooRow = Assert.Single(Mappers.ToWooImporterCsv(new[] { storeProduct }, Array.Empty<StoreProduct>()));
        Assert.Equal("Accessories", wooRow["Type"]);
        Assert.Null(wooRow["ParentId"]);
        Assert.Equal("Home, Sale", wooRow["Categories"]);
        Assert.Equal("Tag One, Tag Two", wooRow["Tags"]);
        Assert.Equal("https://cdn.example/front.jpg, https://cdn.example/back.jpg", wooRow["Images"]);
        Assert.Equal("Title: Default Title", wooRow["Attributes"]);
        Assert.Equal(24.99, wooRow["Regular price"]);
        Assert.Equal(19.99, wooRow["Sale price"]);
        Assert.Equal(19.99, wooRow["Price"]);
        Assert.Equal("1", wooRow["In stock?"]);
        Assert.Equal("Sample Meta Title", wooRow["SEO Title"]);
        Assert.Equal("Sample Meta Description", wooRow["SEO Description"]);
        Assert.Equal("Tag One, Tag Two", wooRow["SEO Keywords"]);
    }
}
