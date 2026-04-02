using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Services.Interfaces;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Helpers
{
    public class TenantRoleProvider : ITenantRoleProvider
    {
        private readonly AdminApiConfiguration _configuration;

        public TenantRoleProvider(AdminApiConfiguration configuration)
        {
            _configuration = configuration;
        }

        public string SuperAdminRole => _configuration.AdministrationRole;

        public string TenantAdminRole => _configuration.TenantAdminRole;
    }
}
