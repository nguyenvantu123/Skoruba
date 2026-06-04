using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Services;

public interface IExternalPhoneOtpClient
{
    Task<ExternalPhoneOtpIssueResult> RequestAsync(
        ExternalPhoneOtpIssueRequest request,
        CancellationToken cancellationToken = default);

    Task<ExternalPhoneOtpVerifyResult> VerifyAsync(
        ExternalPhoneOtpVerifyRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ExternalPhoneOtpIssueRequest(
    string PhoneNumber,
    string TenantKey,
    string ClientId,
    string RemoteIp,
    string ReturnUrl);

public sealed record ExternalPhoneOtpVerifyRequest(
    string PhoneNumber,
    string Otp,
    string TenantKey,
    string ClientId,
    string RemoteIp);

public sealed record ExternalPhoneOtpIssueResult(
    bool Succeeded,
    string? MaskedPhone,
    DateTimeOffset? ExpiresAtUtc,
    int? ExpiresInSeconds,
    int? ResendCooldownRemainingSeconds,
    string? GeneratedOtp = null,
    string? RejectionReason = null);

public sealed record ExternalPhoneOtpVerifyResult(
    bool Succeeded,
    IReadOnlyList<string> UserNames,
    string? RejectionReason = null);
