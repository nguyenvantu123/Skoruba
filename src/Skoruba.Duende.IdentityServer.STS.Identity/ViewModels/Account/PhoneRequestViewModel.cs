namespace Skoruba.Duende.IdentityServer.STS.Identity.ViewModels.Account
{
    /// <summary>
    /// View model used to capture the phone number entered on the request-OTP screen.
    /// Plain DTO; validation is performed server-side by the phone-OTP pipeline.
    /// </summary>
    public sealed class PhoneRequestViewModel
    {
        public string PhoneNumber { get; set; } = string.Empty;

        public string? ReturnUrl { get; set; }

        /// <summary>
        /// Hidden honeypot field. Expected to remain empty for legitimate submissions.
        /// </summary>
        public string? Website { get; set; }
    }
}
