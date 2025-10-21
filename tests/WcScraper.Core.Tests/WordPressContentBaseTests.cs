using Xunit;
using WcScraper.Core;

namespace WcScraper.Core.Tests;

public class WordPressContentBaseTests
{
    [Theory]
    [InlineData("<img src=\"https://example.com/image.jpg\" />", "https://example.com/image.jpg")]
    [InlineData("<img src = \"https://example.com/image.jpg\" />", "https://example.com/image.jpg")]
    [InlineData("<img data-src = \"https://example.com/image.jpg\" />", "https://example.com/image.jpg")]
    [InlineData("<video poster = \"https://example.com/poster.jpg\"></video>", "https://example.com/poster.jpg")]
    public void Normalize_CollectsMediaUrls_WhenAttributesContainOptionalWhitespace(string html, string expected)
    {
        var post = new WordPressPost
        {
            Content = new WordPressRenderedText { Rendered = html }
        };

        post.Normalize();

        Assert.Equal(expected, Assert.Single(post.ReferencedMediaUrls));
    }
}
