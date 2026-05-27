// Feature: tenant-client-cache-public-read, Task 10
//
// Integration-test mirror of the unit-test helper of the same name. We
// redeclare it here so the integration test project does not take a
// project reference on the unit-test assembly. Only the subset used by
// these tests is exposed.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Tests.PublicTenantClients.Helpers;

internal sealed record RecordedMeasurement(
    string InstrumentName,
    double Value,
    IReadOnlyDictionary<string, object?> Tags);

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
            },
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

    public IReadOnlyList<RecordedMeasurement> ForInstrument(string instrumentName)
    {
        lock (_gate)
        {
            return _measurements
                .Where(m => string.Equals(m.InstrumentName, instrumentName, StringComparison.Ordinal))
                .ToArray();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _measurements.Clear();
        }
    }

    public void Dispose() => _listener.Dispose();

    private void OnMeasurement<T>(
        Instrument instrument,
        T measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
        where T : struct
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            dict[tag.Key] = tag.Value;
        }
        var record = new RecordedMeasurement(
            instrument.Name,
            Convert.ToDouble(measurement),
            dict);
        lock (_gate)
        {
            _measurements.Add(record);
        }
    }
}
