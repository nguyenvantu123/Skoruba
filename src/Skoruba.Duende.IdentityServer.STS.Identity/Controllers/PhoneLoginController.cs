// Copyright (c) Skoruba. All rights reserved.
// See LICENSE in the project root for license information.

using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Duende.IdentityServer.Events;
using Duende.IdentityServer.Services;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.Entities.Identity;
using Skoruba.Duende.IdentityServer.STS.Identity.Helpers;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Configuration;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Filters;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Models;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Services;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Storage;
using Skoruba.Duende.IdentityServer.STS.Identity.ViewModels.Account;

using TenantInfrastructure.Abstractions;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Controllers;

/// <summary>
/// Controller xử lý luồng đăng nhập passwordless qua OTP SMS.
/// Toàn bộ controller bị chặn bởi <see cref="PhoneOtpFeatureGateAttribute"/> — khi feature OFF,
/// mọi route trả về 404. Logic continuation sau verify được mirror byte-equivalent với
/// <c>AccountController.Login</c> (Redirect vs LoadingPage cho native client).
/// </summary>
[AllowAnonymous]
[SecurityHeaders]
[PhoneOtpFeatureGate]
[Route("Account/LoginWithPhone")]
public sealed class PhoneLoginController : Controller
{
    private const string LoginTypePhoneOtp = "phone-otp";
    private const string LoginRedirectPath = "/Account/Login";
    private const string VerifyRedirectPath = "/Account/LoginWithPhone/Verify";
    private const string VerifyViewPath = "~/Views/Account/LoginWithPhone/Verify.cshtml";

    private readonly IPhoneOtpService _phoneOtpService;
    private readonly PhoneOtpSessionCookieCodec _cookieCodec;
    private readonly IPhoneNumberNormalizer _normalizer;
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly ApplicationSignInManager<UserIdentity> _signInManager;
    private readonly UserManager<UserIdentity> _userManager;
    private readonly IIdentityServerInteractionService _interaction;
    private readonly IEventService _events;
    private readonly IPhoneOtpAntiBotChallenge _antiBot;
    private readonly IPhoneOtpStore _store;
    private readonly PhoneOtpLoginConfiguration _config;
    private readonly ILogger<PhoneLoginController> _logger;
    private readonly IStringLocalizer<PhoneLoginController> _localizer;

    public PhoneLoginController(
        IPhoneOtpService phoneOtpService,
        PhoneOtpSessionCookieCodec cookieCodec,
        IPhoneNumberNormalizer normalizer,
        ITenantContextAccessor tenantContextAccessor,
        ApplicationSignInManager<UserIdentity> signInManager,
        UserManager<UserIdentity> userManager,
        IIdentityServerInteractionService interaction,
        IEventService events,
        IPhoneOtpAntiBotChallenge antiBot,
        IPhoneOtpStore store,
        IOptions<PhoneOtpLoginConfiguration> options,
        ILogger<PhoneLoginController> logger,
        IStringLocalizer<PhoneLoginController> localizer)
    {
        _phoneOtpService = phoneOtpService;
        _cookieCodec = cookieCodec;
        _normalizer = normalizer;
        _tenantContextAccessor = tenantContextAccessor;
        _signInManager = signInManager;
        _userManager = userManager;
        _interaction = interaction;
        _events = events;
        _antiBot = antiBot;
        _store = store;
        _config = options.Value;
        _logger = logger;
        _localizer = localizer;
    }

