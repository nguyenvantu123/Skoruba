namespace Skoruba.Duende.IdentityServer.STS.Identity.ViewModels.Account
{
    public class LoginTenantStatusViewModel
    {
        public string State { get; set; } = "global";

        public string? TenantKey { get; set; }

        public string? DisplayName { get; set; }

        public string? LogoUrl { get; set; }

        public string? Message { get; set; }
    }
}
