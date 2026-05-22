namespace Skoruba.Duende.IdentityServer.STS.Identity.ViewModels.Account
{
    /// <summary>
    /// View model used by the OTP verification screen.
    /// Plain DTO; validation is performed server-side by the phone-OTP pipeline.
    /// </summary>
    public sealed class PhoneVerifyViewModel
    {
        public string Otp { get; set; } = string.Empty;

        public string? ReturnUrl { get; set; }

        public string MaskedPhone { get; set; } = string.Empty;

        public int ResendCooldownRemainingSeconds { get; set; }

        public int OtpLength { get; set; } = 6;
    }
}