    /// <summary>
    /// Step 1 — Issue OTP. Mọi nhánh rejection được áp delay 200..600 ms (sample bằng
    /// <see cref="RandomNumberGenerator.GetInt32(int, int)"/>) trước khi trả response để các
    /// rejection không phân biệt được về mặt timing (R7.1, R11.2, R11.3).
    /// </summary>
    [HttpPost("Request")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestOtp([FromForm] PhoneRequestViewModel model, CancellationToken ct)
    {
        // Sample delay một lần duy nhất ở đầu request — chỉ apply cho rejection.
        var rejectionDelayMs = RandomNumberGenerator.GetInt32(200, 601);

        if (model is null)
        {
            return await RejectRequestAsync(model: null, rejectionDelayMs, "InvalidModel", ct).ConfigureAwait(false);
        }

        // Honeypot: input ẩn name="website" phải rỗng. Nếu non-empty → bot.
        if (!string.IsNullOrEmpty(model.Website))
        {
            _logger.LogWarning(
                "PhoneOtpRequest: honeypot tripped. {Event} {TenantKey} {RemoteIp} {Outcome}",
                "PhoneOtpRequest",
                _tenantContextAccessor.Current?.TenantKey ?? "<none>",
                GetRemoteIp(),
                "Rejected");
            return await RejectRequestAsync(model, rejectionDelayMs, "HoneypotTripped", ct).ConfigureAwait(false);
        }

        // Anti-bot extension point (no-op default).
        var antiBotDecision = await _antiBot.EvaluateAsync(HttpContext, ct).ConfigureAwait(false);
        if (!antiBotDecision.Allowed)
        {
            _logger.LogWarning(
                "PhoneOtpRequest: anti-bot rejected. {Event} {TenantKey} {RemoteIp} {Outcome} {RateLimitReason}",
                "PhoneOtpRequest",
                _tenantContextAccessor.Current?.TenantKey ?? "<none>",
                GetRemoteIp(),
                "Rejected",
                antiBotDecision.Reason ?? "AntiBot");
            return await RejectRequestAsync(model, rejectionDelayMs, "AntiBot", ct).ConfigureAwait(false);
        }

        var tenant = _tenantContextAccessor.Current;
        if (tenant is null || string.IsNullOrWhiteSpace(tenant.TenantKey))
        {
            _logger.LogWarning(
                "PhoneOtpRequest: missing tenant context. {Event} {RemoteIp} {Outcome} {RateLimitReason}",
                "PhoneOtpRequest",
                GetRemoteIp(),
                "Rejected",
                "MissingTenantContext");
            return await RejectRequestAsync(model, rejectionDelayMs, "MissingTenantContext", ct).ConfigureAwait(false);
        }

        var issueRequest = new IssueOtpRequest(
            RawPhone: model.PhoneNumber ?? string.Empty,
            TenantKey: tenant.TenantKey,
            RemoteIp: GetRemoteIp(),
            ReturnUrl: model.ReturnUrl ?? string.Empty);

        IssueOtpResult issueResult;
        try
        {
            issueResult = await _phoneOtpService.IssueAsync(issueRequest, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "PhoneOtpRequest: unexpected error during IssueAsync. {Event} {TenantKey} {RemoteIp} {Outcome}",
                "PhoneOtpRequest",
                tenant.TenantKey,
                GetRemoteIp(),
                "Rejected");
            return await RejectRequestAsync(model, rejectionDelayMs, "ServiceError", ct).ConfigureAwait(false);
        }

        if (issueResult.Outcome != IssueOutcome.Issued
            || string.IsNullOrEmpty(issueResult.PhoneE164Hash)
            || issueResult.ExpiresAtUtc is null)
        {
            return await RejectRequestAsync(model, rejectionDelayMs, "ServiceRejected", ct).ConfigureAwait(false);
        }

        // Issuance thành công → set cookie qua codec, redirect đến trang Verify.
        var payload = new SessionCookiePayload(
            TenantKey: tenant.TenantKey,
            PhoneE164Hash: issueResult.PhoneE164Hash,
            ExpiresAtUtc: issueResult.ExpiresAtUtc.Value);

        var token = _cookieCodec.Protect(payload);

        Response.Cookies.Append(
            PhoneOtpSessionCookieCodec.CookieName,
            token,
            BuildSessionCookieOptions(issueResult.ExpiresAtUtc.Value));

        var redirectUrl = BuildVerifyRedirectUrl(model.ReturnUrl);

        _logger.LogInformation(
            "PhoneOtpRequest: OTP issued, redirecting to verify page. {Event} {TenantKey} {RemoteIp} {Outcome}",
            "PhoneOtpRequest",
            tenant.TenantKey,
            GetRemoteIp(),
            "Issued");

        return Redirect(redirectUrl);
    }

    /// <summary>
    /// GET trang Verify — render form OTP. Yêu cầu cookie session đã được set ở step 1.
    /// Thiếu cookie hoặc tenant mismatch → 302 về <c>/Account/Login</c> giữ <c>returnUrl</c>.
    /// </summary>
    [HttpGet("Verify")]
    public IActionResult Verify(string? returnUrl = null)
    {
        if (!TryReadAndValidateSessionCookie(out var payload, out var redirectResult))
        {
            return redirectResult!;
        }

        var model = new PhoneVerifyViewModel
        {
            ReturnUrl = returnUrl,
            MaskedPhone = "******",
            OtpLength = _config.OtpLength,
            ResendCooldownRemainingSeconds = 0
        };

        // Cookie mới issue ở step 1: chưa có failure nào, không có cooldown ở GET.
        _ = payload; // payload validated ở trên.

        return View(VerifyViewPath, model);
    }

    /// <summary>
    /// Step 2 — Verify OTP. Sau khi verify thành công, sign-in qua
    /// <see cref="ApplicationSignInManager{TUser}.SignInAsync(TUser, bool, string?)"/> và
    /// continuation theo cùng pattern với <c>AccountController.Login</c>:
    /// <c>Redirect(returnUrl)</c> cho non-native, <c>LoadingPage("Redirect", returnUrl)</c> cho native client.
    /// </summary>
    [HttpPost("Verify")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Verify([FromForm] PhoneVerifyViewModel model, CancellationToken ct)
    {
        if (model is null)
        {
            return RedirectToLoginPreservingReturnUrl(returnUrl: null);
        }

        if (!TryReadAndValidateSessionCookie(out var payload, out var redirectResult))
        {
            return redirectResult!;
        }

        var verifyRequest = new VerifyOtpRequest(
            TenantKey: payload!.TenantKey,
            PhoneE164Hash: payload.PhoneE164Hash,
            SubmittedOtp: model.Otp ?? string.Empty,
            RemoteIp: GetRemoteIp());

        VerifyOtpResult verifyResult;
        try
        {
            verifyResult = await _phoneOtpService.VerifyAsync(verifyRequest, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "PhoneOtpVerify: unexpected error during VerifyAsync. {Event} {TenantKey} {RemoteIp} {Outcome}",
                "PhoneOtpVerify",
                payload.TenantKey,
                GetRemoteIp(),
                "Rejected");
            return RenderVerifyWithError(model);
        }

        if (verifyResult.Outcome != VerifyOutcome.Succeeded || string.IsNullOrEmpty(verifyResult.UserId))
        {
            // Nếu Exhausted/Expired/NoSession — record đã bị xoá ở service. Clear cookie để user
            // bắt đầu lại flow từ trang Login.
            if (verifyResult.Outcome == VerifyOutcome.Exhausted
                || verifyResult.Outcome == VerifyOutcome.Expired
                || verifyResult.Outcome == VerifyOutcome.NoSession)
            {
                ClearSessionCookie();
            }

            return RenderVerifyWithError(model);
        }

        // Sign-in user. Cookie sau verify được phát hành đúng IdentityConstants.ApplicationScheme
        // qua ApplicationSignInManager.SignInAsync (R3.13, R3.15).
        var user = await _userManager.FindByIdAsync(verifyResult.UserId).ConfigureAwait(false);
        if (user is null)
        {
            _logger.LogError(
                "PhoneOtpVerify: verify succeeded but user not found by id. {Event} {TenantKey} {Outcome}",
                "PhoneOtpVerify",
                payload.TenantKey,
                "Rejected");
            ClearSessionCookie();
            return RenderVerifyWithError(model);
        }

        // Step 1 cookie không còn cần thiết sau verify thành công.
        ClearSessionCookie();

        await _signInManager.SignInAsync(user, isPersistent: false).ConfigureAwait(false);

        await _events.RaiseAsync(
            new UserLoginSuccessEvent(
                user.UserName,
                user.Id,
                user.UserName,
                clientId: null)).ConfigureAwait(false);

        _logger.LogInformation(
            "PhoneOtpVerify: sign-in succeeded. {Event} {TenantKey} {RemoteIp} {Outcome} {LoginType}",
            "PhoneOtpVerify",
            payload.TenantKey,
            GetRemoteIp(),
            "Succeeded",
            LoginTypePhoneOtp);

        // Continuation theo cùng pattern với AccountController.Login.
        var returnUrl = model.ReturnUrl;
        var context = await _interaction.GetAuthorizationContextAsync(returnUrl).ConfigureAwait(false);

        if (context != null)
        {
            if (context.IsNativeClient())
            {
                return this.LoadingPage("Redirect", returnUrl);
            }

            return Redirect(returnUrl!);
        }

        if (Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl!);
        }

        if (string.IsNullOrEmpty(returnUrl))
        {
            return Redirect("~/");
        }

        // returnUrl non-empty nhưng không phải authorization context và không local → từ chối an toàn.
        return Redirect("~/");
    }

    /// <summary>
    /// Resend OTP. Phụ thuộc vào cookie session đã issued ở step 1 — không nhận phone từ body
    /// (R3.13). Khi cooldown active, re-render Verify với <c>ResendCooldownRemainingSeconds</c>.
    /// </summary>
    [HttpPost("Resend")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Resend([FromForm] PhoneVerifyViewModel? model, CancellationToken ct)
    {
        if (!TryReadAndValidateSessionCookie(out var payload, out var redirectResult))
        {
            return redirectResult!;
        }

        var returnUrl = model?.ReturnUrl;
        var maskedPhone = "******";

        // Cần lookup record để lấy lại E164 (cookie chỉ chứa hash, controller không có raw phone).
        OtpStoreRecord? existing;
        try
        {
            existing = await _store.GetAsync(payload!.TenantKey, payload.PhoneE164Hash, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "PhoneOtpResend: store lookup failed. {Event} {TenantKey} {RemoteIp} {Outcome}",
                "PhoneOtpResend",
                payload.TenantKey,
                GetRemoteIp(),
                "Rejected");
            return RenderVerifyWithError(BuildVerifyModel(returnUrl, maskedPhone, 0));
        }

        if (existing is null)
        {
            // Record đã hết hạn hoặc bị xoá — không thể resend mà không có raw phone. Clear cookie và quay về Login.
            ClearSessionCookie();
            return RedirectToLoginPreservingReturnUrl(returnUrl);
        }

        maskedPhone = _normalizer.MaskLast4(existing.PhoneE164);

        var resendRequest = new IssueOtpRequest(
            RawPhone: existing.PhoneE164,
            TenantKey: payload.TenantKey,
            RemoteIp: GetRemoteIp(),
            ReturnUrl: returnUrl ?? string.Empty);

        IssueOtpResult resendResult;
        try
        {
            resendResult = await _phoneOtpService.ResendAsync(resendRequest, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "PhoneOtpResend: unexpected error during ResendAsync. {Event} {TenantKey} {RemoteIp} {Outcome}",
                "PhoneOtpResend",
                payload.TenantKey,
                GetRemoteIp(),
                "Rejected");
            return RenderVerifyWithError(BuildVerifyModel(returnUrl, maskedPhone, 0));
        }

        if (resendResult.Outcome == IssueOutcome.Issued
            && !string.IsNullOrEmpty(resendResult.PhoneE164Hash)
            && resendResult.ExpiresAtUtc is not null)
        {
            // Refresh cookie với expiry mới của OTP.
            var newPayload = new SessionCookiePayload(
                TenantKey: payload.TenantKey,
                PhoneE164Hash: resendResult.PhoneE164Hash,
                ExpiresAtUtc: resendResult.ExpiresAtUtc.Value);

            Response.Cookies.Append(
                PhoneOtpSessionCookieCodec.CookieName,
                _cookieCodec.Protect(newPayload),
                BuildSessionCookieOptions(resendResult.ExpiresAtUtc.Value));

            var successModel = BuildVerifyModel(returnUrl, maskedPhone, 0);
            ViewData["PhoneOtpResendSuccess"] = true;
            return View(VerifyViewPath, successModel);
        }

        // Rejection — phổ biến nhất là cooldown active.
        var cooldownRemaining = resendResult.ResendCooldownRemainingSeconds ?? 0;
        var rejectionModel = BuildVerifyModel(returnUrl, maskedPhone, cooldownRemaining);
        return RenderVerifyWithError(rejectionModel);
    }

    /*****************************************/
    /* private helpers                       */
    /*****************************************/

    private async Task<IActionResult> RejectRequestAsync(
        PhoneRequestViewModel? model,
        int delayMs,
        string reason,
        CancellationToken ct)
    {
        try
        {
            await Task.Delay(delayMs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new EmptyResult();
        }

        // Re-render Login với phone tab pre-active + generic error. Vì AccountController.Login
        // build LoginViewModel khá phức tạp, ta dùng TempData để tín hiệu sang GET /Account/Login —
        // task 21 sẽ wire view đọc TempData để pre-activate tab và hiển thị error.
        TempData["PhoneTabPreActive"] = true;
        TempData["PhoneOtpError"] = _localizer["Generic_Request_Error"].Value;
        TempData["PhoneOtpRejectionReason"] = reason;

        var returnUrl = model?.ReturnUrl;
        return RedirectToLoginPreservingReturnUrl(returnUrl);
    }

    private IActionResult RedirectToLoginPreservingReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl))
        {
            return Redirect(LoginRedirectPath);
        }

        return Redirect($"{LoginRedirectPath}?returnUrl={Uri.EscapeDataString(returnUrl)}");
    }

