using System.Collections.Generic;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Models;

public sealed record OtpStoreRecord
{
    public byte[] OtpHash { get; init; } = Array.Empty<byte>();
    public string TenantKey { get; init; } = string.Empty;
    public string PhoneE164 { get; init; } = string.Empty;

    /// <summary>
    /// Equal to <c>CandidateUserIds[0]</c> when <see cref="CandidateUserIds"/> is non-empty.
    /// Kept for backward-compat with code paths that read <c>UserId</c> directly
    /// (Requirement 2.4).
    /// </summary>
    public string UserId { get; init; } = string.Empty;

    /// <summary>
    /// Lock-in danh sách userId thoả filter ở thời điểm <c>IssueAsync</c>.
    /// Khi flag <c>MultiAccount.Enabled = false</c> hoặc lookup chỉ trả 1 user,
    /// list chứa đúng 1 phần tử = <see cref="UserId"/> (Requirement 2.4).
    ///
    /// Backward-compat rule (Requirement 2.6, 14.4, 16.8): record JSON cũ thiếu
    /// field <c>candidateUserIds</c> SHALL deserialize với danh sách rỗng; đọc
    /// path (<see cref="Storage.RedisPhoneOtpStore.GetAsync"/>) sẽ áp fallback
    /// <c>CandidateUserIds = [UserId]</c>.
    /// </summary>
    public IReadOnlyList<string> CandidateUserIds { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }
    public int AttemptCount { get; init; }
}
