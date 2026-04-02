namespace TenantInfrastructure.Abstractions;

public sealed record TenantContext(string TenantKey, IReadOnlyDictionary<string, string> ConnectionSecrets)
{
    public string? GetConnectionSecret(string serviceKey)
    {
        if (string.IsNullOrWhiteSpace(serviceKey))
        {
            return null;
        }

        return ConnectionSecrets.TryGetValue(serviceKey, out var secretName) &&
               !string.IsNullOrWhiteSpace(secretName)
            ? secretName
            : null;
    }
}