    private static string BuildVerifyRedirectUrl(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl))
        {
            return VerifyRedirectPath;
        }

        return $"{VerifyRedirectPath}?returnUrl={Uri.EscapeDataString(returnUrl)}";
    }

    private static CookieOptions BuildSessionCookieOptions(DateTimeOffset otpExpiresAtUtc)
    {
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            // 30s buffer để client đến được trang Verify sau khi OTP hết hạn (R8.4).
            Expires = otpExpiresAtUtc.AddSeconds(30)
        };
    }

    private bool TryReadAndValidateSessionCookie(
        out SessionCookiePayload? payload,
        out IActionResult? redirectResult)
    {
        payload = null;
        redirectResult = null;

        var raw = Request.Cookies[PhoneOtpSessionCookieCodec.CookieName];
        if (string.IsNullOrEmpty(raw))
        {
            redirectResult = RedirectToLoginPreservingReturnUrl(GetReturnUrlFromRequest());
            return false;
        }

        if (!_cookieCodec.TryUnprotect(raw, out var decoded))
        {
            ClearSessionCookie();
            redirectResult = RedirectToLoginPreservingReturnUrl(GetReturnUrlFromRequest());
            return false;
        }

        // Cross-tenant check (R3.14): nếu cookie thuộc tenant khác (vd user đổi subdomain),
        // xoá cookie và 302 về /Account/Login.
        var currentTenantKey = _tenantContextAccessor.Current?.TenantKey;
        if (string.IsNullOrWhiteSpace(currentTenantKey)
            || !string.Equals(currentTenantKey, decoded.TenantKey, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "PhoneOtpVerify: tenant mismatch on cookie. {Event} {CookieTenantKey} {CurrentTenantKey} {Outcome}",
                "PhoneOtpSession",
                decoded.TenantKey,
                currentTenantKey ?? "<none>",
                "Rejected");
            ClearSessionCookie();
            redirectResult = RedirectToLoginPreservingReturnUrl(GetReturnUrlFromRequest());
            return false;
        }

        payload = decoded;
        return true;
    }

    private void ClearSessionCookie()
    {
        Response.Cookies.Delete(PhoneOtpSessionCookieCodec.CookieName);
    }

    private string? GetReturnUrlFromRequest()
    {
        if (Request.Query.TryGetValue("returnUrl", out var fromQuery) && fromQuery.Count > 0)
        {
            return fromQuery[0];
        }

        if (Request.HasFormContentType && Request.Form.TryGetValue("ReturnUrl", out var fromForm) && fromForm.Count > 0)
        {
            return fromForm[0];
        }

        return null;
    }

    private string GetRemoteIp()
    {
        return HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private PhoneVerifyViewModel BuildVerifyModel(string? returnUrl, string maskedPhone, int cooldownRemaining)
    {
        return new PhoneVerifyViewModel
        {
            ReturnUrl = returnUrl,
            MaskedPhone = maskedPhone,
            OtpLength = _config.OtpLength,
            ResendCooldownRemainingSeconds = cooldownRemaining
        };
    }

    private IActionResult RenderVerifyWithError(PhoneVerifyViewModel model)
    {
        ViewData["PhoneOtpVerifyError"] = _localizer["Generic_Verify_Error"].Value;
        return View(VerifyViewPath, model);
    }
}
