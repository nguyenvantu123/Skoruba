namespace Skoruba.Duende.IdentityServer.STS.Identity.Models.Login;

/// <summary>
/// View-only DTO supplied to <c>Views/Shared/Common/_LoginHeader.cshtml</c>
/// so the header partial can forward the current request path and query
/// to the language switcher's hidden <c>returnUrl</c> input.
/// </summary>
/// <remarks>
/// Plain data carrier — no persistence, no business-logic dependency.
/// Populated by the rendering view from <c>HttpContext.Request</c>.
/// </remarks>
public class LoginShellHeaderModel
{
    /// <summary>
    /// Current request path (for example <c>/Account/Login</c>).
    /// </summary>
    public string CurrentPath { get; set; }

    /// <summary>
    /// Current request query string including the leading <c>?</c>, or empty when absent.
    /// </summary>
    public string CurrentQuery { get; set; }
}
