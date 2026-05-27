// Feature: tenant-client-cache-public-read, Task 11/12 — STS.Identity consumer wrapper
//
// Strongly-typed configuration POCO for the STS.Identity consumer wrapper around
// the TenantClientCache SDK. Bound from the appsettings.json section
// "PublicTenantClientSnapshotConsumer". The wrapper is FAIL-CLOSED by default:
// when Enabled=false (the shipping default) the host registers a no-op provider
// and never calls into the SDK validator, so missing BaseAddress / ApiKey at host
// startup do NOT crash the app.
//
// Operators populate the values via environment variables, e.g.:
//   PublicTenantClientSnapshotConsumer__Enabled=true
//   PublicTenantClientSnapshotConsumer__BaseAddress=https://identity.example.com
//   PublicTenantClientSnapshotConsumer__ApiKey=<plaintext-api-key>
//
// SDK contract: Skoruba.Duende.IdentityServer.TenantClientCache.Client.

#nullable enable

namespace Skoruba.Duende.IdentityServer.STS.Identity.Services;

/// <summary>
/// Options bound from the <c>PublicTenantClientSnapshotConsumer</c> configuration
/// section. Surface mirrors the SDK options (<see cref="Skoruba.Duende.IdentityServer.TenantClientCache.Client.TenantClientCacheClientOptions"/>)
/// plus a single boolean kill-switch (<see cref="Enabled"/>) so the host can be
/// started cleanly without forcing a real SDK configuration.
/// </summary>
public sealed class PublicTenantClientSnapshotConsumerConfiguration
{
    /// <summary>Configuration section name. Bound from <c>appsettings.json</c>.</summary>
    public const string SectionName = "PublicTenantClientSnapshotConsumer";

    /// <summary>
    /// Master kill switch. When <see langword="false"/> (the shipping default)
    /// the wrapper registers a no-op provider that returns
    /// <see cref="PublicClientSnapshotOutcome.Disabled"/> without ever calling the
    /// SDK. When <see langword="true"/> the wrapper wires
    /// <see cref="Skoruba.Duende.IdentityServer.TenantClientCache.Client.TenantClientCacheClientServiceCollectionExtensions.AddTenantClientCacheClient(Microsoft.Extensions.DependencyInjection.IServiceCollection, System.Action{Skoruba.Duende.IdentityServer.TenantClientCache.Client.TenantClientCacheClientOptions})"/>
    /// and lets the SDK validator fail-fast on missing
    /// <see cref="BaseAddress"/> / <see cref="ApiKey"/> at host startup
    /// (production fail-fast).
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Absolute base URL of the public-read endpoint (e.g.
    /// <c>https://identity.example.com</c>). Required when <see cref="Enabled"/>
    /// is <see langword="true"/>; the SDK validator enforces the actual format.
    /// </summary>
    public string? BaseAddress { get; set; }

    /// <summary>
    /// Per-tenant API key plaintext sent in the <c>X-Tenant-Api-Key</c> header.
    /// Operators MUST source this from a secret store / env var, never from
    /// committed configuration files.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>HTTP timeout in seconds. Default 5. Range [1, 60].</summary>
    public int HttpTimeoutSeconds { get; set; } = 5;

    /// <summary>Maximum number of retries on top of the initial call. Default 2. Range [0, 5].</summary>
    public int MaxRetryAttempts { get; set; } = 2;

    /// <summary>Base delay for exponential backoff in milliseconds. Default 200. Range [10, 5000].</summary>
    public int RetryBaseDelayMilliseconds { get; set; } = 200;

    /// <summary>Upper bound on the in-memory cache TTL in seconds. Default 300. Range [0, 3600].</summary>
    public int MaxClientCacheTtlSeconds { get; set; } = 300;

    /// <summary>Master switch for the SDK in-memory cache. Default <see langword="true"/>.</summary>
    public bool EnableInMemoryCaching { get; set; } = true;
}
