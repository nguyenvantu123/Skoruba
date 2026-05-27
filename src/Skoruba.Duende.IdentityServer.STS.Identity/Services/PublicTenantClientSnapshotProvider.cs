// Feature: tenant-client-cache-public-read, Task 11/12 — STS.Identity consumer wrapper
//
// Real implementation of <see cref="IPublicTenantClientSnapshotProvider"/>. Resolves
// tenantKey automatically from <see cref="ITenantContextAccessor.Current"/>, then
// delegates to the SDK <see cref="ITenantClientCacheClient"/>.
//
// Logging policy:
//   - Information: never (the SDK already records hits/misses via metrics).
//   - Warning: on every fail-soft path (no tenant context, invalid clientId, SDK
//     non-success outcomes). Structured fields are LIMITED to {tenantKey, clientId,
//     outcome}. The API key plaintext, hash digest, BaseAddress, and snapshot body
//     are NEVER logged.

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Skoruba.Duende.IdentityServer.TenantClientCache.Client;
using Skoruba.Duende.IdentityServer.TenantClientCache.Client.Models;

using TenantInfrastructure.Abstractions;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Services;

internal sealed class PublicTenantClientSnapshotProvider : IPublicTenantClientSnapshotProvider
{
    private readonly ITenantClientCacheClient _sdk;
    private readonly ITenantContextAccessor _tenantContext;
    private readonly ILogger<PublicTenantClientSnapshotProvider> _logger;

    public PublicTenantClientSnapshotProvider(
        ITenantClientCacheClient sdk,
        ITenantContextAccessor tenantContext,
        ILogger<PublicTenantClientSnapshotProvider> logger)
    {
        _sdk = sdk ?? throw new ArgumentNullException(nameof(sdk));
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<PublicClientSnapshotLookup> GetSnapshotAsync(
        string clientId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            _logger.LogWarning(
                "PublicTenantClientSnapshotProvider rejected request: clientId was null or whitespace.");
            return new PublicClientSnapshotLookup(
                Snapshot: null,
                Outcome: PublicClientSnapshotOutcome.InvalidClientId,
                RetryAfter: null);
        }

        var tenantKey = _tenantContext.Current?.TenantKey;
        if (string.IsNullOrWhiteSpace(tenantKey))
        {
            _logger.LogWarning(
                "PublicTenantClientSnapshotProvider rejected request for clientId={ClientId}: no tenant context resolved on the current request.",
                clientId);
            return new PublicClientSnapshotLookup(
                Snapshot: null,
                Outcome: PublicClientSnapshotOutcome.NoTenantContext,
                RetryAfter: null);
        }

        TenantClientSnapshotResult sdkResult;
        try
        {
            sdkResult = await _sdk
                .GetClientAsync(tenantKey, clientId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller-driven cancellation; propagate without logging (not a failure).
            throw;
        }
        catch (Exception ex)
        {
            // Defensive: SDK is documented as fail-soft, but if it ever throws we
            // do NOT propagate the exception out of the wrapper. Tenant key and
            // client id are safe to log; the API key is not part of the exception
            // surface and is therefore never echoed here.
            _logger.LogWarning(
                ex,
                "PublicTenantClientSnapshotProvider absorbed unexpected SDK exception for tenantKey={TenantKey}, clientId={ClientId}.",
                tenantKey,
                clientId);
            return new PublicClientSnapshotLookup(
                Snapshot: null,
                Outcome: PublicClientSnapshotOutcome.Unavailable,
                RetryAfter: null);
        }

        return MapResult(sdkResult, tenantKey, clientId);
    }

    private PublicClientSnapshotLookup MapResult(
        TenantClientSnapshotResult sdkResult,
        string tenantKey,
        string clientId)
    {
        switch (sdkResult.Outcome)
        {
            case SdkCacheOutcome.Hit:
            case SdkCacheOutcome.Miss:
            case SdkCacheOutcome.NotModified:
                return new PublicClientSnapshotLookup(
                    Snapshot: sdkResult.Snapshot,
                    Outcome: PublicClientSnapshotOutcome.Snapshot,
                    RetryAfter: sdkResult.RetryAfter);

            case SdkCacheOutcome.NotFound:
                _logger.LogWarning(
                    "PublicTenantClientSnapshotProvider observed NotFound for tenantKey={TenantKey}, clientId={ClientId}.",
                    tenantKey,
                    clientId);
                return new PublicClientSnapshotLookup(
                    Snapshot: null,
                    Outcome: PublicClientSnapshotOutcome.NotFound,
                    RetryAfter: sdkResult.RetryAfter);

            case SdkCacheOutcome.Unauthorized:
                _logger.LogWarning(
                    "PublicTenantClientSnapshotProvider observed Unauthorized for tenantKey={TenantKey}, clientId={ClientId}. Verify the consumer API key is registered for this tenant.",
                    tenantKey,
                    clientId);
                return new PublicClientSnapshotLookup(
                    Snapshot: null,
                    Outcome: PublicClientSnapshotOutcome.Unauthorized,
                    RetryAfter: sdkResult.RetryAfter);

            case SdkCacheOutcome.RateLimited:
                _logger.LogWarning(
                    "PublicTenantClientSnapshotProvider observed RateLimited for tenantKey={TenantKey}, clientId={ClientId}, retryAfter={RetryAfterSeconds}s.",
                    tenantKey,
                    clientId,
                    sdkResult.RetryAfter?.TotalSeconds);
                return new PublicClientSnapshotLookup(
                    Snapshot: null,
                    Outcome: PublicClientSnapshotOutcome.RateLimited,
                    RetryAfter: sdkResult.RetryAfter);

            case SdkCacheOutcome.ServiceUnavailable:
            case SdkCacheOutcome.TransientFailure:
            default:
                _logger.LogWarning(
                    "PublicTenantClientSnapshotProvider observed transient failure ({SdkOutcome}) for tenantKey={TenantKey}, clientId={ClientId}, retryAfter={RetryAfterSeconds}s.",
                    sdkResult.Outcome,
                    tenantKey,
                    clientId,
                    sdkResult.RetryAfter?.TotalSeconds);
                return new PublicClientSnapshotLookup(
                    Snapshot: null,
                    Outcome: PublicClientSnapshotOutcome.Unavailable,
                    RetryAfter: sdkResult.RetryAfter);
        }
    }
}
