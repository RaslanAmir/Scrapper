using System;
using System.Diagnostics.Metrics;
using System.Net;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace WcScraper.Core.Telemetry;

public static class ScraperTelemetry
{
    public static class Events
    {
        public static readonly EventId RequestStarting = new(1000, nameof(RequestStarting));
        public static readonly EventId RequestSucceeded = new(1001, nameof(RequestSucceeded));
        public static readonly EventId RequestFailed = new(1002, nameof(RequestFailed));
        public static readonly EventId RetryScheduled = new(1003, nameof(RetryScheduled));
        public static readonly EventId RetryOutcome = new(1004, nameof(RetryOutcome));
    }

    public const string MeterName = "WcScraper.Core";

    private static readonly Lazy<Instruments> InstrumentsHolder = new(() => new Instruments(CreateMeter()), LazyThreadSafetyMode.ExecutionAndPublication);

    public static Meter Meter => InstrumentsHolder.Value.Meter;

    public static Histogram<double> RequestDuration => InstrumentsHolder.Value.RequestDuration;

    public static Counter<long> RequestSuccesses => InstrumentsHolder.Value.RequestSuccesses;

    public static Counter<long> RequestFailures => InstrumentsHolder.Value.RequestFailures;

    public static Counter<long> RetryAttempts => InstrumentsHolder.Value.RetryAttempts;

    public static Counter<long> RetryOutcomes => InstrumentsHolder.Value.RetryOutcomes;

    internal static Instruments GetInstruments(MeterProvider? meterProvider)
    {
        if (meterProvider is null)
        {
            return InstrumentsHolder.Value;
        }

        var meter = meterProvider.GetMeter(MeterName, GetVersion());
        return new Instruments(meter);
    }

    internal static TagList CreateRequestTags(ScraperOperationContext context, HttpStatusCode? statusCode, int retryCount)
    {
        var tags = new TagList
        {
            { "operation", context.OperationName }
        };

        if (!string.IsNullOrWhiteSpace(context.Url))
        {
            tags.Add("url", context.Url);
        }

        if (!string.IsNullOrWhiteSpace(context.EntityType))
        {
            tags.Add("entity", context.EntityType);
        }

        if (context.PayloadBytes is { } payloadBytes)
        {
            tags.Add("payload.bytes", payloadBytes);
        }

        if (statusCode is { } code)
        {
            tags.Add("http.status_code", (int)code);
        }

        if (retryCount > 0)
        {
            tags.Add("retry.count", retryCount);
        }

        return tags;
    }

    internal static TagList CreateRetryTags(ScraperOperationContext context, string outcome)
    {
        var tags = new TagList
        {
            { "operation", context.OperationName },
            { "retry.outcome", outcome }
        };

        if (!string.IsNullOrWhiteSpace(context.Url))
        {
            tags.Add("url", context.Url);
        }

        if (!string.IsNullOrWhiteSpace(context.EntityType))
        {
            tags.Add("entity", context.EntityType);
        }

        if (context.PayloadBytes is { } payloadBytes)
        {
            tags.Add("payload.bytes", payloadBytes);
        }

        if (context.RetryAttempt is { } attempt)
        {
            tags.Add("retry.count", attempt);
        }

        if (context.RetryDelay is { } delay)
        {
            tags.Add("retry.delay_ms", delay.TotalMilliseconds);
        }

        if (!string.IsNullOrWhiteSpace(context.RetryReason))
        {
            tags.Add("retry.reason", context.RetryReason);
        }

        return tags;
    }

    private static Meter CreateMeter() => new(MeterName, GetVersion());

    private static Histogram<double> CreateRequestDurationInstrument(Meter meter) => meter.CreateHistogram<double>(
        "scraper.request.duration",
        unit: "ms",
        description: "Duration of scraper HTTP requests in milliseconds.");

    private static Counter<long> CreateRequestSuccessInstrument(Meter meter) => meter.CreateCounter<long>(
        "scraper.request.success",
        description: "Number of successful scraper requests.");

    private static Counter<long> CreateRequestFailureInstrument(Meter meter) => meter.CreateCounter<long>(
        "scraper.request.failure",
        description: "Number of failed scraper requests.");

    private static Counter<long> CreateRetryAttemptInstrument(Meter meter) => meter.CreateCounter<long>(
        "scraper.request.retry.attempt",
        description: "Retry attempts scheduled for scraper requests.");

    private static Counter<long> CreateRetryOutcomeInstrument(Meter meter) => meter.CreateCounter<long>(
        "scraper.request.retry.outcome",
        description: "Retry outcomes for scraper requests.");

    private static string GetVersion()
    {
        var version = typeof(ScraperTelemetry).Assembly.GetName().Version;
        return version?.ToString() ?? "1.0.0";
    }

    internal sealed class Instruments
    {
        public Instruments(Meter meter)
        {
            Meter = meter ?? throw new ArgumentNullException(nameof(meter));
            RequestDuration = CreateRequestDurationInstrument(meter);
            RequestSuccesses = CreateRequestSuccessInstrument(meter);
            RequestFailures = CreateRequestFailureInstrument(meter);
            RetryAttempts = CreateRetryAttemptInstrument(meter);
            RetryOutcomes = CreateRetryOutcomeInstrument(meter);
        }

        public Meter Meter { get; }

        public Histogram<double> RequestDuration { get; }

        public Counter<long> RequestSuccesses { get; }

        public Counter<long> RequestFailures { get; }

        public Counter<long> RetryAttempts { get; }

        public Counter<long> RetryOutcomes { get; }
    }
}
