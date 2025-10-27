using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace WcScraper.Core.Telemetry;

public sealed class ScraperInstrumentationOptions
{
    public ILoggerFactory? LoggerFactory { get; init; }

    public MeterProvider? MeterProvider { get; init; }

    public ActivitySource? ActivitySource { get; init; }
}
