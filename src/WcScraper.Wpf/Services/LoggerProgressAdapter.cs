using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WcScraper.Core.Telemetry;

namespace WcScraper.Wpf.Services;

public sealed class LoggerProgressAdapter : IProgress<string>
{
    private static readonly EventId ProgressEvent = new(5000, nameof(LoggerProgressAdapter));

    private readonly ILogger _logger;
    private readonly Action<string>? _callback;
    private readonly LogLevel _level;
    private readonly IReadOnlyList<KeyValuePair<string, object?>>? _additionalScope;
    private readonly IScraperInstrumentation? _instrumentation;
    private readonly ScraperOperationContext? _operationContext;

    public LoggerProgressAdapter(
        ILogger? logger,
        Action<string>? callback = null,
        ScraperOperationContext? operationContext = null,
        IReadOnlyDictionary<string, object?>? additionalContext = null,
        LogLevel level = LogLevel.Information,
        IScraperInstrumentation? instrumentation = null,
        ScraperInstrumentationOptions? instrumentationOptions = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _callback = callback;
        _level = level;

        if (operationContext is { } context)
        {
            _operationContext = context;
            _instrumentation = instrumentation
                ?? (instrumentationOptions is not null
                    ? ScraperInstrumentation.Create(instrumentationOptions)
                    : ScraperInstrumentation.Create(_logger));
        }

        if (additionalContext is { Count: > 0 })
        {
            var scope = new List<KeyValuePair<string, object?>>(additionalContext.Count);
            foreach (var pair in additionalContext)
            {
                scope.Add(new KeyValuePair<string, object?>(pair.Key, pair.Value));
            }

            _additionalScope = scope;
        }
    }

    public static LoggerProgressAdapter ForOperation(
        ILogger? logger,
        Action<string>? callback,
        string operationName,
        string? url = null,
        string? entityType = null,
        IReadOnlyDictionary<string, object?>? additionalContext = null,
        LogLevel level = LogLevel.Information,
        IScraperInstrumentation? instrumentation = null,
        ScraperInstrumentationOptions? instrumentationOptions = null)
    {
        if (string.IsNullOrWhiteSpace(operationName))
        {
            throw new ArgumentException("Operation name must be provided.", nameof(operationName));
        }

        var context = new ScraperOperationContext(operationName, url, entityType);
        return new LoggerProgressAdapter(logger, callback, context, additionalContext, level, instrumentation, instrumentationOptions);
    }

    public void Report(string value)
    {
        if (value is null)
        {
            return;
        }

        _callback?.Invoke(value);

        void LogCore()
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            _logger.Log(_level, ProgressEvent, "{ProgressMessage}", value);
        }

        if (_operationContext is { } context && _instrumentation is { } instrumentation)
        {
            using (instrumentation.BeginScope(context))
            {
                if (_additionalScope is { Count: > 0 })
                {
                    using (_logger.BeginScope(_additionalScope))
                    {
                        LogCore();
                    }
                }
                else
                {
                    LogCore();
                }
            }
        }
        else if (_additionalScope is { Count: > 0 })
        {
            using (_logger.BeginScope(_additionalScope))
            {
                LogCore();
            }
        }
        else
        {
            LogCore();
        }
    }
}
