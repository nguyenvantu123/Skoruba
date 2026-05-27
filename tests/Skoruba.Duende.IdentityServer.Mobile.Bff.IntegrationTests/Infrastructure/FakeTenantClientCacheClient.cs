// Test fake for ITenantClientCacheClient. Each test stages an outcome via
// the helpers below and the BFF handler observes the prepared response.

using Skoruba.Duende.IdentityServer.TenantClientCache.Client;
using Skoruba.Duende.IdentityServer.TenantClientCache.Client.Models;

namespace Skoruba.Duende.IdentityServer.Mobile.Bff.IntegrationTests.Infrastructure;

public sealed class FakeTenantClientCacheClient : ITenantClientCacheClient
{
    private Func<string, string, string?, CancellationToken, TenantClientSnapshotResult> _responder
        = static (_, _, _, _) => new TenantClientSnapshotResult(
            Snapshot: null,
            Etag: null,
            LastWriteUtc: null,
            Version: null,
            Outcome: SdkCacheOutcome.NotFound,
            RetryAfter: null);

    public string? LastTenantKey { get; private set; }
    public string? LastClientId { get; private set; }
    public string? LastIfNoneMatch { get; private set; }
    public int CallCount { get; private set; }

    public void ResetCounters()
    {
        LastTenantKey = null;
        LastClientId = null;
        LastIfNoneMatch = null;
        CallCount = 0;
    }

    public Task<TenantClientSnapshotResult> GetClientAsync(
        string tenantKey,
        string clientId,
        CancellationToken cancellationToken = default)
        => GetClientAsync(tenantKey, clientId, ifNoneMatch: null, cancellationToken);

    public Task<TenantClientSnapshotResult> GetClientAsync(
        string tenantKey,
        string clientId,
        string? ifNoneMatch,
        CancellationToken cancellationToken = default)
    {
        LastTenantKey = tenantKey;
        LastClientId = clientId;
        LastIfNoneMatch = ifNoneMatch;
        CallCount++;
        var result = _responder(tenantKey, clientId, ifNoneMatch, cancellationToken);
        return Task.FromResult(result);
    }

    public void WhenAnyKey_Returns(TenantClientSnapshotResult result)
        => _responder = (_, _, _, _) => result;

    public void WhenAnyKey_NotFound()
        => _responder = (_, _, _, _) => new TenantClientSnapshotResult(
            null, null, null, null, SdkCacheOutcome.NotFound, null);

    public void WhenAnyKey_Unauthorized()
        => _responder = (_, _, _, _) => new TenantClientSnapshotResult(
            null, null, null, null, SdkCacheOutcome.Unauthorized, null);

    public void WhenAnyKey_RateLimited(TimeSpan retryAfter)
        => _responder = (_, _, _, _) => new TenantClientSnapshotResult(
            null, null, null, null, SdkCacheOutcome.RateLimited, retryAfter);

    public void WhenIfNoneMatch_NotModified(string etag)
        => _responder = (_, _, ifNoneMatch, _) =>
        {
            // If the client passed the same ETag, return 304; otherwise return Hit.
            if (string.Equals(ifNoneMatch, etag, StringComparison.Ordinal))
            {
                return new TenantClientSnapshotResult(
                    null, etag, null, null, SdkCacheOutcome.NotModified, null);
            }
            return new TenantClientSnapshotResult(
                Snapshot: TestSnapshots.Sample("any"),
                Etag: etag,
                LastWriteUtc: null,
                Version: 1,
                Outcome: SdkCacheOutcome.Hit,
                RetryAfter: null);
        };
}

public static class TestSnapshots
{
    public static PublicClientSnapshot Sample(string clientId) => new()
    {
        ClientId = clientId,
        ClientName = "Sample Client",
        Enabled = true,
        ProtocolType = "oidc",
        RedirectUris = new[] { "https://app.example.com/callback" },
        PostLogoutRedirectUris = new[] { "https://app.example.com/logout" },
        AllowedCorsOrigins = new[] { "https://app.example.com" },
        AllowedGrantTypes = new[] { "authorization_code" },
        AllowedScopes = new[] { "openid", "profile" },
        AllowedIdentityTokenSigningAlgorithms = Array.Empty<string>(),
        RequirePkce = true,
        AllowPlainTextPkce = false,
        RequireClientSecret = false,
        RequireConsent = false,
        AllowOfflineAccess = true,
        AllowAccessTokensViaBrowser = false,
        AlwaysIncludeUserClaimsInIdToken = false,
        FrontChannelLogoutUri = null,
        FrontChannelLogoutSessionRequired = false,
        BackChannelLogoutUri = null,
        BackChannelLogoutSessionRequired = false,
        AccessTokenLifetime = 3600,
        IdentityTokenLifetime = 300,
        AuthorizationCodeLifetime = 300,
        AbsoluteRefreshTokenLifetime = 2592000,
        SlidingRefreshTokenLifetime = 1296000,
        RefreshTokenExpiration = 1,
        RefreshTokenUsage = 1,
        UpdateAccessTokenClaimsOnRefresh = false,
        EnableLocalLogin = true,
        RequirePushedAuthorization = false,
        RequireRequestObject = false,
        InitiateLoginUri = "https://app.example.com/initiate",
        UseTenantRedirectPairs = false,
        LastWriteUtc = DateTime.UtcNow,
        Description = null,
        ClientUri = null,
        LogoUri = null
    };
}
