namespace Skoruba.Duende.IdentityServer.Admin.UI.Services.Configurations;

public class AdminAuthenticationConfiguration
{
    public string Authority { get; set; } = string.Empty;
    
    public bool RequireHttpsMetadata { get; set; } = true;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;
    
    public List<string> AdminScopes { get; set; } = new();

    public bool PersistLogin { get; set; }

    public int PersistentLoginDays { get; set; } = 30;
}
