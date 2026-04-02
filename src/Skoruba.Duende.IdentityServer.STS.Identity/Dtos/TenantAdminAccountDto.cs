using System;
using System.Collections.Generic;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Dtos;

public sealed class TenantAdminAccountDto
{
    public Guid Id { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string FirstName { get; set; } = string.Empty;

    public string LastName { get; set; } = string.Empty;

    public string PhoneNumber { get; set; } = string.Empty;

    public List<string> Roles { get; set; } = new();

    public bool HasPassword { get; set; }

    public string AccountType { get; set; } = string.Empty;

    public string AuthSource { get; set; } = string.Empty;

    public string ExternalIdentityId { get; set; } = string.Empty;
}
