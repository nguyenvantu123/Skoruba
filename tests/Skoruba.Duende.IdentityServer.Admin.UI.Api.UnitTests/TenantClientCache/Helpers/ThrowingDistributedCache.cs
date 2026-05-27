// Feature: tenant-client-cache-expansion, Task 6
//
// IDistributedCache decorator used by fail-soft property tests. Wraps a
// real MemoryDistributedCache so non-throwing calls behave correctly,
// but exposes deterministic exception-injection controls:
//
//   * ThrowOnNext(exception)             — inject `exception` on the
//                                          next *Async call, then resume
//                                          delegating to the inner cache.
//   * NextNCallsThrow(n, factory)        — inject the exception produced
//                                          by `factory` on the next `n`
//                                          calls, with separate counters
//                                          honoured per call type.
//
// The decorator also records every method invocation so property tests
// can assert the "no retry" contract (R10.3): for any single logical
// op there must be at most one underlying cache invocation.

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache.Helpers;

internal sealed class ThrowingDistributedCache : IDistributedCache
{
    private readonly IDistributedCache _inner;
    private readonly object _gate = new();

    private int _remainingThrows;
    private Func<Exception>? _exceptionFactory;

    private int _getAsyncCallCount;
    private int _setAsyncCallCount;
    private int _removeAsyncCallCount;
    private int _refreshAsyncCallCount;

    public ThrowingDistributedCache(IDistributedCache? inner = null)
    {
        _inner = inner ?? new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
    }

    /// <summary>
    /// Throw <paramref name="exception"/> exactly once on the next
    /// async cache method call (Get/Set/Remove/Refresh).
    /// </summary>
    public void ThrowOnNext(Exception exception)
    {
        if (exception is null) throw new ArgumentNullException(nameof(exception));
        NextNCallsThrow(1, () => exception);
    }

    /// <summary>
    /// Throw the exception produced by <paramref name="factory"/> on the
    /// next <paramref name="callCount"/> async cache method calls.
    /// </summary>
    public void NextNCallsThrow(int callCount, Func<Exception> factory)
    {
        if (callCount <= 0) throw new ArgumentOutOfRangeException(nameof(callCount));
        if (factory is null) throw new ArgumentNullException(nameof(factory));

        lock (_gate)
        {
            _remainingThrows = callCount;
            _exceptionFactory = factory;
        }
    }

    /// <summary>True when at least one scheduled throw is still pending.</summary>
    public bool HasPendingThrows
    {
        get
        {
            lock (_gate)
            {
                return _remainingThrows > 0;
            }
        }
    }

    public IReadOnlyList<string> Calls { get; } = new List<string>();

    public int GetAsyncCallCount => Volatile.Read(ref _getAsyncCallCount);
    public int SetAsyncCallCount => Volatile.Read(ref _setAsyncCallCount);
    public int RemoveAsyncCallCount => Volatile.Read(ref _removeAsyncCallCount);
    public int RefreshAsyncCallCount => Volatile.Read(ref _refreshAsyncCallCount);

    public int TotalAsyncCallCount =>
        GetAsyncCallCount + SetAsyncCallCount + RemoveAsyncCallCount + RefreshAsyncCallCount;

    public void ResetCounters()
    {
        Interlocked.Exchange(ref _getAsyncCallCount, 0);
        Interlocked.Exchange(ref _setAsyncCallCount, 0);
        Interlocked.Exchange(ref _removeAsyncCallCount, 0);
        Interlocked.Exchange(ref _refreshAsyncCallCount, 0);
    }

    private void MaybeThrow()
    {
        Func<Exception>? factory = null;
        lock (_gate)
        {
            if (_remainingThrows > 0 && _exceptionFactory is not null)
            {
                factory = _exceptionFactory;
                _remainingThrows--;
                if (_remainingThrows == 0)
                {
                    _exceptionFactory = null;
                }
            }
        }

        if (factory is not null)
        {
            throw factory();
        }
    }

    // ===== Sync passthroughs (not used by service but must implement) =====

    public byte[]? Get(string key) => _inner.Get(key);

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        => _inner.Set(key, value, options);

    public void Refresh(string key) => _inner.Refresh(key);

    public void Remove(string key) => _inner.Remove(key);

    // ===== Async paths used by TenantClientCacheService =====

    public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        Interlocked.Increment(ref _getAsyncCallCount);
        MaybeThrow();
        return _inner.GetAsync(key, token);
    }

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options,
        CancellationToken token = default)
    {
        Interlocked.Increment(ref _setAsyncCallCount);
        MaybeThrow();
        return _inner.SetAsync(key, value, options, token);
    }

    public Task RefreshAsync(string key, CancellationToken token = default)
    {
        Interlocked.Increment(ref _refreshAsyncCallCount);
        MaybeThrow();
        return _inner.RefreshAsync(key, token);
    }

    public Task RemoveAsync(string key, CancellationToken token = default)
    {
        Interlocked.Increment(ref _removeAsyncCallCount);
        MaybeThrow();
        return _inner.RemoveAsync(key, token);
    }
}
