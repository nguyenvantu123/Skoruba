namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;

public sealed class ThemePreferenceCacheConfiguration
{
    public const string SectionName = "ThemePreferenceCache";

    public string RedisConnectionString { get; set; } = string.Empty;

    public string InstanceName { get; set; } = "SkorubaAdmin:";

    public int AbsoluteExpirationMinutes { get; set; } = 60;
}
