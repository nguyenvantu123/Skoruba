// Feature: tenant-client-cache-public-read, Task 11/12 — STS.Identity consumer wrapper
//
// No-op provider used when PublicTenantClientSnapshotConsumer:Enabled=false (the
// shipping default). Returns Outcome=Disabled for every call. The SDK is never
// resolved or invoked, so the host can start cleanly without BaseAddress / ApiKey
// being populated.

#nullable enable

using System.Threading;
using System.Threading.Tasks;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Services;

internal sealed class DisabledPublicTenantClientSnapshotProvider : IPublicTenantClientSnapshotProvider
{
    private static readonly PublicClientSnapshotLookup DisabledLookup =
        new(Snapshot: null, Outcome: PublicClientSnapshotOutcome.Disabled, RetryAfter: null);

    public Task<PublicClientSnapshotLookup> GetSnapshotAsync(
        string clientId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(DisabledLookup);
}
