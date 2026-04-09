using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Duende.IdentityServer;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;
using IdentityModel;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.Entities.Identity;
using TenantInfrastructure.Abstractions;
using TenantInfrastructure.Identity;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Services;

public sealed class TenantProfileService : IProfileService
{
    private readonly UserManager<UserIdentity> _userManager;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITenantStore _tenantStore;
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly ILogger<TenantProfileService> _logger;

    public TenantProfileService(
        UserManager<UserIdentity> userManager,
        IServiceScopeFactory scopeFactory,
        ITenantStore tenantStore,
        ITenantContextAccessor tenantContextAccessor,
        ILogger<TenantProfileService> logger)
    {
        _userManager = userManager;
        _scopeFactory = scopeFactory;
        _tenantStore = tenantStore;
        _tenantContextAccessor = tenantContextAccessor;
        _logger = logger;
    }

    public async Task GetProfileDataAsync(ProfileDataRequestContext context)
    {
        var resolution = await ResolveUserAsync(context.Subject);
        var user = resolution.User;
        if (user == null)
        {
            _logger.LogWarning(
                "Profile data request could not resolve a local user. Subject={Subject}, NameIdentifier={NameIdentifier}, IdentitySub={IdentitySub}, TenantClaim={TenantClaim}, Caller={Caller}",
                context.Subject?.Identity?.Name ?? "<none>",
                context.Subject?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "<none>",
                context.Subject?.FindFirst(JwtClaimTypes.Subject)?.Value ?? "<none>",
                context.Subject?.FindFirst(TenantClaimTypes.TenantKey)?.Value ?? "<none>",
                context.Caller ?? "<none>");
            return;
        }

        _logger.LogInformation(
            "Profile data request resolved local user. UserId={UserId}, UserName={UserName}, TenantKey={TenantKey}, ResolutionSource={ResolutionSource}, Caller={Caller}",
            user.Id,
            user.UserName ?? "<none>",
            user.TenantKey ?? "<none>",
            resolution.Source,
            context.Caller ?? "<none>");

        var displayName = user.UserName ?? user.Email;
        if (!string.IsNullOrWhiteSpace(displayName) &&
            ShouldIssueClaim(context, JwtClaimTypes.Name, displayName))
        {
            context.IssuedClaims.Add(new Claim(JwtClaimTypes.Name, displayName));
        }

        if (!string.IsNullOrWhiteSpace(user.Email) &&
            ShouldIssueClaim(context, JwtClaimTypes.Email, user.Email))
        {
            context.IssuedClaims.Add(new Claim(JwtClaimTypes.Email, user.Email));
        }

        var userRoles = await ExecuteAgainstResolvedUserStoreAsync(
            resolution,
            user.Id,
            (manager, resolvedUser) => manager.GetRolesAsync(resolvedUser));
        foreach (var role in userRoles)
        {
            if (ShouldIssueClaim(context, JwtClaimTypes.Role, role, alwaysIssueForAccessToken: true))
            {
                context.IssuedClaims.Add(new Claim(JwtClaimTypes.Role, role));
            }

            if (ShouldIssueClaim(context, ClaimTypes.Role, role, alwaysIssueForAccessToken: true))
            {
                context.IssuedClaims.Add(new Claim(ClaimTypes.Role, role));
            }
        }

        if (!string.IsNullOrWhiteSpace(user.TenantKey) &&
            ShouldIssueClaim(context, TenantClaimTypes.TenantKey, user.TenantKey, alwaysIssueForAccessToken: true))
        {
            context.IssuedClaims.Add(new Claim(TenantClaimTypes.TenantKey, user.TenantKey));
        }

        var firstTimeLogin = await ExecuteAgainstResolvedUserStoreAsync(
            resolution,
            user.Id,
            (manager, resolvedUser) => FirstTimeLoginClaimHelper.GetFirstTimeLoginAsync(manager, resolvedUser));
        var firstTimeLoginValue = FirstTimeLoginClaimHelper.ToClaimValue(firstTimeLogin);
        if (ShouldIssueClaim(context, TenantClaimTypes.FirstTimeLogin, firstTimeLoginValue, alwaysIssueForUserInfo: true))
        {
            context.IssuedClaims.Add(new Claim(TenantClaimTypes.FirstTimeLogin, firstTimeLoginValue, ClaimValueTypes.Boolean));
        }
    }

