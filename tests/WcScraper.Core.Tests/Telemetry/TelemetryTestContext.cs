using System;
using Microsoft.Extensions.Logging;

namespace WcScraper.Core.Tests.Telemetry;

public sealed class TelemetryTestContext : IDisposable
{
    public TelemetryTestContext()
    {
        LoggerFactory = new TestLoggerFactory();
        MeterListener = new TestMeterListener();
    }

    public TestLoggerFactory LoggerFactory { get; }

    public TestMeterListener MeterListener { get; }

    public ILogger CreateLogger<T>() => LoggerFactory.CreateLogger(typeof(T).FullName ?? typeof(T).Name);

    public void Dispose()
    {
        MeterListener.Dispose();
        LoggerFactory.Dispose();
    }
}
