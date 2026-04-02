using IdentityModel;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.Entities.Identity;
using Skoruba.Duende.IdentityServer.STS.Identity.Dtos;
using Skoruba.Duende.IdentityServer.STS.Identity.Helpers;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using TenantInfrastructure.Identity;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Services;

public interface ITenantAdminAccountService
{
    Task<TenantAdminAccountDto?> GetCurrentAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);
    Task<TenantAdminPersonalDataDto?> GetPersonalDataAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);
    Task<TenantAdminChangePasswordResultDto?> ChangePasswordAsync(ClaimsPrincipal principal, TenantAdminChangePasswordDto request, CancellationToken cancellationToken);
    Task<TenantAdminTwoFactorStatusDto?> GetTwoFactorStatusAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);
    Task<TenantAdminTwoFactorSetupDto?> GetTwoFactorSetupAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);
    Task<TenantAdminTwoFactorOperationResultDto?> EnableTwoFactorAsync(ClaimsPrincipal principal, TenantAdminVerifyTwoFactorDto request, CancellationToken cancellationToken);
    Task<TenantAdminTwoFactorOperationResultDto?> DisableTwoFactorAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);
    Task<TenantAdminTwoFactorOperationResultDto?> ResetAuthenticatorAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);
    Task<TenantAdminTwoFactorOperationResultDto?> GenerateRecoveryCodesAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);
}

public sealed class TenantAdminAccountService : ITenantAdminAccountService
{
    private const string AuthenticatorIssuer = "Skoruba.Duende.IdentityServer.STS.Identity";
    private const string AuthenticatorUriFormat = "otpauth://totp/{0}:{1}?secret={2}&issuer={0}&digits=6";

    private readonly UserManager<UserIdentity> _userManager;
    private readonly UrlEncoder _urlEncoder;
    private readonly ILogger<TenantAdminAccountService> _logger;

    public TenantAdminAccountService(UserManager<UserIdentity> userManager, UrlEncoder urlEncoder, ILogger<TenantAdminAccountService> logger)
    {
        _userManager = userManager;
        _urlEncoder = urlEncoder;
        _logger = logger;
    }

    public async Task<TenantAdminAccountDto?> GetCurrentAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var context = GetUserContext(principal);
        if (context == null)
        {
            return null;
        }

        var user = await GetValidatedUserAsync(context);
        if (user == null)
        {
            return null;
        }

        var roles = await _userManager.GetRolesAsync(user);
        var claims = await _userManager.GetClaimsAsync(user);
        var firstName = claims.FirstOrDefault(x => x.Type == JwtClaimTypes.GivenName)?.Value ?? string.Empty;
        var lastName = claims.FirstOrDefault(x => x.Type == JwtClaimTypes.FamilyName)?.Value ?? string.Empty;
        var hasPassword = await _userManager.HasPasswordAsync(user);
        var userId = Guid.TryParse(user.Id, out var parsedUserId) ? parsedUserId : Guid.Empty;

