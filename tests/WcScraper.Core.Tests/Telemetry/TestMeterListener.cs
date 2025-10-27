using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using WcScraper.Core.Telemetry;

namespace WcScraper.Core.Tests.Telemetry;

public sealed class TestMeterListener : IDisposable
{
    private static readonly HashSet<string> TrackedCounterNames = new(StringComparer.Ordinal)
    {
        "scraper.request.success",
        "scraper.request.failure",
        "scraper.request.retry.attempt",
        "scraper.request.retry.outcome"
    };

    private readonly MeterListener _listener;
    private readonly List<HistogramMeasurement> _histogramMeasurements = new();
    private readonly List<CounterMeasurement> _counterMeasurements = new();
    private readonly ConcurrentDictionary<string, long> _counterTotals = new(StringComparer.Ordinal);

    public TestMeterListener()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = OnInstrumentPublished
        };

        _listener.SetMeasurementEventCallback<double>(OnHistogramRecorded);
        _listener.SetMeasurementEventCallback<long>(OnCounterRecorded);
        _listener.Start();
    }

    public IReadOnlyCollection<HistogramMeasurement> HistogramMeasurements
    {
        get
        {
            lock (_histogramMeasurements)
            {
                return _histogramMeasurements.ToList();
            }
        }
    }

    public IReadOnlyCollection<CounterMeasurement> CounterMeasurements
    {
        get
        {
            lock (_counterMeasurements)
            {
                return _counterMeasurements.ToList();
            }
        }
    }

    public IReadOnlyDictionary<string, long> CounterTotals => _counterTotals;

    public void Dispose()
    {
        _listener.Dispose();
    }

    private void OnInstrumentPublished(Instrument instrument, MeterListener listener)
    {
        if (instrument.Meter.Name != ScraperTelemetry.MeterName)
        {
            return;
        }

        switch (instrument)
        {
            case Histogram<double>:
                listener.EnableMeasurementEvents(instrument);
                break;
            case Counter<long> counter when TrackedCounterNames.Contains(counter.Name):
                listener.EnableMeasurementEvents(instrument);
                break;
        }
    }

    private void OnHistogramRecorded(Instrument instrument, double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        var record = new HistogramMeasurement(instrument.Name, measurement, ToDictionary(tags));
        lock (_histogramMeasurements)
        {
            _histogramMeasurements.Add(record);
        }
    }

    private void OnCounterRecorded(Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (!TrackedCounterNames.Contains(instrument.Name))
        {
            return;
        }

        var record = new CounterMeasurement(instrument.Name, measurement, ToDictionary(tags));
        lock (_counterMeasurements)
        {
            _counterMeasurements.Add(record);
        }

        _counterTotals.AddOrUpdate(instrument.Name, measurement, (_, existing) => existing + measurement);
    }

    private static IReadOnlyDictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dictionary = new Dictionary<string, object?>(tags.Length, StringComparer.Ordinal);

        foreach (var tag in tags)
        {
            dictionary[tag.Key] = tag.Value;
        }

        return dictionary;
    }
}

public sealed record HistogramMeasurement(string InstrumentName, double Value, IReadOnlyDictionary<string, object?> Tags);

public sealed record CounterMeasurement(string InstrumentName, long Value, IReadOnlyDictionary<string, object?> Tags);
