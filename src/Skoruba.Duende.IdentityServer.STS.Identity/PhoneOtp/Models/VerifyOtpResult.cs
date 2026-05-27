using System.Collections.Generic;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Models;

/// <summary>
/// Kết quả của <see cref="Services.IPhoneOtpService.VerifyAsync"/>.
/// <para>
/// Khi <see cref="Outcome"/> == <see cref="VerifyOutcome.Succeeded"/>, service
/// PHẢI capture <see cref="OtpStoreRecord.CandidateUserIds"/> và
/// <see cref="OtpStoreRecord.PhoneE164"/> từ record TRƯỚC khi
/// <c>IPhoneOtpStore.DeleteAsync</c> xoá record (Requirement 4.1). Hai field
/// dưới đây giữ thông tin đó cho controller dùng để branch theo
/// <c>CandidateUserIds.Count</c> (Requirement 4.2/4.4) và mask phone vào TempData
/// khi redirect đến account-select page (Section 4.5 design).
/// </para>
/// <para>
/// <see cref="PhoneE164"/> là server-only — controller PHẢI KHÔNG đẩy giá trị
/// này ra HTTP response, view, cookie, hay log (Requirement 10.5). Controller
/// chỉ dùng để gọi <c>IPhoneNumberNormalizer.MaskLast4</c> trước khi đặt
/// <c>TempData["PhoneOtpMaskedPhone"]</c>.
/// </para>
/// </summary>
public sealed record VerifyOtpResult(
    VerifyOutcome Outcome,
    string? UserId,
    int AttemptCount,
    IReadOnlyList<string>? CandidateUserIds = null,
    string? PhoneE164 = null);
