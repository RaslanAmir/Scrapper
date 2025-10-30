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
using Xunit;

namespace WcScraper.Core.Tests;

public sealed class WordPressDirectoryClientTests
{
    [Fact]
    public async Task GetPluginAsync_RecordsTelemetryForNotFoundResponse()
    {
        using var telemetry = new TelemetryTestContext();
        using var handler = new SequenceHandler(new[]
        {
            CreateTooManyRequestsResponse(),
            new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var retryPolicy = new HttpRetryPolicy(
            maxRetries: 1,
            baseDelay: TimeSpan.FromMilliseconds(1),
            maxDelay: TimeSpan.FromMilliseconds(5),
            logger: telemetry.LoggerFactory.CreateLogger<HttpRetryPolicy>());
        var logger = telemetry.LoggerFactory.CreateLogger<WordPressDirectoryClient>();
        var instrumentation = new ScraperInstrumentation(logger);

        var client = new WordPressDirectoryClient(
            httpClient,
            retryPolicy,
            logger: logger,
            instrumentation: instrumentation,
            loggerFactory: telemetry.LoggerFactory);

        var result = await client.GetPluginAsync("missing-plugin");

        Assert.Null(result);
        Assert.Equal(2, handler.CallCount);

        AssertTelemetry(
            telemetry,
            "WordPressDirectory.plugin_information",
            "plugin",
            handler.RequestUris,
            retryExpected: true);

        var successMeasurement = Assert.Single(telemetry.MeterListener.CounterMeasurements
            .Where(m => string.Equals(m.InstrumentName, "scraper.request.success", StringComparison.Ordinal)));

        Assert.Equal(1, successMeasurement.Value);
        Assert.Equal("WordPressDirectory.plugin_information", Assert.IsType<string>(successMeasurement.Tags["operation"]));
        Assert.Equal(handler.RequestUris.Last().ToString(), Assert.IsType<string>(successMeasurement.Tags["url"]));
        Assert.Equal("plugin", Assert.IsType<string>(successMeasurement.Tags["entity"]));
        Assert.Equal(404, Convert.ToInt32(successMeasurement.Tags["http.status_code"], CultureInfo.InvariantCulture));
        Assert.Equal(1, Convert.ToInt32(successMeasurement.Tags["retry.count"], CultureInfo.InvariantCulture));

        Assert.Empty(telemetry.MeterListener.CounterMeasurements
            .Where(m => string.Equals(m.InstrumentName, "scraper.request.failure", StringComparison.Ordinal)));

        var durationMeasurement = Assert.Single(telemetry.MeterListener.HistogramMeasurements
            .Where(m => string.Equals(m.InstrumentName, "scraper.request.duration", StringComparison.Ordinal)));

        Assert.True(durationMeasurement.Value >= 0);
        Assert.Equal("WordPressDirectory.plugin_information", Assert.IsType<string>(durationMeasurement.Tags["operation"]));
        Assert.Equal(handler.RequestUris.Last().ToString(), Assert.IsType<string>(durationMeasurement.Tags["url"]));
        Assert.Equal("plugin", Assert.IsType<string>(durationMeasurement.Tags["entity"]));
        Assert.Equal(404, Convert.ToInt32(durationMeasurement.Tags["http.status_code"], CultureInfo.InvariantCulture));
        Assert.Equal(1, Convert.ToInt32(durationMeasurement.Tags["retry.count"], CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task GetThemeAsync_RecordsTelemetryForSuccessfulResponse()
    {
        using var telemetry = new TelemetryTestContext();
        using var handler = new SequenceHandler(new[]
        {
            CreateTooManyRequestsResponse(),
            CreateThemeResponse()
        });
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var retryPolicy = new HttpRetryPolicy(
            maxRetries: 1,
            baseDelay: TimeSpan.FromMilliseconds(1),
            maxDelay: TimeSpan.FromMilliseconds(5),
            logger: telemetry.LoggerFactory.CreateLogger<HttpRetryPolicy>());
        var logger = telemetry.LoggerFactory.CreateLogger<WordPressDirectoryClient>();
        var instrumentation = new ScraperInstrumentation(logger);

        var client = new WordPressDirectoryClient(
            httpClient,
            retryPolicy,
            logger: logger,
            instrumentation: instrumentation,
            loggerFactory: telemetry.LoggerFactory);

        var result = await client.GetThemeAsync("test-theme");

        Assert.NotNull(result);
        Assert.Equal("test-theme", result!.Slug);
        Assert.Equal("Test Theme", result.Title);
        Assert.Equal(2, handler.CallCount);

        AssertTelemetry(
            telemetry,
            "WordPressDirectory.theme_information",
            "theme",
            handler.RequestUris,
            retryExpected: true);

        var successMeasurement = Assert.Single(telemetry.MeterListener.CounterMeasurements
            .Where(m => string.Equals(m.InstrumentName, "scraper.request.success", StringComparison.Ordinal)));

        Assert.Equal(1, successMeasurement.Value);
        Assert.Equal("WordPressDirectory.theme_information", Assert.IsType<string>(successMeasurement.Tags["operation"]));
        Assert.Equal(handler.RequestUris.Last().ToString(), Assert.IsType<string>(successMeasurement.Tags["url"]));
        Assert.Equal("theme", Assert.IsType<string>(successMeasurement.Tags["entity"]));
        Assert.Equal(200, Convert.ToInt32(successMeasurement.Tags["http.status_code"], CultureInfo.InvariantCulture));
        Assert.Equal(1, Convert.ToInt32(successMeasurement.Tags["retry.count"], CultureInfo.InvariantCulture));

        Assert.Empty(telemetry.MeterListener.CounterMeasurements
            .Where(m => string.Equals(m.InstrumentName, "scraper.request.failure", StringComparison.Ordinal)));

        var durationMeasurement = Assert.Single(telemetry.MeterListener.HistogramMeasurements
            .Where(m => string.Equals(m.InstrumentName, "scraper.request.duration", StringComparison.Ordinal)));

        Assert.True(durationMeasurement.Value >= 0);
        Assert.Equal("WordPressDirectory.theme_information", Assert.IsType<string>(durationMeasurement.Tags["operation"]));
        Assert.Equal(handler.RequestUris.Last().ToString(), Assert.IsType<string>(durationMeasurement.Tags["url"]));
        Assert.Equal("theme", Assert.IsType<string>(durationMeasurement.Tags["entity"]));
        Assert.Equal(200, Convert.ToInt32(durationMeasurement.Tags["http.status_code"], CultureInfo.InvariantCulture));
        Assert.Equal(1, Convert.ToInt32(durationMeasurement.Tags["retry.count"], CultureInfo.InvariantCulture));
    }

    private static void AssertTelemetry(
        TelemetryTestContext telemetry,
        string expectedOperation,
        string expectedEntity,
        IReadOnlyList<Uri> requestUris,
        bool retryExpected)
    {
        var scopes = telemetry.LoggerFactory.Scopes;
        Assert.NotEmpty(requestUris);

        var baseScope = scopes.FirstOrDefault(scope =>
            string.Equals(scope.Category, typeof(WordPressDirectoryClient).FullName, StringComparison.Ordinal) &&
            scope.Values.TryGetValue("Operation", out var operation) &&
            string.Equals(Convert.ToString(operation, CultureInfo.InvariantCulture), expectedOperation, StringComparison.Ordinal));

        Assert.NotNull(baseScope);
        Assert.Equal(expectedOperation, Assert.IsType<string>(baseScope!.Values["Operation"]));
        Assert.Equal(requestUris.First().ToString(), Assert.IsType<string>(baseScope.Values["Url"]));
        Assert.Equal(expectedEntity, Assert.IsType<string>(baseScope.Values["EntityType"]));

        var logs = telemetry.LoggerFactory.Logs;

        if (retryExpected)
        {
            var retryLog = Assert.Single(logs.Where(record =>
                record.EventId == ScraperTelemetry.Events.RetryScheduled &&
                string.Equals(Convert.ToString(GetStateValue(record.State, "Operation"), CultureInfo.InvariantCulture), expectedOperation, StringComparison.Ordinal)));

            Assert.Equal(1, Convert.ToInt32(GetStateValue(retryLog.State, "Attempt"), CultureInfo.InvariantCulture));
            var delayValue = GetStateValue(retryLog.State, "DelayMs");
            Assert.True(Convert.ToDouble(delayValue, CultureInfo.InvariantCulture) >= 0);
            Assert.False(string.IsNullOrWhiteSpace(Convert.ToString(GetStateValue(retryLog.State, "Reason"), CultureInfo.InvariantCulture)));

            var outcomeLog = Assert.Single(logs.Where(record =>
                record.EventId == ScraperTelemetry.Events.RetryOutcome &&
                string.Equals(Convert.ToString(GetStateValue(record.State, "Operation"), CultureInfo.InvariantCulture), expectedOperation, StringComparison.Ordinal)));

            Assert.Equal("success", Convert.ToString(GetStateValue(outcomeLog.State, "Outcome"), CultureInfo.InvariantCulture));
            Assert.Equal(expectedEntity, Convert.ToString(GetStateValue(outcomeLog.State, "EntityType"), CultureInfo.InvariantCulture));
        }
    }

    private static bool TryGetDelayMilliseconds(IReadOnlyDictionary<string, object?> values, out double delayMs)
    {
        if (values.TryGetValue("RetryDelayMs", out var delayValue) && ConvertToDouble(delayValue, out delayMs))
        {
            return true;
        }

        if (values.TryGetValue("RetryDelay", out delayValue) && ConvertToDouble(delayValue, out delayMs))
        {
            return true;
        }

        delayMs = 0;
        return false;
    }

    private static bool ConvertToDouble(object? value, out double result)
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

    private static HttpResponseMessage CreateTooManyRequestsResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
        return response;
    }

    private static HttpResponseMessage CreateThemeResponse()
    {
        const string payload = """
        {
            "slug": "test-theme",
            "name": "Test Theme",
            "version": "1.0.0",
            "homepage": "https://example.com",
            "download_link": "https://example.com/download"
        }
        """;

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public SequenceHandler(IEnumerable<HttpResponseMessage> responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public int CallCount { get; private set; }

        public List<Uri> RequestUris { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (request.RequestUri is { } uri)
            {
                RequestUris.Add(uri);
            }

            if (_responses.Count == 0)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(_responses.Dequeue());
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                while (_responses.Count > 0)
                {
                    _responses.Dequeue().Dispose();
                }
            }

            base.Dispose(disposing);
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
