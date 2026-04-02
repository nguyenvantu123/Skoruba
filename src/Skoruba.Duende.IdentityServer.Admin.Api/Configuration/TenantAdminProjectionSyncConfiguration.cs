namespace Skoruba.Duende.IdentityServer.Admin.Api.Configuration
{
    public sealed class TenantAdminProjectionSyncConfiguration
    {
        public const string SectionName = "TenantAdminProjectionSync";

        public bool Enabled { get; set; }
        public string BaseUrl { get; set; } = "https://localhost:7397/";
        public string RelativePath { get; set; } = "/api/internal/tenant-admins/projection";
        public string ApiKeyHeaderName { get; set; } = "X-Internal-Api-Key";
        public string ApiKey { get; set; } = string.Empty;
        public int TimeoutSeconds { get; set; } = 10;
    }
}
