// Feature: tenant-client-cache-expansion, Task 5
//
// Lightweight TimeProvider double for unit tests. Avoids pulling in the
// Microsoft.Extensions.TimeProvider.Testing package (Task 5 forbids new
// NuGet references). Only overrides GetUtcNow because that's the single
// hook TenantClientCacheService uses.

#nullable enable

using System;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache.Helpers;

internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public FakeTimeProvider(DateTimeOffset initialUtcNow)
    {
        _utcNow = initialUtcNow;
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Set(DateTimeOffset utcNow) => _utcNow = utcNow;

    public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
}
