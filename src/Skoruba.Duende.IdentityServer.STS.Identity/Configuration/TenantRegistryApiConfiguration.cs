namespace Skoruba.Duende.IdentityServer.STS.Identity.Configuration;

public sealed class TenantRegistryApiConfiguration
{
    public string Authority { get; set; } = string.Empty;

    public bool RequireHttpsMetadata { get; set; } = true;
}
