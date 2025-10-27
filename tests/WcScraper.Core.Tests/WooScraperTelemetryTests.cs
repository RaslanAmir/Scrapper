using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WcScraper.Core;
using WcScraper.Core.Telemetry;
using WcScraper.Core.Tests.Telemetry;
using WcScraper.Wpf.Services;
using Xunit;

namespace WcScraper.Core.Tests;

public sealed class WooScraperTelemetryTests
{
    private const string OperationName = "WooScraper.FetchStoreProducts";
    private const string EntityType = "product";

    [Fact]
    public async Task FetchStoreProductsAsync_EmitsTelemetryScopesAndMetrics()
    {
        using var telemetry = new TelemetryTestContext();

        const string baseUrl = "https://example.com";
        const string requestUrl = $"{baseUrl}/wp-json/wc/store/v1/products?per_page=100&page=1";

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

        using var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        });

        using var httpClient = new HttpClient(handler);
        var instrumentationOptions = new ScraperInstrumentationOptions
        {
            LoggerFactory = telemetry.LoggerFactory
        };

        var scraper = new WooScraper(
            httpClient,
            allowLegacyTls: false,
            loggerFactory: telemetry.LoggerFactory,
            instrumentationOptions: instrumentationOptions);

        var progress = LoggerProgressAdapter.ForOperation(
            telemetry.CreateLogger<WooScraperTelemetryTests>(),
            callback: null,
            operationName: OperationName,
            url: requestUrl,
            entityType: EntityType,
            instrumentationOptions: instrumentationOptions);

        var products = await scraper.FetchStoreProductsAsync(baseUrl, perPage: 100, maxPages: 1, log: progress);

        Assert.Single(products);

        var scope = telemetry.LoggerFactory.Scopes.First(s =>
            s.Values.TryGetValue("Operation", out var operation) &&
            string.Equals(operation as string, OperationName, StringComparison.Ordinal));
        Assert.Equal(OperationName, Assert.IsType<string>(scope.Values["Operation"]));
        Assert.Equal(requestUrl, Assert.IsType<string>(scope.Values["Url"]));
        Assert.Equal(EntityType, Assert.IsType<string>(scope.Values["EntityType"]));

        var durationMeasurement = Assert.Single(telemetry.MeterListener.HistogramMeasurements
            .Where(m => string.Equals(m.InstrumentName, "scraper.request.duration", StringComparison.Ordinal)
                        && m.Tags.TryGetValue("operation", out var opTag)
                        && string.Equals(opTag as string, OperationName, StringComparison.Ordinal)
                        && m.Tags.TryGetValue("url", out var urlTag)
                        && string.Equals(urlTag as string, requestUrl, StringComparison.Ordinal)));

        Assert.Equal(OperationName, Assert.IsType<string>(durationMeasurement.Tags["operation"]));
        Assert.Equal(requestUrl, Assert.IsType<string>(durationMeasurement.Tags["url"]));
        Assert.Equal(EntityType, Assert.IsType<string>(durationMeasurement.Tags["entity"]));
        Assert.Equal(200, Assert.IsType<int>(durationMeasurement.Tags["http.status_code"]));

        var successMeasurement = Assert.Single(telemetry.MeterListener.CounterMeasurements
            .Where(m => string.Equals(m.InstrumentName, "scraper.request.success", StringComparison.Ordinal)
                        && m.Tags.TryGetValue("operation", out var opTag)
                        && string.Equals(opTag as string, OperationName, StringComparison.Ordinal)
                        && m.Tags.TryGetValue("url", out var urlTag)
                        && string.Equals(urlTag as string, requestUrl, StringComparison.Ordinal)));

        Assert.Equal(OperationName, Assert.IsType<string>(successMeasurement.Tags["operation"]));
        Assert.Equal(requestUrl, Assert.IsType<string>(successMeasurement.Tags["url"]));
        Assert.Equal(EntityType, Assert.IsType<string>(successMeasurement.Tags["entity"]));
        Assert.Equal(200, Assert.IsType<int>(successMeasurement.Tags["http.status_code"]));

        Assert.True(telemetry.MeterListener.CounterTotals.TryGetValue("scraper.request.success", out var total));
        Assert.True(total >= 1);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder ?? throw new ArgumentNullException(nameof(responder));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }
}
