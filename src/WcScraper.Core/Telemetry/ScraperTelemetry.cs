using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Net;
using System.Threading;
using Microsoft.Extensions.Logging;
using WcScraper.Core;

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

    private const string MeterName = "WcScraper.Core";
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
    private static readonly Lazy<Counter<long>> RetryOutcomeHolder = new(() => Meter.CreateCounter<long>(
        "scraper.request.retry.outcome",
        description: "Retry outcomes for scraper requests."),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Action<ILogger, string, string?, string?, Exception?> RequestStartingMessage = LoggerMessage.Define<string, string?, string?>(
        LogLevel.Information,
        Events.RequestStarting,
        "Starting request {RequestName} targeting {RequestTarget} (uri: {RequestUri})");

    private static readonly Action<ILogger, string, string?, double, int, int?, Exception?> RequestSucceededMessage = LoggerMessage.Define<string, string?, double, int, int?>(
        LogLevel.Information,
        Events.RequestSucceeded,
        "Request {RequestName} targeting {RequestTarget} completed in {DurationMs}ms after {RetryCount} retries (status: {StatusCode})");

    private static readonly Action<ILogger, string, string?, double, int, int?, Exception?> RequestFailedMessage = LoggerMessage.Define<string, string?, double, int, int?>(
        LogLevel.Error,
        Events.RequestFailed,
        "Request {RequestName} targeting {RequestTarget} failed after {DurationMs}ms and {RetryCount} retries (status: {StatusCode})");

    private static readonly Action<ILogger, string?, double, int, string, Exception?> RetryScheduledMessage = LoggerMessage.Define<string?, double, int, string>(
        LogLevel.Warning,
        Events.RetryScheduled,
        "Retrying request {RequestName} in {DelayMs}ms (attempt {Attempt}): {Reason}");

    private static readonly Action<ILogger, string, string, Exception?> RetryOutcomeMessage = LoggerMessage.Define<string, string>(
        LogLevel.Information,
        Events.RetryOutcome,
        "Retry outcome for {RequestName}: {Outcome}");

    public static Meter Meter => MeterHolder.Value;

    public static Histogram<double> RequestDuration => RequestDurationHolder.Value;

    public static Counter<long> RequestSuccesses => RequestSuccessHolder.Value;

    public static Counter<long> RequestFailures => RequestFailureHolder.Value;

    public static Counter<long> RetryOutcomes => RetryOutcomeHolder.Value;

    public static IDisposable BeginRequestScope(this ILogger logger, string requestName, string? requestTarget = null, string? requestUri = null)
    {
        if (logger is null)
        {
            throw new ArgumentNullException(nameof(logger));
        }

        var scopeValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["RequestName"] = requestName
        };

        if (!string.IsNullOrWhiteSpace(requestTarget))
        {
            scopeValues["RequestTarget"] = requestTarget;
        }

        if (!string.IsNullOrWhiteSpace(requestUri))
        {
            scopeValues["RequestUri"] = requestUri;
        }

        return logger.BeginScope(scopeValues);
    }

    public static void RecordRequestStart(this ILogger logger, string requestName, string? requestTarget = null, string? requestUri = null)
    {
        if (logger is null)
        {
            throw new ArgumentNullException(nameof(logger));
        }

        RequestStartingMessage(logger, requestName, requestTarget, requestUri, null);
    }

    public static void RecordRequestSuccess(this ILogger logger, string requestName, string? requestTarget, TimeSpan duration, int retryCount = 0, HttpStatusCode? statusCode = null)
    {
        if (logger is null)
        {
            throw new ArgumentNullException(nameof(logger));
        }

        var durationMs = duration.TotalMilliseconds;
        RequestSucceededMessage(logger, requestName, requestTarget, durationMs, retryCount, statusCode is { } code ? (int?)code : null, null);

        var tags = CreateRequestTags(requestName, requestTarget, statusCode, retryCount);
        RequestDuration.Record(durationMs, tags);
        RequestSuccesses.Add(1, tags);

        if (retryCount > 0)
        {
            var retryTags = CreateRequestTags(requestName, requestTarget, statusCode, retryCount);
            retryTags.Add("outcome", "success");
            RetryOutcomes.Add(1, retryTags);
            RetryOutcomeMessage(logger, requestName, "success", null);
        }
    }

    public static void RecordRequestFailure(this ILogger logger, string requestName, string? requestTarget, TimeSpan duration, Exception exception, int retryCount = 0, HttpStatusCode? statusCode = null)
    {
        if (logger is null)
        {
            throw new ArgumentNullException(nameof(logger));
        }

        if (exception is null)
        {
            throw new ArgumentNullException(nameof(exception));
        }

        var durationMs = duration.TotalMilliseconds;
        RequestFailedMessage(logger, requestName, requestTarget, durationMs, retryCount, statusCode is { } code ? (int?)code : null, exception);

        var tags = CreateRequestTags(requestName, requestTarget, statusCode, retryCount);
        RequestDuration.Record(durationMs, tags);
        RequestFailures.Add(1, tags);

        if (retryCount > 0)
        {
            var retryTags = CreateRequestTags(requestName, requestTarget, statusCode, retryCount);
            retryTags.Add("outcome", "failure");
            RetryOutcomes.Add(1, retryTags);
            RetryOutcomeMessage(logger, requestName, "failure", null);
        }
    }

    public static void RecordRetryAttempt(this ILogger logger, string requestName, string? requestTarget, HttpRetryAttempt attempt)
    {
        if (logger is null)
        {
            throw new ArgumentNullException(nameof(logger));
        }

        RetryScheduledMessage(logger, requestName, attempt.Delay.TotalMilliseconds, attempt.AttemptNumber, attempt.Reason, null);
    }

    public static void LogRetryScheduled(this ILogger logger, TimeSpan delay, int attempt, string reason, string? requestName = null)
    {
        if (logger is null)
        {
            throw new ArgumentNullException(nameof(logger));
        }

        RetryScheduledMessage(logger, requestName, delay.TotalMilliseconds, attempt, reason, null);
    }

    private static TagList CreateRequestTags(string requestName, string? requestTarget, HttpStatusCode? statusCode, int? retryCount)
    {
        var tags = new TagList
        {
            { "request", requestName }
        };

        if (!string.IsNullOrWhiteSpace(requestTarget))
        {
            tags.Add("target", requestTarget);
        }

        if (statusCode is { } code)
        {
            tags.Add("http.status_code", (int)code);
        }

        if (retryCount is { } retries && retries > 0)
        {
            tags.Add("retry.count", retries);
        }

        return tags;
    }

    private static string GetVersion()
    {
        var version = typeof(ScraperTelemetry).Assembly.GetName().Version;
        return version?.ToString() ?? "1.0.0";
    }
}
