using System;
using System.Collections.Generic;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace WcScraper.Core.Telemetry;

public sealed class ScraperInstrumentation : IScraperInstrumentation
{
    private static readonly Action<ILogger, string, string?, string?, long?, Exception?> RequestStartingMessage = LoggerMessage.Define<string, string?, string?, long?>(
        LogLevel.Information,
        ScraperTelemetry.Events.RequestStarting,
        "Starting request {Operation} targeting {Url} (entity: {EntityType}, payload: {PayloadBytes})");

    private static readonly Action<ILogger, string, string?, string?, double, int, int?, long?, Exception?> RequestSucceededMessage = LoggerMessage.Define<string, string?, string?, double, int, int?, long?>(
        LogLevel.Information,
        ScraperTelemetry.Events.RequestSucceeded,
        "Request {Operation} targeting {Url} succeeded in {DurationMs}ms after {RetryCount} retries (status: {StatusCode}, entity: {EntityType}, payload: {PayloadBytes})");

    private static readonly Action<ILogger, string, string?, string?, double, int, int?, long?, Exception?> RequestFailedMessage = LoggerMessage.Define<string, string?, string?, double, int, int?, long?>(
        LogLevel.Error,
        ScraperTelemetry.Events.RequestFailed,
        "Request {Operation} targeting {Url} failed after {DurationMs}ms and {RetryCount} retries (status: {StatusCode}, entity: {EntityType}, payload: {PayloadBytes})");

    private static readonly Action<ILogger, string, string?, string?, double, int, string?, long?, Exception?> RetryScheduledMessage = LoggerMessage.Define<string, string?, string?, double, int, string?, long?>(
        LogLevel.Warning,
        ScraperTelemetry.Events.RetryScheduled,
        "Retrying {Operation} targeting {Url} in {DelayMs}ms (attempt {Attempt}, reason: {Reason}, entity: {EntityType}, payload: {PayloadBytes})");

    private static readonly Action<ILogger, string, string, string?, long?, Exception?> RetryOutcomeMessage = LoggerMessage.Define<string, string, string?, long?>(
        LogLevel.Information,
        ScraperTelemetry.Events.RetryOutcome,
        "Retry outcome for {Operation}: {Outcome} (entity: {EntityType}, payload: {PayloadBytes})");

    private readonly ILogger _logger;

    public ScraperInstrumentation(ILogger logger)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public static IScraperInstrumentation Create(ILogger? logger) => new ScraperInstrumentation(logger ?? NullLogger.Instance);

    public IDisposable BeginScope(ScraperOperationContext context)
    {
        var scope = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Operation"] = context.OperationName
        };

        if (!string.IsNullOrWhiteSpace(context.Url))
        {
            scope["Url"] = context.Url;
        }

        if (!string.IsNullOrWhiteSpace(context.EntityType))
        {
            scope["EntityType"] = context.EntityType;
        }

        if (context.PayloadBytes is { } payloadBytes)
        {
            scope["PayloadBytes"] = payloadBytes;
        }

        if (context.RetryAttempt is { } attempt)
        {
            scope["RetryAttempt"] = attempt;
        }

        if (context.RetryDelay is { } delay)
        {
            scope["RetryDelayMs"] = delay.TotalMilliseconds;
        }

        if (!string.IsNullOrWhiteSpace(context.RetryReason))
        {
            scope["RetryReason"] = context.RetryReason;
        }

        return _logger.BeginScope(scope);
    }

    public void RecordRequestStart(ScraperOperationContext context)
    {
        RequestStartingMessage(_logger, context.OperationName, context.Url, context.EntityType, context.PayloadBytes, null);
    }

    public void RecordRequestSuccess(ScraperOperationContext context, TimeSpan duration, HttpStatusCode? statusCode = null, int retryCount = 0)
    {
        var durationMs = duration.TotalMilliseconds;
        RequestSucceededMessage(_logger, context.OperationName, context.Url, context.EntityType, durationMs, retryCount, statusCode is { } code ? (int?)code : null, context.PayloadBytes, null);

        var tags = ScraperTelemetry.CreateRequestTags(context, statusCode, retryCount);
        ScraperTelemetry.RequestDuration.Record(durationMs, tags);
        ScraperTelemetry.RequestSuccesses.Add(1, tags);

        if (retryCount > 0)
        {
            var retryTags = ScraperTelemetry.CreateRetryTags(context with
            {
                RetryAttempt = retryCount
            }, "success");
            ScraperTelemetry.RetryOutcomes.Add(1, retryTags);
            RetryOutcomeMessage(_logger, context.OperationName, "success", context.EntityType, context.PayloadBytes, null);
        }
    }

    public void RecordRequestFailure(ScraperOperationContext context, TimeSpan duration, Exception exception, HttpStatusCode? statusCode = null, int retryCount = 0)
    {
        if (exception is null)
        {
            throw new ArgumentNullException(nameof(exception));
        }

        var durationMs = duration.TotalMilliseconds;
        RequestFailedMessage(_logger, context.OperationName, context.Url, context.EntityType, durationMs, retryCount, statusCode is { } code ? (int?)code : null, context.PayloadBytes, exception);

        var tags = ScraperTelemetry.CreateRequestTags(context, statusCode, retryCount);
        ScraperTelemetry.RequestDuration.Record(durationMs, tags);
        ScraperTelemetry.RequestFailures.Add(1, tags);

        if (retryCount > 0)
        {
            var retryTags = ScraperTelemetry.CreateRetryTags(context with
            {
                RetryAttempt = retryCount
            }, "failure");
            ScraperTelemetry.RetryOutcomes.Add(1, retryTags);
            RetryOutcomeMessage(_logger, context.OperationName, "failure", context.EntityType, context.PayloadBytes, exception);
        }
    }

    public void RecordRetry(ScraperOperationContext context)
    {
        if (context.RetryAttempt is not { } attempt)
        {
            return;
        }

        var delayMs = context.RetryDelay?.TotalMilliseconds ?? 0d;
        RetryScheduledMessage(_logger, context.OperationName, context.Url, context.EntityType, delayMs, attempt, context.RetryReason, context.PayloadBytes, null);

        var retryTags = ScraperTelemetry.CreateRetryTags(context, "scheduled");
        ScraperTelemetry.RetryAttempts.Add(1, retryTags);
    }
}

public sealed class NullScraperInstrumentation : IScraperInstrumentation
{
    public static readonly NullScraperInstrumentation Instance = new();

    private NullScraperInstrumentation()
    {
    }

    public IDisposable BeginScope(ScraperOperationContext context) => NullScope.Instance;

    public void RecordRequestStart(ScraperOperationContext context)
    {
    }

    public void RecordRequestSuccess(ScraperOperationContext context, TimeSpan duration, HttpStatusCode? statusCode = null, int retryCount = 0)
    {
    }

    public void RecordRequestFailure(ScraperOperationContext context, TimeSpan duration, Exception exception, HttpStatusCode? statusCode = null, int retryCount = 0)
    {
    }

    public void RecordRetry(ScraperOperationContext context)
    {
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
