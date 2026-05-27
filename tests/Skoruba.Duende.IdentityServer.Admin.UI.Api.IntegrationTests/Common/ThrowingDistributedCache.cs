// Feature: tenant-client-cache-expansion, Task 10
//
// IDistributedCache decorator used by integration tests to simulate Redis
// outage scenarios. Mirrors the unit-test helper of the same name from the
// sibling Admin.UI.Api.UnitTests project (verbatim shape) — copied here
// instead of cross-referencing the unit-test project so the integration
// test project remains self-contained.
//
// The decorator keeps state in two flavours:
//   * Pending throws scheduled via NextNCallsThrow / ThrowOnNext fire on
//     the next async cache method call (Get/Set/Remove/Refresh) and
//     decrement until exhausted.
//   * AlwaysThrow flips the decorator to fail every call until reset
//     (used by the Redis-down R17.4 case (e) test that asserts CRUD
//      success HTTP codes when Redis is unreachable for the entire op).

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Common;

internal sealed class ThrowingDistributedCache : IDistributedCache
{
    private readonly IDistributedCache _inner;
    private readonly object _gate = new();

    private int _remainingThrows;
    private Func<Exception>? _exceptionFactory;
    private Func<Exception>? _alwaysThrowFactory;

    private int _getAsyncCallCount;
    private int _setAsyncCallCount;
    private int _removeAsyncCallCount;
    private int _refreshAsyncCallCount;

    public ThrowingDistributedCache(IDistributedCache? inner = null)
    {
        _inner = inner ?? new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
    }

    /// <summary>Throw once on the very next async cache method call.</summary>
    public void ThrowOnNext(Exception exception)
    {
        if (exception is null) throw new ArgumentNullException(nameof(exception));
        NextNCallsThrow(1, () => exception);
    }

    /// <summary>Throw on each of the next <paramref name="callCount"/> calls.</summary>
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

    /// <summary>
    /// Make every async call throw the exception produced by
    /// <paramref name="factory"/> until <see cref="ResetAlwaysThrow"/>.
    /// </summary>
    public void AlwaysThrow(Func<Exception> factory)
    {
        if (factory is null) throw new ArgumentNullException(nameof(factory));

        lock (_gate)
        {
            _alwaysThrowFactory = factory;
        }
    }

    public void ResetAlwaysThrow()
    {
        lock (_gate)
        {
            _alwaysThrowFactory = null;
        }
    }

    public int GetAsyncCallCount => Volatile.Read(ref _getAsyncCallCount);
    public int SetAsyncCallCount => Volatile.Read(ref _setAsyncCallCount);
    public int RemoveAsyncCallCount => Volatile.Read(ref _removeAsyncCallCount);
    public int RefreshAsyncCallCount => Volatile.Read(ref _refreshAsyncCallCount);

    public int TotalAsyncCallCount =>
        GetAsyncCallCount + SetAsyncCallCount + RemoveAsyncCallCount + RefreshAsyncCallCount;

    public IDistributedCache Inner => _inner;

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
            if (_alwaysThrowFactory is not null)
            {
                factory = _alwaysThrowFactory;
            }
            else if (_remainingThrows > 0 && _exceptionFactory is not null)
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

    // ===== Sync passthroughs =====
    public byte[]? Get(string key) => _inner.Get(key);
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _inner.Set(key, value, options);
    public void Refresh(string key) => _inner.Refresh(key);
    public void Remove(string key) => _inner.Remove(key);

    // ===== Async paths =====
    public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        Interlocked.Increment(ref _getAsyncCallCount);
        MaybeThrow();
        return _inner.GetAsync(key, token);
    }

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
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
