using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace WcScraper.Core.Tests.Telemetry;

public sealed class TestLoggerFactory : ILoggerFactory
{
    private readonly ConcurrentDictionary<string, TestLogger> _loggers = new();
    private readonly List<LogRecord> _logRecords = new();
    private readonly List<ScopeRecord> _scopeRecords = new();
    private readonly object _sync = new();

    public IReadOnlyCollection<LogRecord> Logs
    {
        get
        {
            lock (_sync)
            {
                return _logRecords.ToList();
            }
        }
    }

    public IReadOnlyCollection<ScopeRecord> Scopes
    {
        get
        {
            lock (_sync)
            {
                return _scopeRecords.ToList();
            }
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new TestLogger(name, this));
    }

    public void AddProvider(ILoggerProvider provider)
    {
        throw new NotSupportedException();
    }

    public void Dispose()
    {
        _loggers.Clear();
    }

    internal void RecordLog(LogRecord record)
    {
        lock (_sync)
        {
            _logRecords.Add(record);
        }
    }

    internal void RecordScope(ScopeRecord record)
    {
        lock (_sync)
        {
            _scopeRecords.Add(record);
        }
    }

    private sealed class TestLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly TestLoggerFactory _factory;

        public TestLogger(string categoryName, TestLoggerFactory factory)
        {
            _categoryName = categoryName;
            _factory = factory;
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            if (TryExtractDictionary(state, out var dictionary))
            {
                _factory.RecordScope(new ScopeRecord(_categoryName, dictionary));
            }

            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            var values = TryExtractKeyValuePairs(state, out var list) ? list : Array.Empty<KeyValuePair<string, object?>>();

            var record = new LogRecord(
                _categoryName,
                logLevel,
                eventId,
                message,
                exception,
                values);

            _factory.RecordLog(record);
        }

        private static bool TryExtractDictionary<TState>(TState state, out IReadOnlyDictionary<string, object?> dictionary)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> kvps)
            {
                dictionary = kvps.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                return true;
            }

            dictionary = new Dictionary<string, object?>();
            return false;
        }

        private static bool TryExtractKeyValuePairs<TState>(TState state, out IReadOnlyList<KeyValuePair<string, object?>> values)
        {
            if (state is IReadOnlyList<KeyValuePair<string, object?>> readOnlyList)
            {
                values = readOnlyList.ToList();
                return true;
            }

            if (state is IEnumerable<KeyValuePair<string, object?>> kvps)
            {
                values = kvps.Select(kvp => new KeyValuePair<string, object?>(kvp.Key, kvp.Value)).ToList();
                return true;
            }

            values = Array.Empty<KeyValuePair<string, object?>>();
            return false;
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        private NullScope()
        {
        }

        public void Dispose()
        {
        }
    }
}

public sealed record LogRecord(
    string Category,
    LogLevel LogLevel,
    EventId EventId,
    string Message,
    Exception? Exception,
    IReadOnlyList<KeyValuePair<string, object?>> State);

public sealed record ScopeRecord(
    string Category,
    IReadOnlyDictionary<string, object?> Values);
