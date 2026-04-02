using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Dtos;

public sealed class TenantAdminChangePasswordDto
{
    [Required]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required]
    public string NewPassword { get; set; } = string.Empty;
}

public sealed class TenantAdminChangePasswordResultDto
{
    public bool Success { get; set; }

    public List<string> Errors { get; set; } = new();
}