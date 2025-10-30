using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WcScraper.Core;
using WcScraper.Core.Telemetry;
using WcScraper.Core.Tests.Telemetry;
using WcScraper.Wpf.Services;
using Xunit;

namespace WcScraper.Core.Tests;

public sealed class FrontEndDesignSnapshotTelemetryTests
{
    private const string OperationName = "FrontEndDesignSnapshot.Capture";
    private const string EntityType = "page";

    [Fact]
    public async Task CaptureAsync_EmitsSuccessTelemetryScopesAndMetrics()
    {
        using var telemetry = new TelemetryTestContext();

        const string baseUrl = "https://example.com";
        var handler = new SuccessHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);

        var retryPolicy = new HttpRetryPolicy(
            maxRetries: 2,
            baseDelay: TimeSpan.FromMilliseconds(1),
            logger: telemetry.LoggerFactory.CreateLogger<HttpRetryPolicy>());

        var progress = LoggerProgressAdapter.ForOperation(
            telemetry.CreateLogger<FrontEndDesignSnapshotTelemetryTests>(),
            callback: null,
            operationName: OperationName,
            url: baseUrl,
            entityType: EntityType);

        var result = await FrontEndDesignSnapshot.CaptureAsync(
            httpClient,
            baseUrl,
            additionalPageUrls: null,
            log: progress,
            cancellationToken: CancellationToken.None,
            retryPolicy: retryPolicy,
            loggerFactory: telemetry.LoggerFactory);

        Assert.NotNull(result);
        Assert.NotEmpty(result.Pages);

        var scope = Assert.Contains(telemetry.LoggerFactory.Scopes, record =>
            record.Values.TryGetValue("Operation", out var operation)
            && string.Equals(operation as string, OperationName, StringComparison.Ordinal)
            && record.Values.TryGetValue("Url", out var url)
            && string.Equals(url as string, handler.LastSuccessfulUrl, StringComparison.Ordinal));
        Assert.Equal(EntityType, Assert.IsType<string>(scope.Values["EntityType"]));

        var durationMeasurement = Assert.Single(telemetry.MeterListener.HistogramMeasurements.Where(m =>
            string.Equals(m.InstrumentName, "scraper.request.duration", StringComparison.Ordinal)
            && m.Tags.TryGetValue("operation", out var opTag)
            && string.Equals(opTag as string, OperationName, StringComparison.Ordinal)
            && m.Tags.TryGetValue("url", out var urlTag)
            && string.Equals(urlTag as string, handler.LastSuccessfulUrl, StringComparison.Ordinal)));

        Assert.Equal(EntityType, Assert.IsType<string>(durationMeasurement.Tags["entity"]));
        Assert.Equal(200, Assert.IsType<int>(durationMeasurement.Tags["http.status_code"]));

        var successMeasurement = Assert.Single(telemetry.MeterListener.CounterMeasurements.Where(m =>
            string.Equals(m.InstrumentName, "scraper.request.success", StringComparison.Ordinal)
            && m.Tags.TryGetValue("operation", out var opTag)
            && string.Equals(opTag as string, OperationName, StringComparison.Ordinal)
            && m.Tags.TryGetValue("url", out var urlTag)
            && string.Equals(urlTag as string, handler.LastSuccessfulUrl, StringComparison.Ordinal)));

