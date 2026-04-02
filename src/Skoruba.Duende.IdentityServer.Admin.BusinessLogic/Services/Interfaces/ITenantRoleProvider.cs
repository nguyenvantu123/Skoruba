namespace Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Services.Interfaces
{
    public interface ITenantRoleProvider
    {
        string SuperAdminRole { get; }
        string TenantAdminRole { get; }
    }
}
