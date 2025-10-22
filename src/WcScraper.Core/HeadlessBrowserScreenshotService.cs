using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace WcScraper.Core;

public sealed record DesignScreenshot(string Label, int Width, int Height, string FilePath, byte[] ImageBytes);

public sealed class HeadlessBrowserScreenshotService
{
    private static readonly TimeSpan DefaultNavigationTimeout = TimeSpan.FromSeconds(45);

    private static readonly IReadOnlyList<(string Label, int Width, int Height)> DefaultBreakpoints = new List<(string, int, int)>
    {
        ("mobile", 375, 812),
        ("tablet", 768, 1024),
        ("desktop", 1280, 720)
    };

    public async Task<IReadOnlyList<DesignScreenshot>> CaptureScreenshotsAsync(
        string url,
        string outputDirectory,
        IEnumerable<(string Label, int Width, int Height)>? breakpoints = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(url));
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(outputDirectory));
        }

        var breakpointList = (breakpoints ?? DefaultBreakpoints).ToList();
        if (breakpointList.Count == 0)
        {
            return Array.Empty<DesignScreenshot>();
        }

        Directory.CreateDirectory(outputDirectory);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });

        var results = new List<DesignScreenshot>(breakpointList.Count);

        foreach (var (label, width, height) in breakpointList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sanitizedLabel = SanitizeLabel(label);
            var fileName = FormattableString.Invariant($"{sanitizedLabel}_{width}x{height}.png");
            var filePath = Path.Combine(outputDirectory, fileName);

            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize
                {
                    Width = width,
                    Height = height
                }
            });

            var page = await context.NewPageAsync();
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = (float)DefaultNavigationTimeout.TotalMilliseconds
            });

            var bytes = await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Type = ScreenshotType.Png,
                FullPage = true
            });

            await File.WriteAllBytesAsync(filePath, bytes, cancellationToken);

            results.Add(new DesignScreenshot(label, width, height, filePath, bytes));
        }

        return results;
    }

    private static string SanitizeLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return "breakpoint";
        }

        var builder = new StringBuilder(label.Length);
        foreach (var ch in label)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
            else if (ch == '-' || ch == '_')
            {
                builder.Append(ch);
            }
            else if (char.IsWhiteSpace(ch))
            {
                builder.Append('-');
            }
        }

        var sanitized = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "breakpoint" : sanitized;
    }
}