    public async Task IsActiveAsync(IsActiveContext context)
    {
        var resolution = await ResolveUserAsync(context.Subject);
        var user = resolution.User;
        context.IsActive = user != null;

        if (context.IsActive)
        {
            _logger.LogInformation(
                "IsActive resolved local user. UserId={UserId}, UserName={UserName}, TenantKey={TenantKey}, ResolutionSource={ResolutionSource}",
                user!.Id,
                user.UserName ?? "<none>",
                user.TenantKey ?? "<none>",
                resolution.Source);
            return;
        }

        _logger.LogWarning(
            "IsActive could not resolve a local user from the current subject. Subject={Subject}, NameIdentifier={NameIdentifier}, IdentitySub={IdentitySub}, TenantClaim={TenantClaim}",
            context.Subject?.Identity?.Name ?? "<none>",
            context.Subject?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "<none>",
            context.Subject?.FindFirst(JwtClaimTypes.Subject)?.Value ?? "<none>",
            context.Subject?.FindFirst(TenantClaimTypes.TenantKey)?.Value ?? "<none>");
    }

    private async Task<(UserIdentity? User, string Source)> ResolveUserAsync(ClaimsPrincipal subject)
    {
        if (subject == null)
        {
            return (null, "subject:null");
        }

        var directResolution = await ResolveUserAsync(_userManager, subject);
        if (directResolution.User != null)
        {
            return directResolution;
        }

        var tenantKey = subject.FindFirst(TenantClaimTypes.TenantKey)?.Value?.Trim();
        var currentTenantKey = _tenantContextAccessor.Current?.TenantKey?.Trim();
        if (string.IsNullOrWhiteSpace(tenantKey) ||
            string.Equals(currentTenantKey, tenantKey, System.StringComparison.OrdinalIgnoreCase))
        {
            return directResolution;
        }

        var tenant = await _tenantStore.FindAsync(tenantKey, CancellationToken.None);
        if (tenant == null || !tenant.IsActive)
        {
            _logger.LogWarning(
                "Tenant-scoped user resolution fallback could not load tenant metadata. RequestedTenantKey={RequestedTenantKey}, CurrentTenantKey={CurrentTenantKey}, Subject={Subject}",
                tenantKey,
                currentTenantKey ?? "<none>",
                subject.Identity?.Name ?? "<none>");
            return directResolution;
        }

        var previousTenantContext = _tenantContextAccessor.Current;
        try
        {
            _tenantContextAccessor.Set(new TenantContext(
                tenant.TenantKey,
                tenant.ConnectionSecrets));

            using var scope = _scopeFactory.CreateScope();
            var scopedUserManager = scope.ServiceProvider.GetRequiredService<UserManager<UserIdentity>>();
            var tenantResolution = await ResolveUserAsync(scopedUserManager, subject);
            if (tenantResolution.User != null)
            {
                _logger.LogInformation(
                    "Resolved local user through tenant-scoped fallback. RequestedTenantKey={RequestedTenantKey}, OriginalTenantKey={OriginalTenantKey}, ResolutionSource={ResolutionSource}, UserId={UserId}, UserName={UserName}",
                    tenant.TenantKey,
                    currentTenantKey ?? "<none>",
                    tenantResolution.Source,
                    tenantResolution.User.Id,
                    tenantResolution.User.UserName ?? "<none>");
                return (tenantResolution.User, $"tenant-fallback:{tenantResolution.Source}");
            }

            _logger.LogWarning(
                "Tenant-scoped user resolution fallback did not find a local user. RequestedTenantKey={RequestedTenantKey}, OriginalTenantKey={OriginalTenantKey}, Subject={Subject}, NameIdentifier={NameIdentifier}, IdentitySub={IdentitySub}",
                tenant.TenantKey,
                currentTenantKey ?? "<none>",
                subject.Identity?.Name ?? "<none>",
                subject.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "<none>",
                subject.FindFirst(JwtClaimTypes.Subject)?.Value ?? "<none>");
        }
        finally
        {
            if (previousTenantContext == null)
            {
                _tenantContextAccessor.Clear();
            }
            else
            {
                _tenantContextAccessor.Set(previousTenantContext);
            }
        }

        return directResolution;
    }

