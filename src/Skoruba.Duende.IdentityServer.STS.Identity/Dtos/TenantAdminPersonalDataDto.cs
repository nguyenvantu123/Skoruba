using System;
using System.Collections.Generic;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Dtos;

public sealed class TenantAdminPersonalDataDto
{
    public string UserId { get; set; } = string.Empty;
    public string TenantKey { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool EmailConfirmed { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
    public string Profile { get; set; } = string.Empty;
    public string StreetAddress { get; set; } = string.Empty;
    public string Locality { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public Dictionary<string, string> PersonalData { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}