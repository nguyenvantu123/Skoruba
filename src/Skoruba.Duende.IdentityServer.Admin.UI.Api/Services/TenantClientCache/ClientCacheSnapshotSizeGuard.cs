// Feature: tenant-client-cache-expansion, Task 3
//
// Hard 256 KiB ceiling on serialized snapshot payloads. Pulled into its
// own type so callers (TenantClientCacheService at write time, plus tests)
// can share the constant and the predicate without dragging in the rest
// of the cache surface.
//
// Validates: Requirements 2.6, 14.5

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

internal static class ClientCacheSnapshotSizeGuard
{
    /// <summary>
    /// Maximum snapshot size, in bytes, after UTF-8 JSON serialization.
    /// 256 KiB matches the requirements.md ceiling (R2.6) and the design
    /// document's NFR table.
    /// </summary>
    public const int MaxBytes = 256 * 1024;

    /// <summary>
    /// True iff <paramref name="payload"/> is at or below the 256 KiB
    /// ceiling. Inclusive (== <c>MaxBytes</c> is allowed).
    /// </summary>
    public static bool IsWithinLimit(byte[] payload)
        => payload.Length <= MaxBytes;
}
