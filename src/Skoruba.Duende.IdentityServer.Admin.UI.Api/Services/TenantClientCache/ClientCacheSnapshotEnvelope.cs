// Feature: tenant-client-cache-expansion
// Envelope that wraps a ClientCacheSnapshotDto with the metadata required to
// disambiguate it across tenants and to detect future schema drift.
//
// Shape (R2.3):
//   {
//     "version":      <int>,         // 1 in this feature; readers MUST treat
//                                    // version > 1 as Cache_Outcome.Stale.
//     "tenantKey":    <string>,
//     "clientId":     <string>,
//     "lastWriteUtc": <ISO 8601 UTC>,
//     "data":         { ...Public_Safe_Fields... }
//   }
//
// Validates: Requirements 2.3, 2.7

using System;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

public sealed class ClientCacheSnapshotEnvelope
{
    /// <summary>
    /// Snapshot schema version. Hard-coded to <c>1</c> in this feature.
    /// Readers MUST surface <c>Cache_Outcome.Stale</c> when they observe a
    /// value greater than what they are designed to handle.
    /// </summary>
    public int Version { get; init; }

    /// <summary>
    /// Normalized tenant key (caller-supplied; service layer normalizes
    /// before composing the cache key).
    /// </summary>
    public string TenantKey { get; init; } = "";

    /// <summary>
    /// Logical Duende client id (case-sensitive per Duende contract).
    /// </summary>
    public string ClientId { get; init; } = "";

    /// <summary>
    /// UTC timestamp when the cache write that produced this envelope ran.
    /// Mirrors <c>Data.LastWriteUtc</c>; consumers may use either.
    /// </summary>
    public DateTime LastWriteUtc { get; init; }

    /// <summary>
    /// Public-safe payload (whitelisted fields only).
    /// </summary>
    public ClientCacheSnapshotDto Data { get; init; } = default!;
}
