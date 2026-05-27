namespace Skoruba.Duende.IdentityServer.STS.Identity.ViewModels.Account;

/// <summary>
/// One option rendered inside the account-select dropdown. <see cref="SelectionToken"/>
/// is the per-candidate opaque token (Section 4.4 design); <see cref="UserName"/> is the
/// raw <c>UserIdentity.UserName</c> (Requirement 5.8). The token never carries the
/// user-id in plaintext (Requirement 6.8).
/// </summary>
public sealed record CandidateOption(string SelectionToken, string UserName);

/// <summary>
/// View model bound to <c>Views/Account/LoginWithPhone/SelectAccount.cshtml</c>
/// (Section 3.5 design). Built by <c>PhoneLoginController.SelectAccountGet</c> after
/// the account-select cookie has been validated.
/// </summary>
public sealed class SelectAccountViewModel
{
    /// <summary>Masked E.164 phone (e.g. <c>•••• 6232</c>) carried via TempData from the verify pipeline.</summary>
    public string MaskedPhone { get; init; } = string.Empty;

    /// <summary>
    /// Candidates in deterministic order locked-in at OTP issuance time (Requirement 2.3, 5.5).
    /// Empty list is unreachable in normal flow — the GET handler short-circuits to <c>/Account/Login</c>
    /// when the surviving candidate set is empty (Requirement 5.15).
    /// </summary>
    public IReadOnlyList<CandidateOption> Candidates { get; init; } = Array.Empty<CandidateOption>();

    /// <summary>Authorization-context returnUrl preserved across the chooser step (Requirement 5.13).</summary>
    public string? ReturnUrl { get; init; }

    /// <summary>Non-null only on the candidate-deleted re-render path (Requirement 8.5).</summary>
    public string? Error { get; init; }
}
