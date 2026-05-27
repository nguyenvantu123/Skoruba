// Feature: tenant-client-cache-expansion, Task 5
//
// Minimal in-memory ILogger<T> implementation used to assert the structured
// fields emitted by TenantClientCacheService. Captures level, message, the
// raw FormattedLogValues (so each structured field is queryable by name)
// and the exception (if any).
//
// Task 6 will extend this helper with thread-safety + multi-test reuse;
// for Task 5 the simpler shape is enough.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache.Helpers;

internal sealed record CapturedLogEntry(
    LogLevel Level,
    string Message,
    IReadOnlyDictionary<string, object?> Fields,
    Exception? Exception);

internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<CapturedLogEntry> _entries = new();

    public IReadOnlyList<CapturedLogEntry> Entries => _entries;

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (state is IEnumerable<KeyValuePair<string, object?>> kvps)
        {
            foreach (var kv in kvps)
            {
                // The {OriginalFormat} entry is the template itself; keep
                // it but expose every structured field by its template name.
                fields[kv.Key] = kv.Value;
            }
        }

        _entries.Add(new CapturedLogEntry(logLevel, message, fields, exception));
    }

    public void Clear() => _entries.Clear();

    public IEnumerable<CapturedLogEntry> WhereField(string fieldName, object? expectedValue)
        => _entries.Where(e =>
            e.Fields.TryGetValue(fieldName, out var v)
            && Equals(v, expectedValue));

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
