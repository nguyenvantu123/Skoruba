namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Configuration;

public sealed class PhoneOtpUserApiConfiguration
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://localhost:7397/";
    public string RequestPath { get; set; } = "/connect/phone-otp/request";
    public string VerifyPath { get; set; } = "/connect/phone-otp/verify";
    public string ApiKeyHeaderName { get; set; } = "X-Internal-Api-Key";
    public string ApiKey { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 10;
}
