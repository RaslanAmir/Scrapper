using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WcScraper.Core.Telemetry;

namespace WcScraper.Core;

public sealed class WordPressDirectoryClient
{
    private const string PluginEndpoint = "https://api.wordpress.org/plugins/info/1.2/";
    private const string ThemeEndpoint = "https://api.wordpress.org/themes/info/1.2/";
    private static readonly Regex DirectorySlugRegex = new("^[a-z0-9-]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly HttpClient _httpClient;
    private readonly ILogger<WordPressDirectoryClient> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IScraperInstrumentation _instrumentation;
    private HttpRetryPolicy _retryPolicy;

    public WordPressDirectoryClient(
        HttpClient httpClient,
        HttpRetryPolicy? retryPolicy = null,
        ILogger<WordPressDirectoryClient>? logger = null,
        IScraperInstrumentation? instrumentation = null,
        ILoggerFactory? loggerFactory = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = logger ?? _loggerFactory.CreateLogger<WordPressDirectoryClient>();
        _instrumentation = instrumentation ?? ScraperInstrumentation.Create(_logger);
        _retryPolicy = retryPolicy ?? new HttpRetryPolicy(logger: _loggerFactory.CreateLogger<HttpRetryPolicy>());
    }

    public HttpRetryPolicy RetryPolicy
    {
        get => _retryPolicy;
        set => _retryPolicy = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static bool IsLikelyDirectorySlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return false;
        }

        slug = slug.Trim();
        if (!DirectorySlugRegex.IsMatch(slug))
        {
            return false;
        }

        // Common premium bundle markers that are unlikely to exist in the public directory.
        if (slug.Contains("nulled", StringComparison.Ordinal) ||
            slug.Contains("gpl", StringComparison.Ordinal) ||
            slug.Contains("codecanyon", StringComparison.Ordinal) ||
            slug.Contains("themeforest", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    public async Task<WordPressDirectoryEntry?> GetPluginAsync(
        string slug,
        CancellationToken cancellationToken = default)
    {
        return await GetAsync(PluginEndpoint, "plugin_information", slug, ParsePluginAsync, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<WordPressDirectoryEntry?> GetThemeAsync(
        string slug,
        CancellationToken cancellationToken = default)
    {
        return await GetAsync(ThemeEndpoint, "theme_information", slug, ParseThemeAsync, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<WordPressDirectoryEntry?> GetAsync(
        string endpoint,
        string action,
        string slug,
        Func<Stream, CancellationToken, Task<WordPressDirectoryEntry?>> parser,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        var requestUri = BuildRequestUri(endpoint, action, slug);
        var requestName = $"WordPressDirectory.{action}";
        _logger.LogDebug("Requesting WordPress directory entry for '{Slug}' via {Endpoint}", slug, endpoint);

        var entityType = action switch
        {
            "plugin_information" => "plugin",
            "theme_information" => "theme",
            _ => action
        };

        var context = new ScraperOperationContext(requestName, requestUri, entityType: entityType);

        using var scope = _instrumentation.BeginScope(context);
        _instrumentation.RecordRequestStart(context);

        var stopwatch = Stopwatch.StartNew();
        var retryCount = 0;
        HttpResponseMessage? response = null;

        try
        {
            response = await _retryPolicy.SendAsync(
                    () => _httpClient.GetAsync(requestUri, cancellationToken),
                    cancellationToken,
                    attempt =>
                    {
                        retryCount = attempt.AttemptNumber;
                        var retryContext = context with
                        {
                            RetryAttempt = attempt.AttemptNumber,
                            RetryDelay = attempt.Delay,
                            RetryReason = attempt.Reason
                        };

                        using var retryScope = _instrumentation.BeginScope(retryContext);
                        _instrumentation.RecordRetry(retryContext);
                    })
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                stopwatch.Stop();
                _instrumentation.RecordRequestSuccess(context, stopwatch.Elapsed, response.StatusCode, retryCount);
                return null;
            }

            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var entry = await parser(stream, cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();
            _instrumentation.RecordRequestSuccess(context, stopwatch.Elapsed, response.StatusCode, retryCount);
            return entry;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var statusCode = response?.StatusCode ?? (ex as HttpRequestException)?.StatusCode;
            _instrumentation.RecordRequestFailure(context, stopwatch.Elapsed, ex, statusCode, retryCount);
            throw;
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static string BuildRequestUri(string endpoint, string action, string slug)
    {
        var separator = endpoint.Contains('?') ? '&' : '?';
        var builder = new StringBuilder(endpoint);
        builder.Append(separator);
        builder.Append("action=").Append(Uri.EscapeDataString(action));
        builder.Append("&request%5Bslug%5D=").Append(Uri.EscapeDataString(slug));
        builder.Append("&request%5Bfields%5D%5Bsections%5D=0");
        builder.Append("&request%5Bfields%5D%5Bdescription%5D=0");
        builder.Append("&request%5Bfields%5D%5Brequires%5D=0");
        builder.Append("&request%5Bfields%5D%5Brating%5D=0");
        builder.Append("&request%5Bfields%5D%5Bactive_installs%5D=0");
        builder.Append("&request%5Bfields%5D%5Bdownloaded%5D=0");
        return builder.ToString();
    }

    private static async Task<WordPressDirectoryEntry?> ParsePluginAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("error", out _))
        {
            return null;
        }

        var entry = new WordPressDirectoryEntry
        {
            Slug = root.TryGetProperty("slug", out var slugElement) ? slugElement.GetString() : null,
            Title = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null,
            Version = root.TryGetProperty("version", out var versionElement) ? versionElement.GetString() : null,
            Homepage = root.TryGetProperty("homepage", out var homepageElement) ? homepageElement.GetString() : null,
            DownloadUrl = root.TryGetProperty("download_link", out var downloadElement) ? downloadElement.GetString() : null
        };

        if (string.IsNullOrWhiteSpace(entry.Homepage) && root.TryGetProperty("plugin_url", out var pluginUrlElement))
        {
            entry.Homepage = pluginUrlElement.GetString();
        }

        if (root.TryGetProperty("author", out var authorElement))
        {
            entry.Author = NormalizeAuthor(authorElement.GetString());
        }

        return entry;
    }

    private static async Task<WordPressDirectoryEntry?> ParseThemeAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("error", out _))
        {
            return null;
        }

        var entry = new WordPressDirectoryEntry
        {
            Slug = root.TryGetProperty("slug", out var slugElement) ? slugElement.GetString() : null,
            Title = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null,
            Version = root.TryGetProperty("version", out var versionElement) ? versionElement.GetString() : null,
            Homepage = root.TryGetProperty("homepage", out var homepageElement) ? homepageElement.GetString() : null,
            DownloadUrl = root.TryGetProperty("download_link", out var downloadElement) ? downloadElement.GetString() : null
        };

        if (root.TryGetProperty("author", out var authorElement) && authorElement.ValueKind == JsonValueKind.Object)
        {
            if (authorElement.TryGetProperty("author", out var authorString))
            {
                entry.Author = NormalizeAuthor(authorString.GetString());
            }
            else if (authorElement.TryGetProperty("display_name", out var displayName))
            {
                entry.Author = NormalizeAuthor(displayName.GetString());
            }
        }

        return entry;
    }

    private static string? NormalizeAuthor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        value = value.Trim();

        if (value.Contains('<') && value.Contains('>'))
        {
            value = Regex.Replace(value, "<.*?>", string.Empty, RegexOptions.Singleline);
        }

        value = WebUtility.HtmlDecode(value);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

public sealed class WordPressDirectoryEntry
{
    public string? Slug { get; set; }
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Homepage { get; set; }
    public string? Version { get; set; }
    public string? DownloadUrl { get; set; }
}
