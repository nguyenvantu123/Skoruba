using System.Collections.Generic;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Models;

/// <summary>
/// Kết quả của <see cref="Services.IPhoneOtpService.IssueAsync"/>.
/// Field <see cref="CandidateUserIds"/> mang Candidate_Set đã persist trong
/// <see cref="OtpStoreRecord"/> (Requirement 2.4); call site cũ không pass
/// field này vẫn build OK (default <c>null</c>) — backward-compat (Section 3.2 design).
/// </summary>
public sealed record IssueOtpResult(
    IssueOutcome Outcome,
    string? PhoneE164Hash,
    DateTimeOffset? ExpiresAtUtc,
    int? ResendCooldownRemainingSeconds,
    IReadOnlyList<string>? CandidateUserIds = null,
    string? RejectionReason = null,
    string? GeneratedOtp = null);
