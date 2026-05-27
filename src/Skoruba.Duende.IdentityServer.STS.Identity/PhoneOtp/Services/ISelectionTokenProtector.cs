namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Services;

/// <summary>
/// Per-candidate opaque token bound to a specific <c>userId</c> via ASP.NET Core
/// Data Protection. Dùng làm <c>value</c> cho mỗi <c>&lt;option&gt;</c> trong
/// account-select dropdown (Requirements 5.9, 6.8 — Section 4.4 design).
/// </summary>
public interface ISelectionTokenProtector
{
    /// <summary>
    /// Issue an opaque token bound to <paramref name="userId"/>. Token là base64url-encoded
    /// ciphertext, KHÔNG chứa <paramref name="userId"/> ở plaintext.
    /// </summary>
    string Issue(string userId);

    /// <summary>
    /// Reverse <see cref="Issue"/>. Trả về <c>false</c> nếu token bị tampered, sai purpose,
    /// hoặc rỗng/null.
    /// </summary>
    bool TryResolve(string token, out string userId);
}
