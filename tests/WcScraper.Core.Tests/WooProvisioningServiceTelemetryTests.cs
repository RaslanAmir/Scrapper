using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WcScraper.Core;
using WcScraper.Core.Tests.Telemetry;
using WcScraper.Wpf.Services;
using Xunit;

namespace WcScraper.Core.Tests;

public sealed class WooProvisioningServiceTelemetryTests : IDisposable
{
    private readonly string _tempDirectory;

    public WooProvisioningServiceTelemetryTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "woo-provisioning-" + Guid.NewGuid().ToString("n", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task UploadPluginsAsync_EmitsSuccessTelemetry()
    {
        using var telemetry = new TelemetryTestContext();

        var settings = new WooProvisioningSettings("https://example.net", "ck", "cs");
        var bundle = CreateBundle("sample");

        var handler = new PluginUploadHandler(HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var service = new WooProvisioningService(httpClient);

        var progress = LoggerProgressAdapter.ForOperation(
            telemetry.CreateLogger<WooProvisioningServiceTelemetryTests>(),
            callback: null,
            operationName: "WooProvisioningService.UploadPlugin",
            url: handler.SuccessUrl,
            entityType: "plugin");

        await service.UploadPluginsAsync(settings, new[] { bundle }, progress: progress, cancellationToken: CancellationToken.None);

        Assert.True(handler.SuccessCount > 0);

        var scope = Assert.Contains(telemetry.LoggerFactory.Scopes, record =>
            record.Values.TryGetValue("Operation", out var operation)
            && string.Equals(operation as string, "WooProvisioningService.UploadPlugin", StringComparison.Ordinal));
        Assert.Equal("plugin", Assert.IsType<string>(scope.Values["EntityType"]));

        var successMeasurement = Assert.Single(telemetry.MeterListener.CounterMeasurements.Where(m =>
            string.Equals(m.InstrumentName, "scraper.request.success", StringComparison.Ordinal)
            && m.Tags.TryGetValue("operation", out var operation)
            && string.Equals(operation as string, "WooProvisioningService.UploadPlugin", StringComparison.Ordinal)));

        Assert.Equal("plugin", Assert.IsType<string>(successMeasurement.Tags["entity"]));
        Assert.Equal(200, Assert.IsType<int>(successMeasurement.Tags["http.status_code"]));

        var durationMeasurement = Assert.Single(telemetry.MeterListener.HistogramMeasurements.Where(m =>
            string.Equals(m.InstrumentName, "scraper.request.duration", StringComparison.Ordinal)
            && m.Tags.TryGetValue("operation", out var operation)
            && string.Equals(operation as string, "WooProvisioningService.UploadPlugin", StringComparison.Ordinal)));

        Assert.Equal(200, Assert.IsType<int>(durationMeasurement.Tags["http.status_code"]));
    }

    [Fact]
    public async Task UploadPluginsAsync_EmitsFailureTelemetryWithRetries()
    {
        using var telemetry = new TelemetryTestContext();

        var settings = new WooProvisioningSettings("https://example.net", "ck", "cs");
        var bundle = CreateBundle("retry");

        var handler = new PluginUploadHandler(HttpStatusCode.ServiceUnavailable, retryAfter: TimeSpan.FromMilliseconds(25));
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var service = new WooProvisioningService(httpClient);

        var progress = LoggerProgressAdapter.ForOperation(
            telemetry.CreateLogger<WooProvisioningServiceTelemetryTests>(),
            callback: null,
            operationName: "WooProvisioningService.UploadPlugin",
            url: handler.SuccessUrl,
            entityType: "plugin");

        await service.UploadPluginsAsync(settings, new[] { bundle }, progress: progress, cancellationToken: CancellationToken.None);

        Assert.True(handler.RetryCount > 0);

        var retryScope = Assert.Contains(telemetry.LoggerFactory.Scopes, record =>
            record.Values.TryGetValue("RetryAttempt", out var attempt)
            && Convert.ToInt32(attempt, CultureInfo.InvariantCulture) >= 1);

        Assert.True(retryScope.Values.TryGetValue("RetryDelayMs", out var delayValue));
        Assert.True(Convert.ToDouble(delayValue, CultureInfo.InvariantCulture) >= 0);

        var failureMeasurement = Assert.Single(telemetry.MeterListener.CounterMeasurements.Where(m =>
            string.Equals(m.InstrumentName, "scraper.request.failure", StringComparison.Ordinal)
            && m.Tags.TryGetValue("operation", out var operation)
            && string.Equals(operation as string, "WooProvisioningService.UploadPlugin", StringComparison.Ordinal)));

        Assert.Equal("plugin", Assert.IsType<string>(failureMeasurement.Tags["entity"]));
        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, Assert.IsType<int>(failureMeasurement.Tags["http.status_code"]));

        var retryAttemptMeasurement = Assert.Single(telemetry.MeterListener.CounterMeasurements.Where(m =>
            string.Equals(m.InstrumentName, "scraper.request.retry.attempt", StringComparison.Ordinal)
            && m.Tags.TryGetValue("operation", out var operation)
            && string.Equals(operation as string, "WooProvisioningService.UploadPlugin", StringComparison.Ordinal)));

        Assert.True(Convert.ToInt32(retryAttemptMeasurement.Tags["retry.count"], CultureInfo.InvariantCulture) >= 1);

        var retryOutcomeMeasurement = Assert.Single(telemetry.MeterListener.CounterMeasurements.Where(m =>
            string.Equals(m.InstrumentName, "scraper.request.retry.outcome", StringComparison.Ordinal)
            && m.Tags.TryGetValue("operation", out var operation)
            && string.Equals(operation as string, "WooProvisioningService.UploadPlugin", StringComparison.Ordinal)));

        Assert.Equal("failure", Assert.IsType<string>(retryOutcomeMeasurement.Tags["retry.outcome"]));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }

    private ExtensionArtifact CreateBundle(string slug)
    {
        var directory = Path.Combine(_tempDirectory, slug);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "options.json"), "{}", Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), "{}", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(directory, "archive.zip"), new byte[] { 0x1, 0x2, 0x3 });
        return new ExtensionArtifact(slug, directory);
    }

    private sealed class PluginUploadHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly TimeSpan? _retryAfter;
        private int _attempts;

        public PluginUploadHandler(HttpStatusCode statusCode, TimeSpan? retryAfter = null)
        {
            _statusCode = statusCode;
            _retryAfter = retryAfter;
            SuccessUrl = "https://example.net/wp-json/wc-scraper/v1/plugins/install";
        }

        public string SuccessUrl { get; }
        public int SuccessCount { get; private set; }
        public int RetryCount => Math.Max(0, _attempts - SuccessCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);

            if (_statusCode == HttpStatusCode.OK && request.RequestUri!.ToString().Equals(SuccessUrl, StringComparison.Ordinal))
            {
                SuccessCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("success", Encoding.UTF8, "application/json")
                });
            }

            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent("failure", Encoding.UTF8, "text/plain")
            };

            if (_retryAfter is { } retry)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(retry);
            }

            return Task.FromResult(response);
        }
    }
}
