using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace TenantInfrastructure.MasterDb;

public sealed class TenantInfo
{
    private static readonly JsonSerializerOptions ConnectionSecretsSerializerOptions = new(JsonSerializerDefaults.Web);

    public int Id { get; set; }

    // tenantA
    public string TenantKey { get; set; } = default!;

    public string DisplayName { get; set; } = default!;

    public bool IsActive { get; set; } = true;

    // Stored in MasterDb as JSON for multi-service connection secret mapping.
    public string ConnectionSecretsJson { get; set; } = "{}";

    [NotMapped]
    public Dictionary<string, string> ConnectionSecrets
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ConnectionSecretsJson))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            return JsonSerializer.Deserialize<Dictionary<string, string>>(ConnectionSecretsJson, ConnectionSecretsSerializerOptions) ??
                   new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        set
        {
            ConnectionSecretsJson = JsonSerializer.Serialize(
                value ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ConnectionSecretsSerializerOptions);
        }
    }

    // Redirect URL m?c d?nh c?a tenant sau khi dang nh?p
    public string? RedirectUrl { get; set; }

    public string? LogoUrl { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
