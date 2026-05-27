// Test-only authentication handler. Reads the synthetic
// `X-Test-TenantKey` header and converts it into a `tenant_key` claim,
// bypassing the real Skoruba STS so the BFF endpoint can be exercised
// in-process. NEVER ship this handler outside the test project.

using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TenantInfrastructure.Identity;

namespace Skoruba.Duende.IdentityServer.Mobile.Bff.IntegrationTests.Infrastructure;

/// <summary>
/// Authentication scheme used in integration tests.
/// </summary>
public static class TestAuthenticationDefaults
{
    public const string Scheme = "TestBearer";
    public const string TenantKeyHeader = "X-Test-TenantKey";
    public const string AuthMarkerHeader = "X-Test-Authenticated";
}

public sealed class TestAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Only authenticate when the test fixture explicitly opts in via
        // X-Test-Authenticated. This keeps the "missing-claim" test case
        // realistic: the user is unauthenticated rather than authenticated
        // with an empty claim.
        if (!Request.Headers.TryGetValue(TestAuthenticationDefaults.AuthMarkerHeader, out var marker)
            || !string.Equals(marker.ToString(), "1", StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>
        {
            new("sub", "test-user")
        };

        if (Request.Headers.TryGetValue(TestAuthenticationDefaults.TenantKeyHeader, out var tenantKeyValues))
        {
            var tenantKey = tenantKeyValues.ToString();
            if (!string.IsNullOrEmpty(tenantKey))
            {
                claims.Add(new Claim(TenantClaimTypes.TenantKey, tenantKey));
            }
        }

        var identity = new ClaimsIdentity(claims, TestAuthenticationDefaults.Scheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, TestAuthenticationDefaults.Scheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
