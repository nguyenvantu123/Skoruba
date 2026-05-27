// Feature: tenant-client-cache-expansion
// Outcome enum used by ITenantClientCacheService to surface a single value
// that drives both structured logs and metrics.
//
// Source: Glossary `Cache_Outcome` in requirements.md.

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

public enum Cache_Outcome
{
    /// <summary>Read found a non-stale envelope.</summary>
    Hit,

    /// <summary>Read found nothing (or a corrupt payload that was discarded).</summary>
    Miss,

    /// <summary>Envelope present but its schema version is greater than the reader can handle.</summary>
    Stale,

    /// <summary>Write succeeded.</summary>
    WriteSucceeded,

    /// <summary>
    /// Write was skipped because <c>TenantClientCacheOptions.Enabled</c> is
    /// <c>false</c> or because Tenant_Scope_Resolution returned an empty set.
    /// </summary>
    WriteSkippedDisabled,

    /// <summary>Write failed (Redis down, timeout, oversize, serialization error). Fail_Soft.</summary>
    WriteFailedTransient,

    /// <summary>Invalidate succeeded (or was idempotent on a missing key).</summary>
    InvalidateSucceeded,

    /// <summary>Invalidate failed (Redis down, timeout). Fail_Soft.</summary>
    InvalidateFailedTransient,
}
