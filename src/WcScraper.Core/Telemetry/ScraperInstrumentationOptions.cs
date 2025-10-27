using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace WcScraper.Core.Telemetry;

public sealed class ScraperInstrumentationOptions
{
    public static ScraperInstrumentationOptions SharedDefaults { get; } = new();

    public ILoggerFactory? LoggerFactory { get; init; }

    public MeterProvider? MeterProvider { get; init; }

    public ActivitySource? ActivitySource { get; init; }

    public ScraperInstrumentationOptions WithFallbackLoggerFactory(ILoggerFactory loggerFactory)
    {
        if (loggerFactory is null)
        {
            throw new ArgumentNullException(nameof(loggerFactory));
        }

        if (LoggerFactory is not null)
        {
            return this;
        }

        return new ScraperInstrumentationOptions
        {
            LoggerFactory = loggerFactory,
            MeterProvider = MeterProvider,
            ActivitySource = ActivitySource
        };
    }
}