    private async Task<TResult> ExecuteAgainstResolvedUserStoreAsync<TResult>(
        (UserIdentity? User, string Source) resolution,
        string userId,
        Func<UserManager<UserIdentity>, UserIdentity, Task<TResult>> action)
    {
        if (resolution.User == null)
        {
            throw new InvalidOperationException("A resolved user is required to execute an operation against the user store.");
        }

        if (!resolution.Source.StartsWith("tenant-fallback:", System.StringComparison.OrdinalIgnoreCase))
        {
            return await action(_userManager, resolution.User);
        }

        var tenantKey = resolution.User.TenantKey?.Trim();
        if (string.IsNullOrWhiteSpace(tenantKey))
        {
            _logger.LogWarning(
                "Tenant-scoped user store operation fell back to the ambient user manager because the resolved user did not expose a tenant key. UserId={UserId}, ResolutionSource={ResolutionSource}",
                userId,
                resolution.Source);
            return await action(_userManager, resolution.User);
        }

        var tenant = await _tenantStore.FindAsync(tenantKey, CancellationToken.None);
        if (tenant == null || !tenant.IsActive)
        {
            _logger.LogWarning(
                "Tenant-scoped user store operation fell back to the ambient user manager because tenant metadata could not be loaded. UserId={UserId}, TenantKey={TenantKey}, ResolutionSource={ResolutionSource}",
                userId,
                tenantKey,
                resolution.Source);
            return await action(_userManager, resolution.User);
        }

        var previousTenantContext = _tenantContextAccessor.Current;
        try
        {
            _tenantContextAccessor.Set(new TenantContext(
                tenant.TenantKey,
                tenant.ConnectionSecrets));

            using var scope = _scopeFactory.CreateScope();
            var scopedUserManager = scope.ServiceProvider.GetRequiredService<UserManager<UserIdentity>>();
            var scopedUser = await scopedUserManager.FindByIdAsync(userId);
            if (scopedUser == null)
            {
                _logger.LogWarning(
                    "Tenant-scoped user store operation could not reload the user inside the tenant scope. UserId={UserId}, TenantKey={TenantKey}, ResolutionSource={ResolutionSource}",
                    userId,
                    tenant.TenantKey,
                    resolution.Source);
                return await action(_userManager, resolution.User);
            }

            return await action(scopedUserManager, scopedUser);
        }
        finally
        {
            if (previousTenantContext == null)
            {
                _tenantContextAccessor.Clear();
            }
            else
            {
                _tenantContextAccessor.Set(previousTenantContext);
            }
        }
    }

    private static async Task<(UserIdentity? User, string Source)> ResolveUserAsync(
        UserManager<UserIdentity> userManager,
        ClaimsPrincipal subject)
    {
        var userFromPrincipal = await userManager.GetUserAsync(subject);
        if (userFromPrincipal != null)
        {
            return (userFromPrincipal, "principal");
        }

        var candidateIds = new[]
            {
                ("user-manager-id", userManager.GetUserId(subject)),
                ("name-identifier", subject.FindFirst(ClaimTypes.NameIdentifier)?.Value),
                ("identity-sub", subject.FindFirst(JwtClaimTypes.Subject)?.Value)
            }
            .Where(x => !string.IsNullOrWhiteSpace(x.Item2))
            .Distinct();

        foreach (var (source, candidateId) in candidateIds)
        {
            var user = await userManager.FindByIdAsync(candidateId!);
            if (user != null)
            {
                return (user, source);
            }
        }

        return (null, "unresolved");
    }

    private static bool ShouldIssueClaim(
        ProfileDataRequestContext context,
        string claimType,
        string claimValue,
        bool alwaysIssueForAccessToken = false,
        bool alwaysIssueForUserInfo = false)
    {
        if (alwaysIssueForAccessToken && context.Caller.IndexOf("AccessToken", System.StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return !context.IssuedClaims.Any(c => c.Type == claimType && c.Value == claimValue);
        }

        if (alwaysIssueForUserInfo && context.Caller.IndexOf("UserInfo", System.StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return !context.IssuedClaims.Any(c => c.Type == claimType && c.Value == claimValue);
        }

        var requestedClaims = context.RequestedClaimTypes;
        var isRequested = requestedClaims == null || !requestedClaims.Any() || requestedClaims.Contains(claimType);
        if (!isRequested)
        {
            return false;
        }

        return !context.IssuedClaims.Any(c => c.Type == claimType && c.Value == claimValue);
    }
}
