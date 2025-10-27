using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private readonly ActivitySource? _activitySource;
    private readonly ScraperTelemetry.Instruments _instruments;

    public ScraperInstrumentation(ScraperInstrumentationOptions? options = null)
    {
        options ??= new ScraperInstrumentationOptions();

        var loggerFactory = options.LoggerFactory ?? NullLoggerFactory.Instance;
        var categoryName = typeof(ScraperInstrumentation).FullName ?? nameof(ScraperInstrumentation);
        _logger = loggerFactory.CreateLogger(categoryName);
        _activitySource = options.ActivitySource;
        _instruments = ScraperTelemetry.GetInstruments(options.MeterProvider);
    }

    public ScraperInstrumentation(ILogger logger)
        : this(CreateOptionsFromLogger(logger))
    {
    }

    public static IScraperInstrumentation Create(ILogger? logger) => new ScraperInstrumentation(logger ?? NullLogger.Instance);

    public static IScraperInstrumentation Create(ScraperInstrumentationOptions? options) => new ScraperInstrumentation(options);

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

        var loggerScope = _logger.BeginScope(scope);
        var activity = StartActivity(context);

        if (activity is null)
        {
            return loggerScope;
        }

        return new InstrumentationScope(loggerScope, activity);
    }

    public void RecordRequestStart(ScraperOperationContext context)
    {
        RequestStartingMessage(_logger, context.OperationName, context.Url, context.EntityType, context.PayloadBytes, null);
        if (TryGetCurrentActivity(out var activity))
        {
            activity.AddEvent(new ActivityEvent("ScraperRequestStart"));
        }
    }

    public void RecordRequestSuccess(ScraperOperationContext context, TimeSpan duration, HttpStatusCode? statusCode = null, int retryCount = 0)
    {
        var durationMs = duration.TotalMilliseconds;
        RequestSucceededMessage(_logger, context.OperationName, context.Url, context.EntityType, durationMs, retryCount, statusCode is { } code ? (int?)code : null, context.PayloadBytes, null);

        var tags = ScraperTelemetry.CreateRequestTags(context, statusCode, retryCount);
        _instruments.RequestDuration.Record(durationMs, tags);
        _instruments.RequestSuccesses.Add(1, tags);

        if (retryCount > 0)
        {
            var retryTags = ScraperTelemetry.CreateRetryTags(context with
            {
                RetryAttempt = retryCount
            }, "success");
            _instruments.RetryOutcomes.Add(1, retryTags);
            RetryOutcomeMessage(_logger, context.OperationName, "success", context.EntityType, context.PayloadBytes, null);
        }

        if (TryGetCurrentActivity(out var activity))
        {
            if (statusCode is { } code)
            {
                activity.SetTag("http.status_code", (int)code);
            }

            activity.SetTag("retry.count", retryCount);
            activity.SetTag("duration.ms", durationMs);
            activity.SetStatus(ActivityStatusCode.Ok);
            activity.AddEvent(new ActivityEvent("ScraperRequestSuccess"));
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
        _instruments.RequestDuration.Record(durationMs, tags);
        _instruments.RequestFailures.Add(1, tags);

        if (retryCount > 0)
        {
            var retryTags = ScraperTelemetry.CreateRetryTags(context with
            {
                RetryAttempt = retryCount
            }, "failure");
            _instruments.RetryOutcomes.Add(1, retryTags);
            RetryOutcomeMessage(_logger, context.OperationName, "failure", context.EntityType, context.PayloadBytes, exception);
        }

        if (TryGetCurrentActivity(out var activity))
        {
            if (statusCode is { } code)
            {
                activity.SetTag("http.status_code", (int)code);
            }

            activity.SetTag("retry.count", retryCount);
            activity.SetTag("duration.ms", durationMs);
            activity.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity.AddEvent(new ActivityEvent("ScraperRequestFailure"));
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
        _instruments.RetryAttempts.Add(1, retryTags);

        if (TryGetCurrentActivity(out var activity))
        {
            var eventTags = new ActivityTagsCollection
            {
                { "retry.attempt", attempt },
                { "retry.delay_ms", delayMs }
            };

            if (!string.IsNullOrWhiteSpace(context.RetryReason))
            {
                eventTags.Add("retry.reason", context.RetryReason);
            }

            activity.AddEvent(new ActivityEvent("ScraperRequestRetry", tags: eventTags));
        }
    }

    private Activity? StartActivity(ScraperOperationContext context)
    {
        if (_activitySource is null)
        {
            return null;
        }

        var activityName = string.IsNullOrWhiteSpace(context.OperationName)
            ? "ScraperOperation"
            : context.OperationName;

        var activity = _activitySource.StartActivity(activityName, ActivityKind.Client);

        if (activity is null)
        {
            return null;
        }

        activity.SetTag("operation", context.OperationName);

        if (!string.IsNullOrWhiteSpace(context.Url))
        {
            activity.SetTag("url", context.Url);
        }

        if (!string.IsNullOrWhiteSpace(context.EntityType))
        {
            activity.SetTag("entity", context.EntityType);
        }

        if (context.PayloadBytes is { } payloadBytes)
        {
            activity.SetTag("payload.bytes", payloadBytes);
        }

        if (context.RetryAttempt is { } attempt)
        {
            activity.SetTag("retry.attempt", attempt);
        }

        if (context.RetryDelay is { } delay)
        {
            activity.SetTag("retry.delay_ms", delay.TotalMilliseconds);
        }

        if (!string.IsNullOrWhiteSpace(context.RetryReason))
        {
            activity.SetTag("retry.reason", context.RetryReason);
        }

        return activity;
    }

    private bool TryGetCurrentActivity(out Activity? activity)
    {
        activity = null;

        if (_activitySource is null)
        {
            return false;
        }

        activity = Activity.Current;

        if (activity is null || activity.Source != _activitySource)
        {
            activity = null;
            return false;
        }

        return true;
    }

    private static ScraperInstrumentationOptions CreateOptionsFromLogger(ILogger? logger)
    {
        if (logger is null || ReferenceEquals(logger, NullLogger.Instance))
        {
            return new ScraperInstrumentationOptions();
        }

        return new ScraperInstrumentationOptions
        {
            LoggerFactory = new SingleLoggerFactory(logger)
        };
    }

    private sealed class InstrumentationScope : IDisposable
    {
        private readonly IDisposable _scope;
        private readonly Activity? _activity;

        public InstrumentationScope(IDisposable scope, Activity? activity)
        {
            _scope = scope;
            _activity = activity;
        }

        public void Dispose()
        {
            _activity?.Dispose();
            _scope.Dispose();
        }
    }

    private sealed class SingleLoggerFactory : ILoggerFactory
    {
        private readonly ILogger _logger;

        public SingleLoggerFactory(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => _logger;

        public void Dispose()
        {
        }
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
