using System;
using System.Collections.Generic;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Dtos;

public sealed class TenantRegistryLookupResultDto
{
    public string TenantId { get; set; } = string.Empty;

    public string Identifier { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string SecretName { get; set; } = string.Empty;

    public Dictionary<string, string> ConnectionSecrets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsActive { get; set; }
}
