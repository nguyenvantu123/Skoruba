// Feature: tenant-client-cache-expansion, Task 6
//
// Wraps System.Diagnostics.Metrics.MeterListener to capture every
// counter increment and histogram measurement emitted by the
// TenantClientCacheMetrics meter (name "TenantClientCache"). Each
// measurement is preserved together with its tag set so property tests
// can assert (a) the counter was incremented exactly once, (b) the tag
// dictionary is exactly {tenantKey, outcome}, and (c) "clientId" is
// never present as a tag (R16.3).
//
// The listener subscribes only to the meter passed in via constructor
// to avoid bleeding measurements from sibling tests / app meters.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache.Helpers;

/// <summary>
/// One observation captured by <see cref="RecordingMeterListener"/>.
/// </summary>
internal sealed record RecordedMeasurement(
    string InstrumentName,
    double Value,
    IReadOnlyDictionary<string, object?> Tags);

/// <summary>
/// Test-only collector that listens to a single <see cref="Meter"/> name
/// and stores every Counter / Histogram measurement keyed by instrument
/// name. Disposal stops the underlying <see cref="MeterListener"/>.
/// </summary>
internal sealed class RecordingMeterListener : IDisposable
{
    private readonly string _meterName;
    private readonly MeterListener _listener;
    private readonly object _gate = new();
    private readonly List<RecordedMeasurement> _measurements = new();

    public RecordingMeterListener(string meterName)
    {
        _meterName = meterName ?? throw new ArgumentNullException(nameof(meterName));

        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == _meterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };

        _listener.SetMeasurementEventCallback<long>(OnMeasurement);
        _listener.SetMeasurementEventCallback<double>(OnMeasurement);
        _listener.SetMeasurementEventCallback<int>(OnMeasurement);
        _listener.SetMeasurementEventCallback<float>(OnMeasurement);
        _listener.SetMeasurementEventCallback<short>(OnMeasurement);
        _listener.SetMeasurementEventCallback<byte>(OnMeasurement);
        _listener.SetMeasurementEventCallback<decimal>(OnMeasurement);

        _listener.Start();
    }

    /// <summary>All measurements captured so far, in insertion order.</summary>
    public IReadOnlyList<RecordedMeasurement> Measurements
    {
        get
        {
            lock (_gate)
            {
                return _measurements.ToArray();
            }
        }
    }

    /// <summary>
    /// Filter measurements by instrument name (e.g.
    /// <c>tenant_client_cache.write.success</c>).
    /// </summary>
    public IReadOnlyList<RecordedMeasurement> ForInstrument(string instrumentName)
    {
        lock (_gate)
        {
            return _measurements.Where(m => m.InstrumentName == instrumentName).ToArray();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _measurements.Clear();
        }
    }

    /// <summary>
    /// Force every observable instrument the listener has subscribed to
    /// (e.g. <c>tenant_client_cache.refresh.last_completed_at</c>) to
    /// publish its current value. Counters / histograms publish eagerly
    /// so this is only needed when asserting on gauges.
    /// </summary>
    public void RecordObservableInstruments() => _listener.RecordObservableInstruments();

    public void Dispose() => _listener.Dispose();

    private void OnMeasurement<T>(
        Instrument instrument,
        T measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
        where T : struct
    {
        var tagDict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            tagDict[tag.Key] = tag.Value;
        }

        var record = new RecordedMeasurement(
            instrument.Name,
            Convert.ToDouble(measurement),
            tagDict);

        lock (_gate)
        {
            _measurements.Add(record);
        }
    }
}
