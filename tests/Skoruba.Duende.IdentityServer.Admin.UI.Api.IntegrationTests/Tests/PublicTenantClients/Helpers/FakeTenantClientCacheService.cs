// Feature: tenant-client-cache-public-read, Task 10
//
// Test double for ITenantClientCacheService. The integration tests for the
// public-read pipeline need to drive every terminal outcome of the pipeline
// (200 / 304 / 404 / 503 transient / 503 pipeline-disabled) without going
// near the production write side. The fake exposes:
//
//   * WhenAnyKey_Returns(envelope)        — every (tenantKey, clientId)
//                                            request returns the canned envelope
//   * WhenKey_Returns(t, c, envelope)     — per-key canned envelope
//   * WhenAnyKey_Throws(exception)        — every read throws (drives R7.5
//                                            via PublicReadExceptionFilter)
//   * WhenAnyKey_ReturnsNull()             — drives R7.3 snapshot_not_found
//   * WhenAnyKey_PipelineDisabled()       — returns sentinel envelope
//                                            with Version <= 0 so the
//                                            controller returns R7.4
//                                            snapshot_pipeline_disabled
//   * Verify_Calls(expected)              — assertion helper around the
//                                            captured call log
//   * Calls                                — all observed (tenantKey,
//                                            clientId) pairs in order
//
// Validates: Requirements 7.3, 7.4, 7.5 (test infrastructure)

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Tests.PublicTenantClients.Helpers;

/// <summary>
/// Lightweight in-memory <see cref="ITenantClientCacheService"/> used by
/// every integration test in <c>Tests/PublicTenantClients/</c>. The fake
/// captures every call and lets each test stage canned responses without
/// touching the production cache writer / Redis.
/// </summary>
internal sealed class FakeTenantClientCacheService : ITenantClientCacheService
{
    private readonly object _gate = new();
    private readonly ConcurrentBag<RecordedCall> _calls = new();
    private readonly Dictionary<string, ClientCacheSnapshotEnvelope?> _perKey = new(StringComparer.Ordinal);

    private ClientCacheSnapshotEnvelope? _defaultEnvelope;
    private bool _hasDefault;
    private Func<Exception>? _throwFactory;

    public IReadOnlyList<RecordedCall> Calls
    {
        get
        {
            // Snapshot for predictable assertions.
            var arr = _calls.ToArray();
            Array.Sort(arr, (a, b) => a.Sequence.CompareTo(b.Sequence));
            return arr;
        }
    }

    /// <summary>Stage a canned envelope returned for every key.</summary>
    public FakeTenantClientCacheService WhenAnyKey_Returns(ClientCacheSnapshotEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        lock (_gate)
        {
            _defaultEnvelope = envelope;
            _hasDefault = true;
            _throwFactory = null;
        }
        return this;
    }

    /// <summary>Stage a canned envelope returned for a specific key.</summary>
    public FakeTenantClientCacheService WhenKey_Returns(
        string tenantKey,
        string clientId,
        ClientCacheSnapshotEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(tenantKey);
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(envelope);
        lock (_gate)
        {
            _perKey[BuildKey(tenantKey, clientId)] = envelope;
        }
        return this;
    }

    /// <summary>
    /// Stage a synthetic exception. Each call to <see cref="ReadSnapshotAsync"/>
    /// invokes <paramref name="exceptionFactory"/> and throws the result so the
    /// PublicReadExceptionFilter sees the exception (R7.5).
    /// </summary>
    public FakeTenantClientCacheService WhenAnyKey_Throws(Func<Exception> exceptionFactory)
    {
        ArgumentNullException.ThrowIfNull(exceptionFactory);
        lock (_gate)
        {
            _throwFactory = exceptionFactory;
            _hasDefault = false;
            _defaultEnvelope = null;
        }
        return this;
    }

    /// <summary>Convenience overload — single fixed exception.</summary>
    public FakeTenantClientCacheService WhenAnyKey_Throws(Exception exception)
        => WhenAnyKey_Throws(() => exception);

    /// <summary>Stage a null envelope so the controller returns 404 (R7.3).</summary>
    public FakeTenantClientCacheService WhenAnyKey_ReturnsNull()
    {
        lock (_gate)
        {
            _defaultEnvelope = null;
            _hasDefault = true;
            _throwFactory = null;
        }
        return this;
    }

    /// <summary>
    /// Stage the sentinel envelope for the Snapshot_Pipeline_Disabled state
    /// (Version &lt;= 0) so the controller returns R7.4
    /// <c>snapshot_pipeline_disabled</c> with <c>Retry-After: 60</c>.
    /// </summary>
    public FakeTenantClientCacheService WhenAnyKey_PipelineDisabled()
    {
        var sentinel = new ClientCacheSnapshotEnvelope
        {
            Version = 0,
            TenantKey = "__disabled__",
            ClientId = "__disabled__",
            LastWriteUtc = new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Data = new ClientCacheSnapshotDto(),
        };
        lock (_gate)
        {
            _defaultEnvelope = sentinel;
            _hasDefault = true;
            _throwFactory = null;
        }
        return this;
    }

    public void Reset()
    {
        lock (_gate)
        {
            _calls.Clear();
            _perKey.Clear();
            _defaultEnvelope = null;
            _hasDefault = false;
            _throwFactory = null;
        }
    }

    public Task<ClientCacheSnapshotEnvelope?> ReadSnapshotAsync(
        string tenantKey,
        string clientId,
        CancellationToken cancellationToken)
    {
        var sequence = System.Threading.Interlocked.Increment(ref _sequenceCounter);
        _calls.Add(new RecordedCall(sequence, tenantKey, clientId));

        Func<Exception>? throwFactory;
        ClientCacheSnapshotEnvelope? perKey = null;
        bool hasPerKey;
        ClientCacheSnapshotEnvelope? defaultEnvelope;
        bool hasDefault;
        lock (_gate)
        {
            throwFactory = _throwFactory;
            hasPerKey = _perKey.TryGetValue(BuildKey(tenantKey, clientId), out perKey);
            defaultEnvelope = _defaultEnvelope;
            hasDefault = _hasDefault;
        }

        if (throwFactory is not null)
        {
            throw throwFactory();
        }

        if (hasPerKey)
        {
            return Task.FromResult(perKey);
        }

        if (hasDefault)
        {
            return Task.FromResult(defaultEnvelope);
        }

        return Task.FromResult<ClientCacheSnapshotEnvelope?>(null);
    }

    public Task WriteSnapshotAsync(
        string tenantKey,
        ClientDto client,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task WriteSnapshotsAsync(
        IReadOnlyCollection<string> tenantKeys,
        ClientDto client,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task InvalidateSnapshotAsync(
        string tenantKey,
        string clientId,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task InvalidateSnapshotsAsync(
        IReadOnlyCollection<string> tenantKeys,
        string clientId,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    private static string BuildKey(string tenantKey, string clientId)
        => $"{tenantKey}::{clientId}";

    private long _sequenceCounter;

    internal sealed record RecordedCall(long Sequence, string TenantKey, string ClientId);
}
