using System.Collections.Generic;

namespace Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Shared.Dtos.TenantAdminProjection
{
    public sealed class TenantAdminProjectionSyncRequest
    {
        public string TenantKey { get; set; } = string.Empty;
        public string ExternalIdentityId { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string PhoneNumber { get; set; } = string.Empty;
        public string BranchCode { get; set; } = string.Empty;
        public List<string> Roles { get; set; } = new();
        public string AccountType { get; set; } = "TenantAdmin";
        public string AuthSource { get; set; } = "CentralIdentity";
        public bool IsActive { get; set; }
        public TenantAdminProjectionOperation Operation { get; set; } = TenantAdminProjectionOperation.Upsert;
    }
}
