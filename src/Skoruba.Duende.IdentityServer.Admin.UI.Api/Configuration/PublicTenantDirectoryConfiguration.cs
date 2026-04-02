namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;

public sealed class PublicTenantDirectoryConfiguration
{
    public const string SectionName = "PublicTenantDirectory";

    public int ResponseCacheSeconds { get; set; } = 300;

    public int SearchMinLength { get; set; } = 2;

    public int SearchMaxLength { get; set; } = 100;

    public int RateLimitPermitLimit { get; set; } = 30;

    public int RateLimitWindowSeconds { get; set; } = 60;

    public int RateLimitQueueLimit { get; set; } = 0;
}
