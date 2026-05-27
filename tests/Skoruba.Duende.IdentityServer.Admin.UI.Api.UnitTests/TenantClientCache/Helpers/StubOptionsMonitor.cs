// Feature: tenant-client-cache-expansion, Task 5
//
// Minimal IOptionsMonitor<T> double — backed by a single mutable instance
// the tests can swap to flip Enabled / TTL values mid-test. We deliberately
// do NOT raise change tokens; TenantClientCacheService re-reads
// `_options.CurrentValue` on every call so a static reference is enough.

#nullable enable

using System;
using Microsoft.Extensions.Options;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache.Helpers;

internal sealed class StubOptionsMonitor<T> : IOptionsMonitor<T> where T : class, new()
{
    private T _value;

    public StubOptionsMonitor(T value)
    {
        _value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public T CurrentValue => _value;

    public T Get(string? name) => _value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;

    public void Set(T value) => _value = value;
}
