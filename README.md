# Scrapper

Utilities for exporting WooCommerce and Shopify catalog data into flat files that can be re-imported into other systems. The tooling can also mirror WordPress marketing content – including pages, posts, menus, widgets, and the complete media library – so a destination WooCommerce store can be brought online with navigation and marketing collateral already in place.

## WordPress export bundle

When a WooCommerce store is selected the exporter now captures a parallel WordPress bundle alongside the product catalog. The scraper talks to the core REST endpoints (`/wp-json/wp/v2/pages`, `/posts`, `/media`) plus the active menu and widget endpoints so content that is normally managed outside WooCommerce is preserved.

The export bundle contains:

- JSON files for pages, posts, menus, widgets, and the full media library (`wordpress-pages.json`, `wordpress-posts.json`, `wordpress-menus.json`, `wordpress-widgets.json`, `wordpress-media.json`).
- Downloaded media organized under `media/wordpress/<timestamp>/`, mirroring the URLs referenced in the JSON artifacts so provisioning can replay the structure without redownloading assets.
- A `wordpress-site-content.json` container that aggregates all pieces for provisioning workflows.

Supply a WordPress username and application password before running the export if you want authenticated-only data (widgets, draft content) to be included.

## Provisioning WordPress content

During replication the WPF app now asks `WooProvisioningService` to seed WordPress content _before_ products are created. Provide the same WordPress credentials that were used during export so the provisioning pipeline can:

1. Upload the downloaded media library and map remote IDs back to exported content.
2. Recreate pages and posts, linking to the uploaded media when a match is found.
3. Create menus and assign their locations to match the source site.
4. Rebuild widget areas and populate them with their original widgets.

These steps ensure that navigation and marketing surfaces are in place when products are provisioned, giving the cloned storefront a consistent look and feel.

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
