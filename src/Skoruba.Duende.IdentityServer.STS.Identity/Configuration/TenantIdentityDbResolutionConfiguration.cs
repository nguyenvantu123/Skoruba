namespace Skoruba.Duende.IdentityServer.STS.Identity.Configuration;

public sealed class TenantIdentityDbResolutionConfiguration
{
    public const string SectionName = "TenantIdentityDbResolution";

    public string CentralBaseUrl { get; set; } = "https://sts.dev.localhost:5001";

    public string TenantIdentityServiceKey { get; set; } = "BlazorApiUser";
}
