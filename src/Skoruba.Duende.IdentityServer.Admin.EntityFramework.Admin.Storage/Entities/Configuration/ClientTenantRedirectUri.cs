using Duende.IdentityServer.EntityFramework.Entities;

namespace Skoruba.Duende.IdentityServer.Admin.EntityFramework.Admin.Storage.Entities.Configuration;

public class ClientTenantRedirectUri
{
    public int Id { get; set; }

    public int ClientId { get; set; }

    public string TenantKey { get; set; }

    public string SignInCallbackUrl { get; set; }

    public string SignOutCallbackUrl { get; set; }

    public string CorsOrigin { get; set; }

    public Client Client { get; set; }
}
