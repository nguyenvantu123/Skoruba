namespace TenantInfrastructure.Resolution;

public sealed class TenantResolutionOptions
{
    // tenantA.sso.domain.com => parts>=3
    public int MinHostParts { get; set; } = 3;

    public HashSet<string> ReservedSubdomains { get; } =
        new(StringComparer.OrdinalIgnoreCase) { "www", "sso", "admin", "api" };

    public Func<string, string> Normalize { get; set; } =
        s => s.Trim().ToLowerInvariant();

    public bool AllowMissingTenant { get; set; } = true;

    // Header names are checked before host/subdomain.
    public List<string> TenantHeaderNames { get; } = new()
    {
        "X-Tenant-Id",
        "X-TenantId"
    };

    // Cookie names used when the tenant is persisted by frontend/gateway.
    public List<string> TenantCookieNames { get; } = new()
    {
        "tenant_id",
        "tenant"
    };

    // Query/form names for development and token flows where headers are unavailable.
    public List<string> TenantQueryNames { get; } = new()
    {
        "tenant",
        "tenant_id",
        "tenantid"
    };

    // Skip tenant resolution entirely for these hosts.
    public HashSet<string> SkipHosts { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "127.0.0.1",
        "::1"
    };
}
