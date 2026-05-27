// Feature: tenant-client-cache-public-read — Mobile BFF host (cold-start).
//
// Bootstrap response DTO. Returned by the ANONYMOUS endpoint
// `GET /mobile/bootstrap/{tenantKey}/{clientId}`. Distinct from
// `MobileClientSnapshotResponse` because the cold-start surface must be
// strictly minimal — only the OIDC client metadata a freshly-installed
// Flutter app needs to start the PKCE login flow.
//
// Hard rules:
//   * NO token lifetimes.
//   * NO logout URIs (other than post-logout redirects, which the OIDC
//     client must know up front).
//   * NO secrets / claims / properties — defensive against accidental leak.
//
// Adding fields here is a deliberate API change. Keep the surface tight.

using System.Text.Json.Serialization;

using Skoruba.Duende.IdentityServer.TenantClientCache.Client.Models;

namespace Skoruba.Duende.IdentityServer.Mobile.Bff.Models;

/// <summary>
/// Slim, anonymous-safe projection of a public client snapshot, augmented
/// with the OIDC <c>authority</c> from BFF configuration. Everything in
/// this shape is suitable for an unauthenticated cold-start request.
/// </summary>
/// <param name="Authority">OIDC authority (issuer) URL.</param>
/// <param name="ClientId">OIDC client identifier (echo of validated path).</param>
/// <param name="ClientName">Human-readable client name.</param>
/// <param name="RedirectUris">Allowed redirect URIs.</param>
/// <param name="PostLogoutRedirectUris">Allowed post-logout redirect URIs.</param>
/// <param name="AllowedScopes">Allowed scopes.</param>
/// <param name="AllowedGrantTypes">Allowed OAuth grant types.</param>
/// <param name="RequirePkce">Whether PKCE is required.</param>
public sealed record MobileClientBootstrapResponse(
    [property: JsonPropertyName("authority")] string Authority,
    [property: JsonPropertyName("clientId")] string ClientId,
    [property: JsonPropertyName("clientName")] string? ClientName,
    [property: JsonPropertyName("redirectUris")] IReadOnlyList<string> RedirectUris,
    [property: JsonPropertyName("postLogoutRedirectUris")] IReadOnlyList<string> PostLogoutRedirectUris,
    [property: JsonPropertyName("allowedScopes")] IReadOnlyList<string> AllowedScopes,
    [property: JsonPropertyName("allowedGrantTypes")] IReadOnlyList<string> AllowedGrantTypes,
    [property: JsonPropertyName("requirePkce")] bool RequirePkce)
{
    /// <summary>
    /// Project an upstream <see cref="PublicClientSnapshot"/> plus the
    /// configured authority into the bootstrap shape.
    /// </summary>
    public static MobileClientBootstrapResponse From(PublicClientSnapshot snapshot, string authority)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(authority);
        return new MobileClientBootstrapResponse(
            Authority: authority,
            ClientId: snapshot.ClientId,
            ClientName: snapshot.ClientName,
            RedirectUris: snapshot.RedirectUris,
            PostLogoutRedirectUris: snapshot.PostLogoutRedirectUris,
            AllowedScopes: snapshot.AllowedScopes,
            AllowedGrantTypes: snapshot.AllowedGrantTypes,
            RequirePkce: snapshot.RequirePkce);
    }
}
