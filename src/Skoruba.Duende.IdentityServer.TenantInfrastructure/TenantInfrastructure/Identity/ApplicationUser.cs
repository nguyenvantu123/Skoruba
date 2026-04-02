namespace TenantInfrastructure.Identity;

using Microsoft.AspNetCore.Identity;

public class ApplicationUser : IdentityUser
{
    // tenant key theo subdomain, ví dụ tenantA
    public string TenantKey { get; set; } = default!;

    // lấy từ Okta claim branch_code (bạn có thể để trùng TenantKey)
    public string BranchCode { get; set; } = default!;

    // super_admin / branch_admin / staff_admin / user
    public string AdminLevel { get; set; } = "user";
}
