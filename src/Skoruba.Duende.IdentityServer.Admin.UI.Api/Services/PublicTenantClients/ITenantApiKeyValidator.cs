// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

using System;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.PublicTenantClients;

/// <summary>
/// Validator for the per-tenant API key carried by header
/// <c>X-Tenant-Api-Key</c> on the public-read endpoint
/// (<c>GET /api/public/tenants/{tenantKey}/clients/{clientId}</c>).
/// </summary>
/// <remarks>
/// <para>
/// The implementation hashes <paramref name="apiKeyPlaintext"/> with SHA-256
/// (UTF-8 encoded, no BOM) and compares the result to the hex digest stored
/// in <c>TenantClientCachePublicRead:ApiKeys[normalizedTenantKey]</c> using a
/// constant-time comparison (R3.2). Validation is re-evaluated against
/// <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}.CurrentValue"/>
/// on every call so hot-reload-driven revocation (R1.6) takes effect on the
/// very next request (R3.5).
/// </para>
/// <para>
/// Caller MUST pre-normalize <paramref name="normalizedTenantKey"/> via
/// <c>tenantKey.Trim().ToLowerInvariant()</c> (R2.3) BEFORE calling this
/// method. Implementations look up the configured hash by exact ordinal
/// match — passing a non-normalized key will silently miss.
/// </para>
/// </remarks>
public interface ITenantApiKeyValidator
{
    /// <summary>
    /// Returns <c>true</c> iff <paramref name="apiKeyPlaintext"/> matches the
    /// configured SHA-256 hex digest for <paramref name="normalizedTenantKey"/>.
    /// </summary>
    /// <param name="normalizedTenantKey">
    /// Tenant key already normalized via <c>Trim().ToLowerInvariant()</c>
    /// (R2.3). Empty / unregistered keys cause this method to return
    /// <c>false</c> after best-effort timing parity with the registered path
    /// (R3.3 anti-enumeration).
    /// </param>
    /// <param name="apiKeyPlaintext">
    /// Raw header value as a span over the request header characters. The
    /// span is consumed in-place; it is never persisted nor logged (R3.4).
    /// </param>
    /// <returns>
    /// <c>true</c> when the SHA-256 of <paramref name="apiKeyPlaintext"/>
    /// matches the configured digest under
    /// <see cref="System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.ReadOnlySpan{byte}, System.ReadOnlySpan{byte})"/>;
    /// <c>false</c> otherwise.
    /// </returns>
    bool TryValidate(string normalizedTenantKey, ReadOnlySpan<char> apiKeyPlaintext);
}
