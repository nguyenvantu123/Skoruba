// Feature: tenant-client-cache-expansion, Task 10
//
// Lightweight ILoggerProvider used by integration tests to assert that the
// TenantClientCache audit events are emitted (R17.4 cases (e), (f), (g),
// background-refresh paths). Stores entries with their structured fields
// preserved so individual tests can filter by EventType / Outcome / Subreason
// without paying the cost of a Serilog sink.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Common;

internal sealed record CapturedLogEntry(
    string Category,
    LogLevel Level,
    string Message,
    IReadOnlyDictionary<string, object?> Fields,
    Exception? Exception);

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentBag<CapturedLogEntry> _entries = new();

    public IReadOnlyList<CapturedLogEntry> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Clear()
    {
        while (_entries.TryTake(out _)) { /* drain */ }
    }

    public void Dispose() { /* nothing to release */ }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentBag<CapturedLogEntry> _entries;

        public CapturingLogger(string category, ConcurrentBag<CapturedLogEntry> entries)
        {
            _category = category;
            _entries = entries;
        }

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
                    fields[kv.Key] = kv.Value;
                }
            }

            _entries.Add(new CapturedLogEntry(_category, logLevel, message, fields, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
