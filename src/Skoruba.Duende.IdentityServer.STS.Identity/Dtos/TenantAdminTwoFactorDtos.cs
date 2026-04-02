using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Dtos;

public sealed class TenantAdminTwoFactorStatusDto
{
    public bool HasAuthenticator { get; set; }
    public bool IsTwoFactorEnabled { get; set; }
    public int RecoveryCodesLeft { get; set; }
}

public sealed class TenantAdminTwoFactorSetupDto
{
    public string SharedKey { get; set; } = string.Empty;
    public string AuthenticatorUri { get; set; } = string.Empty;
}

public sealed class TenantAdminVerifyTwoFactorDto
{
    [Required]
    public string Code { get; set; } = string.Empty;
}

public sealed class TenantAdminTwoFactorOperationResultDto
{
    public bool Success { get; set; }
    public TenantAdminTwoFactorStatusDto? Status { get; set; }
    public TenantAdminTwoFactorSetupDto? Setup { get; set; }
    public List<string> RecoveryCodes { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}