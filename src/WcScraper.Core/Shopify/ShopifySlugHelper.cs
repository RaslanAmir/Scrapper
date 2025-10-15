using System.Text;

namespace WcScraper.Core.Shopify;

internal static class ShopifySlugHelper
{
    public static string? Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var span = value.Trim().ToLowerInvariant().AsSpan();
        var builder = new StringBuilder(span.Length);

        foreach (var ch in span)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
            else if (char.IsWhiteSpace(ch) || ch == '-' || ch == '_')
            {
                if (builder.Length > 0 && builder[^1] != '-')
                {
                    builder.Append('-');
                }
            }
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? null : slug;
    }
}
