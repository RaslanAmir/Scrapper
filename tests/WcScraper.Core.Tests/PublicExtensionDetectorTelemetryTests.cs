using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using WcScraper.Core;
using WcScraper.Core.Tests.Telemetry;
using WcScraper.Wpf.Services;
using Xunit;

namespace WcScraper.Core.Tests;

public sealed class PublicExtensionDetectorTelemetryTests
{
    private const string OperationName = "PublicExtensionDetector.Detect";

    [Fact]
    public async Task DetectAsync_EmitsRetryTelemetryWhenFollowingLinkedAssets()
    {
        using var telemetry = new TelemetryTestContext();

        const string baseUrl = "https://example.com/";
        const string assetUrl = "https://example.com/wp-content/plugins/sample-plugin/main.js";

        var html = """
<html>
    <head>
        <script src="/wp-content/plugins/sample-plugin/main.js"></script>
    </head>
    <body>
        <h1>Sample</h1>
    </body>
</html>
""";

        using var handler = new RetryAfterHandler(baseUrl, assetUrl, html);
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var retryPolicy = new HttpRetryPolicy(
            maxRetries: 2,
            baseDelay: TimeSpan.FromMilliseconds(1),
            maxDelay: TimeSpan.FromMilliseconds(5),
            logger: telemetry.LoggerFactory.CreateLogger<HttpRetryPolicy>());

        using var detector = new PublicExtensionDetector(
            httpClient,
            httpRetryPolicy: retryPolicy,
            logger: telemetry.LoggerFactory.CreateLogger<PublicExtensionDetector>(),
            loggerFactory: telemetry.LoggerFactory);

        var progress = LoggerProgressAdapter.ForOperation(
            telemetry.CreateLogger<PublicExtensionDetectorTelemetryTests>(),
            callback: null,
            operationName: OperationName,
            url: baseUrl,
            entityType: "page");

        var findings = await detector.DetectAsync(baseUrl, followLinkedAssets: true, log: progress);

        Assert.NotEmpty(findings);
        Assert.Equal(2, handler.BaseRequestAttempts);

        var retryScope = Assert.Single(telemetry.LoggerFactory.Scopes.Where(scope =>
            scope.Values.ContainsKey("RetryAttempt") && scope.Values.ContainsKey("RetryReason")));

        Assert.Equal(1, Convert.ToInt32(retryScope.Values["RetryAttempt"], CultureInfo.InvariantCulture));
        Assert.True(TryGetDelayMilliseconds(retryScope.Values, out var scopeDelayMs) && scopeDelayMs > 0);
        Assert.False(string.IsNullOrWhiteSpace(Convert.ToString(retryScope.Values["RetryReason"], CultureInfo.InvariantCulture)));

        var retryAttemptMeasurement = Assert.Single(telemetry.MeterListener.CounterMeasurements
            .Where(m => string.Equals(m.InstrumentName, "scraper.request.retry.attempt", StringComparison.Ordinal)));

        Assert.Equal(1, retryAttemptMeasurement.Value);
        Assert.Equal(OperationName, Assert.IsType<string>(retryAttemptMeasurement.Tags["operation"]));
        Assert.Equal(baseUrl, Assert.IsType<string>(retryAttemptMeasurement.Tags["url"]));
        Assert.Equal("scheduled", Assert.IsType<string>(retryAttemptMeasurement.Tags["retry.outcome"]));
        Assert.Equal(1, Convert.ToInt32(retryAttemptMeasurement.Tags["retry.count"], CultureInfo.InvariantCulture));
        Assert.True(TryGetDelayMilliseconds(retryAttemptMeasurement.Tags, out var attemptDelayMs) && attemptDelayMs > 0);
        Assert.False(string.IsNullOrWhiteSpace(Convert.ToString(retryAttemptMeasurement.Tags["retry.reason"], CultureInfo.InvariantCulture)));

        var retryOutcomeMeasurement = Assert.Single(telemetry.MeterListener.CounterMeasurements
            .Where(m => string.Equals(m.InstrumentName, "scraper.request.retry.outcome", StringComparison.Ordinal)));

        Assert.Equal(1, retryOutcomeMeasurement.Value);
        Assert.Equal(OperationName, Assert.IsType<string>(retryOutcomeMeasurement.Tags["operation"]));
        Assert.Equal(baseUrl, Assert.IsType<string>(retryOutcomeMeasurement.Tags["url"]));
        Assert.Equal("success", Assert.IsType<string>(retryOutcomeMeasurement.Tags["retry.outcome"]));
        Assert.Equal(1, Convert.ToInt32(retryOutcomeMeasurement.Tags["retry.count"], CultureInfo.InvariantCulture));
    }

    private static bool TryGetDelayMilliseconds(IReadOnlyDictionary<string, object?> values, out double delayMs)
    {
        if (values.TryGetValue("RetryDelay", out var scopeDelay) && TryConvertToDouble(scopeDelay, out delayMs))
        {
            return true;
        }

        if (values.TryGetValue("RetryDelayMs", out scopeDelay) && TryConvertToDouble(scopeDelay, out delayMs))
        {
            return true;
        }

        if (values.TryGetValue("retry.delay_ms", out scopeDelay) && TryConvertToDouble(scopeDelay, out delayMs))
        {
            return true;
        }

        delayMs = 0;
        return false;
    }

    private static bool TryConvertToDouble(object? value, out double result)
    {
        switch (value)
        {
            case null:
                result = 0;
                return false;
            case double d:
                result = d;
                return true;
            case float f:
                result = f;
                return true;
            case int i:
                result = i;
                return true;
            case long l:
                result = l;
                return true;
            case TimeSpan ts:
                result = ts.TotalMilliseconds;
                return true;
            case string s when double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed):
                result = parsed;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private sealed class RetryAfterHandler : HttpMessageHandler
    {
        private readonly string _baseUrl;
        private readonly string _assetUrl;
        private readonly string _html;
        private int _baseAttempts;

        public RetryAfterHandler(string baseUrl, string assetUrl, string html)
        {
            _baseUrl = baseUrl;
            _assetUrl = assetUrl;
            _html = html;
        }

        public int BaseRequestAttempts => Volatile.Read(ref _baseAttempts);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (string.Equals(url, _baseUrl, StringComparison.OrdinalIgnoreCase))
            {
                var attempt = Interlocked.Increment(ref _baseAttempts);
                if (attempt == 1)
                {
                    var retry = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    retry.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(5));
                    return Task.FromResult(retry);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_html)
                });
            }

            if (string.Equals(url, _assetUrl, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(string.Empty)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
