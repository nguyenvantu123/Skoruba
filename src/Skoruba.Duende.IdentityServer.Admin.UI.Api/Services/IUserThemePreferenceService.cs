using System.Security.Claims;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Dtos.Users;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Services;

public interface IUserThemePreferenceService
{
    Task<ThemePreferenceApiDto?> GetPreferencesAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);

    Task<ThemePreferenceApiDto?> UpdatePreferencesAsync(ClaimsPrincipal principal, ThemePreferenceApiDto request, CancellationToken cancellationToken);
}
