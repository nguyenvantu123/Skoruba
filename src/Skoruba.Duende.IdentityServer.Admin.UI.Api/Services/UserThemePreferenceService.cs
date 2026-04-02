using System.Security.Claims;
using System.Text.Json;
using IdentityModel;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.Entities.Identity;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Dtos.Users;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Services;

public sealed class UserThemePreferenceService : IUserThemePreferenceService
{
    private const string ThemePreferenceClaimType = "theme_preference";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> AllowedThemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "light",
        "dark",
        "system"
    };

    private readonly UserManager<UserIdentity> _userManager;
    private readonly IDistributedCache _distributedCache;
    private readonly ThemePreferenceCacheConfiguration _cacheConfiguration;
    private readonly ILogger<UserThemePreferenceService> _logger;

    public UserThemePreferenceService(
        UserManager<UserIdentity> userManager,
        IDistributedCache distributedCache,
        IOptions<ThemePreferenceCacheConfiguration> cacheConfiguration,
        ILogger<UserThemePreferenceService> logger)
    {
        _userManager = userManager;
        _distributedCache = distributedCache;
        _cacheConfiguration = cacheConfiguration.Value;
        _logger = logger;
    }

    public async Task<ThemePreferenceApiDto?> GetPreferencesAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(principal);
        if (user == null)
        {
            return null;
        }

        var cacheKey = BuildCacheKey(user.Id);
        try
        {
            var cachedPayload = await _distributedCache.GetStringAsync(cacheKey, cancellationToken);
            if (!string.IsNullOrWhiteSpace(cachedPayload))
            {
                if (TryDeserializePreferences(cachedPayload, out var cachedPreferences))
                {
                    return cachedPreferences;
                }

                return CreateDto(NormalizeTheme(cachedPayload), string.Empty);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read user preferences cache for key {CacheKey}. Falling back to user claims.", cacheKey);
        }

        var claims = await _userManager.GetClaimsAsync(user);
        var resolvedTheme = NormalizeTheme(claims.FirstOrDefault(x => x.Type == ThemePreferenceClaimType)?.Value);
        var preferences = CreateDto(resolvedTheme, string.Empty);

        await SetPreferencesInCacheAsync(cacheKey, preferences, cancellationToken);

        return preferences;
    }

    public async Task<ThemePreferenceApiDto?> UpdatePreferencesAsync(ClaimsPrincipal principal, ThemePreferenceApiDto request, CancellationToken cancellationToken)
    {
        var normalizedTheme = NormalizeTheme(!string.IsNullOrWhiteSpace(request.Theme)
            ? request.Theme
            : request.IsDarkMode ? "dark" : "light");
        if (!AllowedThemes.Contains(normalizedTheme))
        {
            throw new ArgumentException("Theme must be one of: light, dark, system.", nameof(request));
        }

        var user = await GetCurrentUserAsync(principal);
        if (user == null)
        {
            return null;
        }

        var claims = await _userManager.GetClaimsAsync(user);
        var existingThemeClaim = claims.FirstOrDefault(x => x.Type == ThemePreferenceClaimType);

        IdentityResult result;
        if (existingThemeClaim == null)
        {
            result = await _userManager.AddClaimAsync(user, new Claim(ThemePreferenceClaimType, normalizedTheme));
        }
        else if (!string.Equals(existingThemeClaim.Value, normalizedTheme, StringComparison.Ordinal))
        {
            result = await _userManager.ReplaceClaimAsync(user, existingThemeClaim, new Claim(ThemePreferenceClaimType, normalizedTheme));
        }
        else
        {
            result = IdentityResult.Success;
        }

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(x => x.Description)));
        }

        var preferences = CreateDto(normalizedTheme, request.LastPageVisit);
        await SetPreferencesInCacheAsync(BuildCacheKey(user.Id), preferences, cancellationToken);

        return preferences;
    }

    private async Task<UserIdentity?> GetCurrentUserAsync(ClaimsPrincipal principal)
    {
        var userId = principal.FindFirstValue(JwtClaimTypes.Subject) ??
                     principal.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        return await _userManager.FindByIdAsync(userId);
    }

    private async Task SetPreferencesInCacheAsync(string cacheKey, ThemePreferenceApiDto preferences, CancellationToken cancellationToken)
    {
        try
        {
            await _distributedCache.SetStringAsync(
                cacheKey,
                JsonSerializer.Serialize(preferences, JsonOptions),
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(Math.Max(1, _cacheConfiguration.AbsoluteExpirationMinutes))
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write user preferences cache for key {CacheKey}.", cacheKey);
        }
    }

    private string BuildCacheKey(string userId)
    {
        return $"{_cacheConfiguration.InstanceName}user-preferences:{userId}";
    }

    private static ThemePreferenceApiDto CreateDto(string normalizedTheme, string? lastPageVisit)
    {
        return new ThemePreferenceApiDto
        {
            Theme = normalizedTheme,
            IsDarkMode = string.Equals(normalizedTheme, "dark", StringComparison.OrdinalIgnoreCase),
            LastPageVisit = lastPageVisit?.Trim() ?? string.Empty
        };
    }

    private static bool TryDeserializePreferences(string cachedPayload, out ThemePreferenceApiDto preferences)
    {
        preferences = default!;

        try
        {
            var deserialized = JsonSerializer.Deserialize<ThemePreferenceApiDto>(cachedPayload, JsonOptions);
            if (deserialized == null)
            {
                return false;
            }

            preferences = CreateDto(
                NormalizeTheme(!string.IsNullOrWhiteSpace(deserialized.Theme)
                    ? deserialized.Theme
                    : deserialized.IsDarkMode ? "dark" : "light"),
                deserialized.LastPageVisit);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string NormalizeTheme(string? theme)
    {
        return string.IsNullOrWhiteSpace(theme) ? "system" : theme.Trim().ToLowerInvariant();
    }
}

