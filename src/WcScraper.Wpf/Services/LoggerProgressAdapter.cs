using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WcScraper.Core.Telemetry;

namespace WcScraper.Wpf.Services;

public sealed class LoggerProgressAdapter : IProgress<string>, ILogger
{
    private static readonly EventId ProgressEvent = new(5000, nameof(LoggerProgressAdapter));

    private readonly ILogger _logger;
    private readonly Action<string>? _callback;
    private readonly LogLevel _defaultLevel;
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
        _defaultLevel = level;

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
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        Log(_defaultLevel, ProgressEvent, value, null, static (state, _) => state);
    }

    public IDisposable BeginScope<TState>(TState state)
        => _logger.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => _logger.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (formatter is null)
        {
            throw new ArgumentNullException(nameof(formatter));
        }

        void WriteLog()
        {
            _logger.Log(logLevel, eventId, state, exception, formatter);
        }

        if (_operationContext is { } context && _instrumentation is { } instrumentation)
        {
            using (instrumentation.BeginScope(context))
            {
                LogWithinAdditionalScope(WriteLog);
            }
        }
        else
        {
            LogWithinAdditionalScope(WriteLog);
        }

        if (_callback is null)
        {
            return;
        }

        var message = formatter(state, exception);
        if (string.IsNullOrWhiteSpace(message) && exception is not null)
        {
            message = exception.Message;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (exception is not null && !string.Equals(message, exception.Message, StringComparison.Ordinal))
        {
            message = $"{message}: {exception.Message}";
        }

        _callback(FormatUiMessage(logLevel, message));
    }

    private void LogWithinAdditionalScope(Action logAction)
    {
        if (_additionalScope is { Count: > 0 })
        {
            using (_logger.BeginScope(_additionalScope))
            {
                logAction();
            }
        }
        else
        {
            logAction();
        }
    }

    private static string FormatUiMessage(LogLevel level, string message)
    {
        var prefix = level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "CRITICAL",
            _ => string.Empty
        };

        if (string.IsNullOrEmpty(prefix))
        {
            return message;
        }

        return $"[{prefix}] {message}";
    }
}