        Assert.Equal(EntityType, Assert.IsType<string>(successMeasurement.Tags["entity"]));
        Assert.Equal(200, Assert.IsType<int>(successMeasurement.Tags["http.status_code"]));
    }

    [Fact]
    public async Task CaptureAsync_EmitsRetryAndFailureTelemetry()
    {
        using var telemetry = new TelemetryTestContext();

        const string baseUrl = "https://example.org";
        var handler = new RetryFailureHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);

        var retryPolicy = new HttpRetryPolicy(
            maxRetries: 1,
            baseDelay: TimeSpan.FromMilliseconds(1),
            logger: telemetry.LoggerFactory.CreateLogger<HttpRetryPolicy>());

        var progress = LoggerProgressAdapter.ForOperation(
            telemetry.CreateLogger<FrontEndDesignSnapshotTelemetryTests>(),
            callback: null,
            operationName: OperationName,
            url: baseUrl,
            entityType: EntityType);

        await Assert.ThrowsAsync<HttpRequestException>(() => FrontEndDesignSnapshot.CaptureAsync(
            httpClient,
            baseUrl,
            additionalPageUrls: null,
            log: progress,
            cancellationToken: CancellationToken.None,
            retryPolicy: retryPolicy,
            loggerFactory: telemetry.LoggerFactory));

        var retryLog = Assert.Single(telemetry.LoggerFactory.Logs.Where(record =>
            record.EventId == ScraperTelemetry.Events.RetryScheduled &&
            string.Equals(Convert.ToString(GetStateValue(record.State, "Operation"), CultureInfo.InvariantCulture), OperationName, StringComparison.Ordinal)));

        Assert.Equal(1, Convert.ToInt32(GetStateValue(retryLog.State, "Attempt"), CultureInfo.InvariantCulture));
        var delayValue = GetStateValue(retryLog.State, "DelayMs");
        Assert.True(Convert.ToDouble(delayValue, CultureInfo.InvariantCulture) >= 0);
        Assert.False(string.IsNullOrWhiteSpace(Convert.ToString(GetStateValue(retryLog.State, "Reason"), CultureInfo.InvariantCulture)));

        var retryAttemptMeasurement = Assert.Single(telemetry.MeterListener.CounterMeasurements.Where(m =>
            string.Equals(m.InstrumentName, "scraper.request.retry.attempt", StringComparison.Ordinal)
            && m.Tags.TryGetValue("operation", out var opTag)
            && string.Equals(opTag as string, OperationName, StringComparison.Ordinal)));

        Assert.Equal("scheduled", Assert.IsType<string>(retryAttemptMeasurement.Tags["retry.outcome"]));
        Assert.Equal(1, Convert.ToInt32(retryAttemptMeasurement.Tags["retry.count"], CultureInfo.InvariantCulture));
        Assert.False(string.IsNullOrWhiteSpace(Convert.ToString(retryAttemptMeasurement.Tags["retry.reason"], CultureInfo.InvariantCulture)));

        var retryOutcomeLog = Assert.Single(telemetry.LoggerFactory.Logs.Where(record =>
            record.EventId == ScraperTelemetry.Events.RetryOutcome &&
            string.Equals(Convert.ToString(GetStateValue(record.State, "Operation"), CultureInfo.InvariantCulture), OperationName, StringComparison.Ordinal)));

        Assert.Equal("failure", Convert.ToString(GetStateValue(retryOutcomeLog.State, "Outcome"), CultureInfo.InvariantCulture));
        Assert.Equal(EntityType, Convert.ToString(GetStateValue(retryOutcomeLog.State, "EntityType"), CultureInfo.InvariantCulture));

        var failureMeasurement = Assert.Single(telemetry.MeterListener.CounterMeasurements.Where(m =>
            string.Equals(m.InstrumentName, "scraper.request.failure", StringComparison.Ordinal)
            && m.Tags.TryGetValue("operation", out var opTag)
            && string.Equals(opTag as string, OperationName, StringComparison.Ordinal)));

        Assert.Equal(EntityType, Assert.IsType<string>(failureMeasurement.Tags["entity"]));
        Assert.Equal(500, Assert.IsType<int>(failureMeasurement.Tags["http.status_code"]));

        var durationMeasurement = Assert.Single(telemetry.MeterListener.HistogramMeasurements.Where(m =>
            string.Equals(m.InstrumentName, "scraper.request.duration", StringComparison.Ordinal)
            && m.Tags.TryGetValue("operation", out var opTag)
            && string.Equals(opTag as string, OperationName, StringComparison.Ordinal)
            && m.Tags.TryGetValue("http.status_code", out var statusTag)
            && Convert.ToInt32(statusTag, CultureInfo.InvariantCulture) == 500));

        Assert.Equal(handler.FailedUrl, Assert.IsType<string>(durationMeasurement.Tags["url"]));
    }

    private sealed class SuccessHandler : HttpMessageHandler
    {
        private int _pageAttempts;

        public string? LastSuccessfulUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url.EndsWith("/", StringComparison.Ordinal))
            {
                var attempt = Interlocked.Increment(ref _pageAttempts);
                if (attempt == 1)
                {
                    var retry = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    retry.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(5));
                    return Task.FromResult(retry);
                }

                LastSuccessfulUrl = url;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><head><link rel=\"stylesheet\" href=\"/styles/site.css\"></head><body>ok</body></html>", Encoding.UTF8, "text/html")
                });
            }

            if (url.EndsWith("/styles/site.css", StringComparison.Ordinal))
            {
                LastSuccessfulUrl = url;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("body { color: black; }", Encoding.UTF8, "text/css")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class RetryFailureHandler : HttpMessageHandler
    {
        private int _attempts;

        public string FailedUrl { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (_attempts == 0)
            {
                _attempts++;
                var retry = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                retry.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(10));
                return Task.FromResult(retry);
            }

            FailedUrl = url;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("error", Encoding.UTF8, "text/plain")
            });
        }
    }

    private static object? GetStateValue(IReadOnlyList<KeyValuePair<string, object?>> values, string key)
    {
        foreach (var kvp in values)
        {
            if (string.Equals(kvp.Key, key, StringComparison.Ordinal))
            {
                return kvp.Value;
            }
        }

        return null;
    }
}
