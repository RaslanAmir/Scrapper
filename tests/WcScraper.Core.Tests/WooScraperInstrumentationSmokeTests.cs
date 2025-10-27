using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using WcScraper.Core;
using WcScraper.Core.Telemetry;
using WcScraper.Wpf.Services;
using Xunit;

namespace WcScraper.Core.Tests;

public sealed class WooScraperInstrumentationSmokeTests
{
    [Fact]
    public async Task WooScraper_LogsAndActivitiesFlowThroughInjectedProviders()
    {
        using var harness = new InstrumentationHarness();
        var instrumentationOptions = harness.CreateOptions();

        const string baseUrl = "https://example.com";
        const string requestUrl = $"{baseUrl}/wp-json/wc/store/v1/products?per_page=100&page=1";
        const string operationName = "WooScraper.FetchStoreProducts";

        const string payload = """
        [
          {
            "id": 1,
            "name": "Example",
            "description": "<p>Example</p>",
            "short_description": "<p>Short</p>",
            "summary": "<p>Summary</p>",
            "tags": [],
            "images": []
          }
        ]
        """;

        using var handler = new SequenceHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        });

        using var httpClient = new HttpClient(handler, disposeHandler: true);

        var scraper = new WooScraper(
            httpClient,
            allowLegacyTls: false,
            loggerFactory: harness.LoggerFactory,
            instrumentationOptions: instrumentationOptions);

        var progress = LoggerProgressAdapter.ForOperation(
            harness.LoggerFactory.CreateLogger<WooScraperInstrumentationSmokeTests>(),
            callback: null,
            operationName: operationName,
            url: requestUrl,
            entityType: "product",
            instrumentationOptions: instrumentationOptions);

        var products = await scraper.FetchStoreProductsAsync(baseUrl, perPage: 100, maxPages: 1, log: progress);

        Assert.Single(products);

        harness.Flush();

        var logEvents = harness.LogEvents;
        Assert.Contains(logEvents, evt => evt.RenderMessage().Contains($"GET {requestUrl}", StringComparison.Ordinal));

        var activity = Assert.Single(harness.ExportedActivities.Where(a => string.Equals(a.DisplayName, operationName, StringComparison.Ordinal)));
        Assert.Equal(requestUrl, activity.GetTagItem("url"));
        Assert.Equal("product", activity.GetTagItem("entity"));
    }

    [Fact]
    public async Task WordPressDirectoryClient_SuccessScenario_EmitsTelemetry()
    {
        using var harness = new InstrumentationHarness();
        var options = harness.CreateOptions();

        const string payload = """
        {
          "slug": "jetpack",
          "name": "Jetpack",
          "version": "12.0",
          "homepage": "https://jetpack.com",
          "download_link": "https://downloads.wordpress.org/plugin/jetpack.zip"
        }
        """;

        using var handler = new SequenceHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        });

        using var httpClient = new HttpClient(handler, disposeHandler: true);

        var client = new WordPressDirectoryClient(
            httpClient,
            loggerFactory: harness.LoggerFactory,
            instrumentationOptions: options);

        var entry = await client.GetPluginAsync("jetpack");
        Assert.NotNull(entry);

        harness.Flush();

        var logEvents = harness.LogEvents;
        Assert.Contains(logEvents, evt => evt.RenderMessage().Contains("Starting request WordPressDirectory.plugin_information", StringComparison.Ordinal));
        Assert.Contains(logEvents, evt => evt.RenderMessage().Contains("Request WordPressDirectory.plugin_information targeting", StringComparison.Ordinal));

        var metrics = harness.Metrics;
        var durationMetric = Assert.Single(metrics.Where(m => string.Equals(m.Name, "scraper.request.duration", StringComparison.Ordinal)));
        var durationPoint = GetSingleMetricPoint(durationMetric);
        Assert.True(durationPoint.GetHistogramSum() > 0);
        Assert.Equal("WordPressDirectory.plugin_information", Assert.IsType<string>(GetTagValue(durationPoint, "operation")));
        Assert.Equal("plugin", Assert.IsType<string>(GetTagValue(durationPoint, "entity")));

        var successMetric = Assert.Single(metrics.Where(m => string.Equals(m.Name, "scraper.request.success", StringComparison.Ordinal)));
        var successPoint = GetSingleMetricPoint(successMetric);
        Assert.Equal(1, successPoint.GetSumLong());
        Assert.Equal("WordPressDirectory.plugin_information", Assert.IsType<string>(GetTagValue(successPoint, "operation")));
        Assert.Equal("plugin", Assert.IsType<string>(GetTagValue(successPoint, "entity")));

        var activity = Assert.Single(harness.ExportedActivities.Where(a => string.Equals(a.DisplayName, "WordPressDirectory.plugin_information", StringComparison.Ordinal)));
        Assert.Equal("plugin", activity.GetTagItem("entity"));
        Assert.Equal("https://api.wordpress.org/plugins/info/1.2/?action=plugin_information&request%5Bslug%5D=jetpack&request%5Bfields%5D%5Bsections%5D=0&request%5Bfields%5D%5Bdescription%5D=0&request%5Bfields%5D%5Brequires%5D=0&request%5Bfields%5D%5Brating%5D=0&request%5Bfields%5D%5Bactive_installs%5D=0&request%5Bfields%5D%5Bdownloaded%5D=0", activity.GetTagItem("url"));
    }

    [Fact]
    public async Task WordPressDirectoryClient_RetryScenario_EmitsRetryTelemetry()
    {
        using var harness = new InstrumentationHarness();
        var options = harness.CreateOptions();

        const string payload = """
        {
          "slug": "woocommerce",
          "name": "WooCommerce",
          "version": "9.0",
          "homepage": "https://woocommerce.com",
          "download_link": "https://downloads.wordpress.org/plugin/woocommerce.zip"
        }
        """;

        var attempt = 0;
        using var handler = new SequenceHttpMessageHandler(_ =>
        {
            attempt++;
            if (attempt == 1)
            {
                var retryResponse = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    ReasonPhrase = "Too Many Requests",
                    Content = new StringContent("Slow down", Encoding.UTF8, "text/plain")
                };
                retryResponse.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
                return retryResponse;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler, disposeHandler: true);

        var client = new WordPressDirectoryClient(
            httpClient,
            loggerFactory: harness.LoggerFactory,
            instrumentationOptions: options)
        {
            RetryPolicy = new HttpRetryPolicy(
                maxRetries: 2,
                baseDelay: TimeSpan.FromMilliseconds(1),
                maxDelay: TimeSpan.FromMilliseconds(5),
                logger: harness.LoggerFactory.CreateLogger<HttpRetryPolicy>(),
                instrumentationOptions: options)
        };

        var entry = await client.GetPluginAsync("woocommerce");
        Assert.NotNull(entry);

        harness.Flush();

        var logEvents = harness.LogEvents;
        Assert.Contains(logEvents, evt => evt.RenderMessage().Contains("Retrying WordPressDirectory.plugin_information", StringComparison.Ordinal));
        Assert.Contains(logEvents, evt => evt.RenderMessage().Contains("Request WordPressDirectory.plugin_information targeting", StringComparison.Ordinal));

        var metrics = harness.Metrics;
        var retryAttemptMetric = Assert.Single(metrics.Where(m => string.Equals(m.Name, "scraper.request.retry.attempt", StringComparison.Ordinal)));
        var retryAttemptPoint = GetSingleMetricPoint(retryAttemptMetric);
        Assert.Equal(1, retryAttemptPoint.GetSumLong());
        Assert.Equal("WordPressDirectory.plugin_information", Assert.IsType<string>(GetTagValue(retryAttemptPoint, "operation")));
        Assert.Equal(1, Convert.ToInt32(GetTagValue(retryAttemptPoint, "retry.count")));
        Assert.Equal("scheduled", Assert.IsType<string>(GetTagValue(retryAttemptPoint, "retry.outcome")));

        var retryOutcomeMetric = Assert.Single(metrics.Where(m => string.Equals(m.Name, "scraper.request.retry.outcome", StringComparison.Ordinal)));
        var retryOutcomePoint = GetSingleMetricPoint(retryOutcomeMetric);
        Assert.Equal(1, retryOutcomePoint.GetSumLong());
        Assert.Equal("WordPressDirectory.plugin_information", Assert.IsType<string>(GetTagValue(retryOutcomePoint, "operation")));
        Assert.Equal("success", Assert.IsType<string>(GetTagValue(retryOutcomePoint, "retry.outcome")));
        Assert.Equal(1, Convert.ToInt32(GetTagValue(retryOutcomePoint, "retry.count")));

        var successMetric = Assert.Single(metrics.Where(m => string.Equals(m.Name, "scraper.request.success", StringComparison.Ordinal)));
        var successPoint = GetSingleMetricPoint(successMetric);
        Assert.Equal(1, successPoint.GetSumLong());
        Assert.Equal(1, Convert.ToInt32(GetTagValue(successPoint, "retry.count")));

        var activity = Assert.Single(harness.ExportedActivities.Where(a => string.Equals(a.DisplayName, "WordPressDirectory.plugin_information", StringComparison.Ordinal)));
        Assert.Equal(1, Convert.ToInt32(activity.GetTagItem("retry.count")));
        Assert.Contains(activity.Events, e => string.Equals(e.Name, "ScraperRequestRetry", StringComparison.Ordinal));
        Assert.Contains(activity.Events, e => string.Equals(e.Name, "ScraperRequestSuccess", StringComparison.Ordinal));
    }

    private static MetricPoint GetSingleMetricPoint(Metric metric)
    {
        var points = new List<MetricPoint>();
        foreach (var point in metric.GetMetricPoints())
        {
            points.Add(point);
        }

        return Assert.Single(points);
    }

    private static object? GetTagValue(MetricPoint metricPoint, string tagName)
    {
        foreach (var tag in metricPoint.Tags)
        {
            if (string.Equals(tag.Key, tagName, StringComparison.Ordinal))
            {
                return tag.Value;
            }
        }

        return null;
    }

    private sealed class SequenceHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders;
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fallback;

        public SequenceHttpMessageHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responders)
        {
            if (responders is null || responders.Length == 0)
            {
                throw new ArgumentException("At least one responder must be provided.", nameof(responders));
            }

            _responders = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responders);
            _fallback = responders[^1];
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var responder = _responders.Count > 0 ? _responders.Dequeue() : _fallback;
            var response = responder(request);
            return Task.FromResult(response);
        }
    }

    private sealed class InstrumentationHarness : IDisposable
    {
        private readonly Logger _logger;
        private readonly InMemorySink _sink;
        private readonly List<Metric> _metrics = new();
        private readonly List<Activity> _activities = new();

        public InstrumentationHarness()
        {
            _sink = new InMemorySink();
            _logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .Enrich.FromLogContext()
                .WriteTo.Sink(_sink)
                .CreateLogger();

            LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
            {
                builder.AddSerilog(_logger, dispose: false);
            });

            MeterProvider = Sdk.CreateMeterProviderBuilder()
                .AddMeter(ScraperTelemetry.MeterName)
                .AddInMemoryExporter(_metrics)
                .Build();

            ActivitySource = new ActivitySource("WcScraper.Core.Tests.Instrumentation");
            TracerProvider = Sdk.CreateTracerProviderBuilder()
                .AddSource(ActivitySource.Name)
                .AddInMemoryExporter(_activities)
                .Build();
        }

        public ILoggerFactory LoggerFactory { get; }

        public MeterProvider MeterProvider { get; }

        public ActivitySource ActivitySource { get; }

        public TracerProvider TracerProvider { get; }

        public IReadOnlyList<LogEvent> LogEvents => _sink.GetSnapshot();

        public IReadOnlyList<Metric> Metrics => _metrics;

        public IReadOnlyList<Activity> ExportedActivities => _activities;

        public ScraperInstrumentationOptions CreateOptions() => new()
        {
            LoggerFactory = LoggerFactory,
            MeterProvider = MeterProvider,
            ActivitySource = ActivitySource
        };

        public void Flush()
        {
            MeterProvider.ForceFlush();
            TracerProvider.ForceFlush();
        }

        public void Dispose()
        {
            TracerProvider.Dispose();
            MeterProvider.Dispose();
            ActivitySource.Dispose();
            LoggerFactory.Dispose();
            _logger.Dispose();
        }

        private sealed class InMemorySink : ILogEventSink
        {
            private readonly List<LogEvent> _events = new();
            private readonly object _sync = new();

            public IReadOnlyList<LogEvent> GetSnapshot()
            {
                lock (_sync)
                {
                    return _events.ToList();
                }
            }

            public void Emit(LogEvent logEvent)
            {
                if (logEvent is null)
                {
                    return;
                }

                lock (_sync)
                {
                    _events.Add(logEvent);
                }
            }
        }
    }
}
