# Scrapper

Utilities for exporting WooCommerce and Shopify catalog data into flat files that can be re-imported into other systems.

## Shopify API configuration

The Shopify scraper requires a base store URL plus credentials for the Admin REST API. The app accepts the same settings that
are surfaced in the WPF UI:

1. **Base URL** – the root of your storefront, e.g. `https://example.myshopify.com`. The scraper normalizes the value so it can
   be reused when constructing permalinks and CSV exports.
2. **Admin access token** – recommended. Create or open a custom app in Shopify (`Settings ➜ Apps and sales channels ➜ Develop
   apps`) and grant at least the `read_products` scope. After installing the app, copy the Admin API access token and paste it
   into the scraper. The token is sent via the `X-Shopify-Access-Token` header when querying `products.json`.
3. **Storefront access token (optional)** – supply a Storefront API token from the same app if you want collection information to
   be enriched via the GraphQL endpoint. Without it collections will be empty but the rest of the export will continue to work.
4. **Private app API key and secret (fallback)** – legacy private apps expose a key/secret pair that can still be used when an
   Admin access token is unavailable. Provide both values to allow the scraper to fall back to HTTP basic authentication.

Only one authentication mode is required: either an Admin access token **or** the API key/secret pair. The Storefront token is
optional but improves collection metadata in the exports.

### Minimal permission checklist

Make sure the custom app has the following scopes before exporting:

- `read_products`
- `read_product_listings` (needed for GraphQL collection enrichment)

If you plan to include inventory levels or pricing, also grant `read_inventory` and `read_prices`.

## Expected export files and columns

The WPF app produces up to four files per run. Tests in `tests/WcScraper.Tests` verify the column layout so Shopify exports stay
aligned with the WooCommerce path.

### `products.csv` / `products.xlsx` / `products.jsonl`

These files are generated from the generic product projection that both WooCommerce and Shopify share. Columns are:

```
id, name, slug, permalink, sku, type, description_html, short_description_html, summary_html,
regular_price, sale_price, price, currency, in_stock, stock_status, average_rating, review_count,
has_options, parent_id, categories, category_slugs, tags, tag_slugs, images, image_alts
```

### `shopify_products.csv`

This CSV mirrors Shopify's bulk import template. Each row contains:

```
Handle, Title, Body (HTML), Vendor, Product Category, Type, Tags, Published,
Option1 Name, Option1 Value, Variant SKU, Variant Price, Variant Inventory Qty,
Variant Requires Shipping, Variant Taxable, Variant Weight Unit, Image Src
```

When parent products have variations the exporter also emits `Option2 Name` / `Option2 Value` and `Option3 Name` / `Option3 Value`
columns for each variant row.

### `woocommerce_products.csv`

The WooCommerce importer template continues to be supported and includes the following fields:

```
ID, Type, SKU, Name, Published, Is featured?, Visibility in catalog, Short description,
Description, Tax status, In stock?, Categories, Tags, Images, Position
```

## Running tests

Run the full test suite (including the Shopify smoke test) with:

```bash
dotnet test WcScraper.sln
```

The solution file wires up both the existing unit tests and the new integration coverage so the command above will exercise all
Shopify and WooCommerce scenarios in CI.
