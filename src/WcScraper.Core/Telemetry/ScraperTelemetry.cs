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

    private static readonly Lazy<Meter> MeterHolder = new(() => new Meter(MeterName, GetVersion()), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<Histogram<double>> RequestDurationHolder = new(() => Meter.CreateHistogram<double>(
            "scraper.request.duration",
            unit: "ms",
            description: "Duration of scraper HTTP requests in milliseconds."),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<Counter<long>> RequestSuccessHolder = new(() => Meter.CreateCounter<long>(
            "scraper.request.success",
            description: "Number of successful scraper requests."),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<Counter<long>> RequestFailureHolder = new(() => Meter.CreateCounter<long>(
            "scraper.request.failure",
            description: "Number of failed scraper requests."),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<Counter<long>> RetryAttemptsHolder = new(() => Meter.CreateCounter<long>(
            "scraper.request.retry.attempt",
            description: "Retry attempts scheduled for scraper requests."),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<Counter<long>> RetryOutcomeHolder = new(() => Meter.CreateCounter<long>(
            "scraper.request.retry.outcome",
            description: "Retry outcomes for scraper requests."),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static Meter Meter => MeterHolder.Value;

    public static Histogram<double> RequestDuration => RequestDurationHolder.Value;

    public static Counter<long> RequestSuccesses => RequestSuccessHolder.Value;

    public static Counter<long> RequestFailures => RequestFailureHolder.Value;

    public static Counter<long> RetryAttempts => RetryAttemptsHolder.Value;

    public static Counter<long> RetryOutcomes => RetryOutcomeHolder.Value;

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

    private static string GetVersion()
    {
        var version = typeof(ScraperTelemetry).Assembly.GetName().Version;
        return version?.ToString() ?? "1.0.0";
    }
}
