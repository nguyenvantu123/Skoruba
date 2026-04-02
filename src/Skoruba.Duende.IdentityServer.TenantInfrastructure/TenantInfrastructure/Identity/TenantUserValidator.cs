using System.Security;
using TenantInfrastructure.Abstractions;

namespace TenantInfrastructure.Identity;

public interface ITenantUserValidator
{
    void EnsureBranchMatchesTenant(string branchCodeFromOkta);
    void EnsureUserBelongsToTenant(string userTenantKey);
}

public sealed class TenantUserValidator : ITenantUserValidator
{
    private readonly ITenantContextAccessor _accessor;

    public TenantUserValidator(ITenantContextAccessor accessor) => _accessor = accessor;

    public void EnsureBranchMatchesTenant(string branchCodeFromOkta)
    {
        var tenant = _accessor.Current?.TenantKey
            ?? throw new InvalidOperationException("Tenant not resolved");

        if (!string.Equals(branchCodeFromOkta, tenant, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("Branch/Tenant mismatch.");
    }

    public void EnsureUserBelongsToTenant(string userTenantKey)
    {
        var tenant = _accessor.Current?.TenantKey
            ?? throw new InvalidOperationException("Tenant not resolved");

        if (!string.Equals(userTenantKey, tenant, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("User belongs to another tenant.");
    }
}
