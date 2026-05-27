// Feature: tenant-client-cache-public-read — Mobile BFF host.
//
// Slim response DTO. The BFF curates the 38-field PublicClientSnapshot down to
// the 10 fields a typical mobile OAuth client needs. Adding new fields here is
// a deliberate API change — keep it tight to limit the data surface a Flutter
// binary handles. Forbidden upstream fields (e.g. clientSecrets) cannot leak
// through this shape.

using System.Text.Json.Serialization;

using Skoruba.Duende.IdentityServer.TenantClientCache.Client.Models;

namespace Skoruba.Duende.IdentityServer.Mobile.Bff.Models;

/// <summary>
/// Mobile-friendly projection of <see cref="PublicClientSnapshot"/>.
/// Only the 10 fields a typical mobile OAuth client cares about are surfaced.
/// </summary>
/// <param name="ClientId">OIDC client identifier.</param>
/// <param name="ClientName">Human-readable client name.</param>
/// <param name="Enabled">Whether the client is currently enabled.</param>
/// <param name="RedirectUris">Allowed redirect URIs.</param>
/// <param name="PostLogoutRedirectUris">Allowed post-logout redirect URIs.</param>
/// <param name="AllowedScopes">Allowed scopes.</param>
/// <param name="AllowedGrantTypes">Allowed OAuth grant types.</param>
/// <param name="RequirePkce">Whether PKCE is required.</param>
/// <param name="InitiateLoginUri">Optional initiate-login URI.</param>
/// <param name="AccessTokenLifetime">Access-token lifetime in seconds.</param>
/// <param name="IdentityTokenLifetime">Identity-token lifetime in seconds.</param>
public sealed record MobileClientSnapshotResponse(
    [property: JsonPropertyName("clientId")] string ClientId,
    [property: JsonPropertyName("clientName")] string? ClientName,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("redirectUris")] IReadOnlyList<string> RedirectUris,
    [property: JsonPropertyName("postLogoutRedirectUris")] IReadOnlyList<string> PostLogoutRedirectUris,
    [property: JsonPropertyName("allowedScopes")] IReadOnlyList<string> AllowedScopes,
    [property: JsonPropertyName("allowedGrantTypes")] IReadOnlyList<string> AllowedGrantTypes,
    [property: JsonPropertyName("requirePkce")] bool RequirePkce,
    [property: JsonPropertyName("initiateLoginUri")] string? InitiateLoginUri,
    [property: JsonPropertyName("accessTokenLifetime")] int AccessTokenLifetime,
    [property: JsonPropertyName("identityTokenLifetime")] int IdentityTokenLifetime)
{
    /// <summary>
    /// Projects a full <see cref="PublicClientSnapshot"/> to the slim mobile shape.
    /// </summary>
    public static MobileClientSnapshotResponse From(PublicClientSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new MobileClientSnapshotResponse(
            ClientId: snapshot.ClientId,
            ClientName: snapshot.ClientName,
            Enabled: snapshot.Enabled,
            RedirectUris: snapshot.RedirectUris,
            PostLogoutRedirectUris: snapshot.PostLogoutRedirectUris,
            AllowedScopes: snapshot.AllowedScopes,
            AllowedGrantTypes: snapshot.AllowedGrantTypes,
            RequirePkce: snapshot.RequirePkce,
            InitiateLoginUri: snapshot.InitiateLoginUri,
            AccessTokenLifetime: snapshot.AccessTokenLifetime,
            IdentityTokenLifetime: snapshot.IdentityTokenLifetime);
    }
}
