using System;
using System.Linq;
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

    [Fact]
    public void ToWooImporterCsv_EmitsVariationRowsWithParentContext()
    {
        var parent = new StoreProduct
        {
            Id = 101,
            Name = "Variable Shirt",
            Type = "variable",
            ShortDescription = "<p>Parent summary</p>",
            Description = "<p>Parent description</p>",
            MetaTitle = "Parent Meta Title",
            MetaDescription = "Parent Meta Description",
            MetaKeywords = "Parent Meta Keywords",
            Prices = new PriceInfo
            {
                CurrencyCode = "USD",
                CurrencyMinorUnit = 2,
                RegularPrice = "2199",
                SalePrice = "1999",
                Price = "1999"
            },
            Categories =
            {
                new Category { Name = "Apparel" },
                new Category { Name = "Sale" }
            },
            Tags =
            {
                new ProductTag { Name = "Featured" },
                new ProductTag { Name = "Limited" }
            }
        };

        var variation = new StoreProduct
        {
            Id = 202,
            ParentId = parent.Id,
            Name = "Variable Shirt - Blue",
            Sku = "SHIRT-BLUE",
            IsInStock = true,
            StockStatus = "instock",
            Prices = new PriceInfo
            {
                CurrencyCode = "USD",
                CurrencyMinorUnit = 2,
                RegularPrice = "1999",
                SalePrice = "1499",
                Price = "1499"
            },
            Attributes =
            {
                new VariationAttribute { Name = "Color", Option = "Blue" }
            },
            Images =
            {
                new ProductImage { Src = "https://cdn.example/blue-shirt.jpg" }
            },
            ImageFilePaths = "images/blue-shirt.jpg"
        };

        var rows = Mappers.ToWooImporterCsv(new[] { parent }, new[] { variation }).ToList();
        Assert.Equal(2, rows.Count);

        var parentRow = rows[0];
        Assert.Equal("variable", parentRow["Type"]);
        Assert.Null(parentRow["ParentId"]);
        Assert.Equal("Variable Shirt", parentRow["Name"]);
        Assert.Equal("Apparel, Sale", parentRow["Categories"]);
        Assert.Equal("Featured, Limited", parentRow["Tags"]);

        var variationRow = rows[1];
        Assert.Equal("variation", variationRow["Type"]);
        Assert.Equal(parent.Id, variationRow["ParentId"]);
        Assert.Equal("SHIRT-BLUE", variationRow["SKU"]);
        Assert.Equal("Variable Shirt - Blue", variationRow["Name"]);
        Assert.Equal(19.99, variationRow["Regular price"]);
        Assert.Equal(14.99, variationRow["Sale price"]);
        Assert.Equal(14.99, variationRow["Price"]);
        Assert.Equal("USD", variationRow["Currency"]);
        Assert.Equal("1", variationRow["In stock?"]);
        Assert.Equal("instock", variationRow["Stock status"]);
        Assert.Equal("Apparel, Sale", variationRow["Categories"]);
        Assert.Equal("Featured, Limited", variationRow["Tags"]);
        Assert.Equal("Color: Blue", variationRow["Attributes"]);
        Assert.Equal("https://cdn.example/blue-shirt.jpg", variationRow["Images"]);
        Assert.Equal("images/blue-shirt.jpg", variationRow["Image File Paths"]);
        Assert.Equal("Parent Meta Title", variationRow["SEO Title"]);
        Assert.Equal("Parent Meta Description", variationRow["SEO Description"]);
        Assert.Equal("Parent Meta Keywords", variationRow["SEO Keywords"]);
        Assert.Equal("<p>Parent summary</p>", variationRow["Short description"]);
        Assert.Equal("<p>Parent description</p>", variationRow["Description"]);
    }
}
