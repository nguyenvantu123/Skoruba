namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Dtos.Users;

public class ThemePreferenceApiDto
{
    public string Theme { get; set; } = string.Empty;

    public bool IsDarkMode { get; set; }

    public string LastPageVisit { get; set; } = string.Empty;
}