        return new TenantAdminAccountDto
        {
            Id = userId,
            UserName = user.UserName ?? string.Empty,
            TenantId = context.TenantKey,
            Email = user.Email ?? string.Empty,
            FirstName = firstName,
            LastName = lastName,
            PhoneNumber = user.PhoneNumber ?? string.Empty,
            Roles = roles.ToList(),
            HasPassword = hasPassword,
            AccountType = "TenantAdmin",
            AuthSource = "CentralIdentity",
            ExternalIdentityId = user.Id
        };
    }

    public async Task<TenantAdminPersonalDataDto?> GetPersonalDataAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var context = GetUserContext(principal);
        if (context == null)
        {
            return null;
        }

        var user = await GetValidatedUserAsync(context);
        if (user == null)
        {
            return null;
        }

        var claims = await _userManager.GetClaimsAsync(user);
        var profile = OpenIdClaimHelpers.ExtractProfileInfo(claims);
        var personalData = typeof(UserIdentity)
            .GetProperties()
            .Where(prop => Attribute.IsDefined(prop, typeof(PersonalDataAttribute)))
            .ToDictionary(
                p => p.Name,
                p => p.GetValue(user)?.ToString() ?? "null",
                StringComparer.OrdinalIgnoreCase);

        return new TenantAdminPersonalDataDto
        {
            UserId = user.Id,
            TenantKey = context.TenantKey,
            UserName = user.UserName ?? string.Empty,
            Email = user.Email ?? string.Empty,
            EmailConfirmed = user.EmailConfirmed,
            PhoneNumber = user.PhoneNumber ?? string.Empty,
            FullName = profile.FullName ?? string.Empty,
            Website = profile.Website ?? string.Empty,
            Profile = profile.Profile ?? string.Empty,
            StreetAddress = profile.StreetAddress ?? string.Empty,
            Locality = profile.Locality ?? string.Empty,
            Region = profile.Region ?? string.Empty,
            PostalCode = profile.PostalCode ?? string.Empty,
            Country = profile.Country ?? string.Empty,
            PersonalData = personalData
        };
    }

    public async Task<TenantAdminChangePasswordResultDto?> ChangePasswordAsync(ClaimsPrincipal principal, TenantAdminChangePasswordDto request, CancellationToken cancellationToken)
    {
        var context = GetUserContext(principal);
        if (context == null)
        {
            return null;
        }

        if (request == null)
        {
            return new TenantAdminChangePasswordResultDto
            {
                Success = false,
                Errors = { "Request body is required." }
            };
        }

        var user = await GetValidatedUserAsync(context);
        if (user == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return new TenantAdminChangePasswordResultDto
            {
                Success = false,
                Errors = { "CurrentPassword and NewPassword are required." }
            };
        }

        var result = await _userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (result.Succeeded)
        {
            var claimResult = await FirstTimeLoginClaimHelper.SetFirstTimeLoginAsync(_userManager, user, false);
            if (!claimResult.Succeeded)
            {
                _logger.LogWarning(
                    "Unable to set firstTimeLogin=false for tenant admin '{UserId}'. Errors={Errors}",
                    user.Id,
                    string.Join("; ", claimResult.Errors.Select(x => x.Description)));
            }

            return new TenantAdminChangePasswordResultDto { Success = true };
        }

        return new TenantAdminChangePasswordResultDto
        {
            Success = false,
            Errors = result.Errors.Select(error => error.Description).ToList()
        };
    }

    public async Task<TenantAdminTwoFactorStatusDto?> GetTwoFactorStatusAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var context = GetUserContext(principal);
        if (context == null)
        {
            return null;
        }

        var user = await GetValidatedUserAsync(context);
        return user == null ? null : await BuildTwoFactorStatusAsync(user);
    }

    public async Task<TenantAdminTwoFactorSetupDto?> GetTwoFactorSetupAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var context = GetUserContext(principal);
        if (context == null)
        {
            return null;
        }

        var user = await GetValidatedUserAsync(context);
        return user == null ? null : await BuildTwoFactorSetupAsync(user);
    }

    public async Task<TenantAdminTwoFactorOperationResultDto?> EnableTwoFactorAsync(ClaimsPrincipal principal, TenantAdminVerifyTwoFactorDto request, CancellationToken cancellationToken)
    {
        var context = GetUserContext(principal);
        if (context == null)
        {
            return null;
        }

        var user = await GetValidatedUserAsync(context);
        if (user == null)
        {
            return null;
        }

        if (request == null || string.IsNullOrWhiteSpace(request.Code))
        {
            return Failure("Code is required.");
        }

        var verificationCode = request.Code.Replace(" ", string.Empty).Replace("-", string.Empty);
        var isValid = await _userManager.VerifyTwoFactorTokenAsync(user, _userManager.Options.Tokens.AuthenticatorTokenProvider, verificationCode);
        if (!isValid)
        {
            return Failure("Invalid verification code.");
        }

        var enableResult = await _userManager.SetTwoFactorEnabledAsync(user, true);
        if (!enableResult.Succeeded)
        {
            return Failure(enableResult.Errors.Select(error => error.Description).ToArray());
        }

        var response = new TenantAdminTwoFactorOperationResultDto
        {
            Success = true,
            Status = await BuildTwoFactorStatusAsync(user)
        };

        if (await _userManager.CountRecoveryCodesAsync(user) == 0)
        {
            var recoveryCodes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
            response.RecoveryCodes.AddRange(recoveryCodes);
            response.Status = await BuildTwoFactorStatusAsync(user);
        }

        return response;
    }

    public async Task<TenantAdminTwoFactorOperationResultDto?> DisableTwoFactorAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var context = GetUserContext(principal);
        if (context == null)
        {
            return null;
        }

        var user = await GetValidatedUserAsync(context);
        if (user == null)
        {
            return null;
        }

        var result = await _userManager.SetTwoFactorEnabledAsync(user, false);
        if (!result.Succeeded)
        {
            return Failure(result.Errors.Select(error => error.Description).ToArray());
        }

        return new TenantAdminTwoFactorOperationResultDto
        {
            Success = true,
            Status = await BuildTwoFactorStatusAsync(user)
        };
    }

    public async Task<TenantAdminTwoFactorOperationResultDto?> ResetAuthenticatorAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var context = GetUserContext(principal);
        if (context == null)
        {
            return null;
        }

        var user = await GetValidatedUserAsync(context);
        if (user == null)
        {
            return null;
        }

        await _userManager.SetTwoFactorEnabledAsync(user, false);
        await _userManager.ResetAuthenticatorKeyAsync(user);

        return new TenantAdminTwoFactorOperationResultDto
        {
            Success = true,
            Status = await BuildTwoFactorStatusAsync(user),
            Setup = await BuildTwoFactorSetupAsync(user)
        };
    }

    public async Task<TenantAdminTwoFactorOperationResultDto?> GenerateRecoveryCodesAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var context = GetUserContext(principal);
        if (context == null)
        {
            return null;
        }

        var user = await GetValidatedUserAsync(context);
        if (user == null)
        {
            return null;
        }

        if (!user.TwoFactorEnabled)
        {
            return Failure("Cannot generate recovery codes while two-factor authentication is disabled.");
        }

        var recoveryCodes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        return new TenantAdminTwoFactorOperationResultDto
        {
            Success = true,
            Status = await BuildTwoFactorStatusAsync(user),
            RecoveryCodes = recoveryCodes.ToList()
        };
    }

    private async Task<UserIdentity?> GetValidatedUserAsync(UserContext context)
    {
        var user = await _userManager.FindByIdAsync(context.UserId);
        return IsValidTenantAdminUser(user, context.TenantKey) ? user : null;
    }

    private async Task<TenantAdminTwoFactorStatusDto> BuildTwoFactorStatusAsync(UserIdentity user)
    {
        return new TenantAdminTwoFactorStatusDto
        {
            HasAuthenticator = !string.IsNullOrWhiteSpace(await _userManager.GetAuthenticatorKeyAsync(user)),
            IsTwoFactorEnabled = user.TwoFactorEnabled,
            RecoveryCodesLeft = await _userManager.CountRecoveryCodesAsync(user)
        };
    }

    private async Task<TenantAdminTwoFactorSetupDto> BuildTwoFactorSetupAsync(UserIdentity user)
    {
        var unformattedKey = await _userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrWhiteSpace(unformattedKey))
        {
            await _userManager.ResetAuthenticatorKeyAsync(user);
            unformattedKey = await _userManager.GetAuthenticatorKeyAsync(user);
        }

        return new TenantAdminTwoFactorSetupDto
        {
            SharedKey = FormatKey(unformattedKey ?? string.Empty),
            AuthenticatorUri = GenerateQrCodeUri(user.Email ?? user.UserName ?? string.Empty, unformattedKey ?? string.Empty)
        };
    }

    private string GenerateQrCodeUri(string email, string unformattedKey)
    {
        return string.Format(
            AuthenticatorUriFormat,
            _urlEncoder.Encode(AuthenticatorIssuer),
            _urlEncoder.Encode(email),
            unformattedKey);
    }

    private static string FormatKey(string unformattedKey)
    {
        var result = new StringBuilder();
        var currentPosition = 0;

        while (currentPosition + 4 < unformattedKey.Length)
        {
            result.Append(unformattedKey.Substring(currentPosition, 4)).Append(' ');
            currentPosition += 4;
        }

        if (currentPosition < unformattedKey.Length)
        {
            result.Append(unformattedKey.Substring(currentPosition));
        }

        return result.ToString().ToLowerInvariant();
    }

    private static TenantAdminTwoFactorOperationResultDto Failure(params string[] errors)
    {
        return new TenantAdminTwoFactorOperationResultDto
        {
            Success = false,
            Errors = errors.Where(error => !string.IsNullOrWhiteSpace(error)).ToList()
        };
    }

    private static bool IsValidTenantAdminUser(UserIdentity? user, string tenantKey)
    {
        return user != null && !string.IsNullOrWhiteSpace(tenantKey) && string.Equals(user.TenantKey?.Trim(), tenantKey, StringComparison.OrdinalIgnoreCase);
    }

    private static UserContext? GetUserContext(ClaimsPrincipal principal)
    {
        var userId = principal.FindFirstValue(JwtClaimTypes.Subject) ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var tenantKey = principal.FindFirstValue(TenantClaimTypes.TenantKey);
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(tenantKey))
        {
            return null;
        }

        return new UserContext(userId.Trim(), tenantKey.Trim());
    }

    private sealed record UserContext(string UserId, string TenantKey);
}
