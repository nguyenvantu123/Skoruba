// Copyright (c) Skoruba. All rights reserved.
// See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Duende.IdentityServer.Events;
using Duende.IdentityServer.Services;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
    private const string LoginTypePhoneOtpMulti = "phone-otp-multi";
    private const string LoginRedirectPath = "/Account/Login";
    private const string VerifyRedirectPath = "/Account/LoginWithPhone/Verify";
    private const string SelectAccountRedirectPath = "/Account/LoginWithPhone/SelectAccount";
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
    private readonly TimeProvider _timeProvider;
    private readonly PhoneOtpAccountSelectCookieCodec? _selectCodec;
    private readonly ISelectionTokenProtector? _tokenProtector;
    private readonly IPhoneOtpRateLimiter? _rateLimiter;

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
        IStringLocalizer<PhoneLoginController> localizer,
        TimeProvider timeProvider,
        PhoneOtpAccountSelectCookieCodec? selectCodec = null,
        ISelectionTokenProtector? tokenProtector = null,
        IPhoneOtpRateLimiter? rateLimiter = null)
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
        _timeProvider = timeProvider;
        _selectCodec = selectCodec;
        _tokenProtector = tokenProtector;
        _rateLimiter = rateLimiter;
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

        // R4.1 — Capture Candidate_Set TRƯỚC khi continuation tiếp tục (record đã bị
        // service.DeleteAsync xoá; VerifyOtpResult đã carry CandidateUserIds + PhoneE164).
        // Fallback `[UserId]` cho legacy / single-user shape (R2.6, R14.4).
        var candidateUserIds = verifyResult.CandidateUserIds is { Count: > 0 } cs
            ? cs
            : new[] { verifyResult.UserId! };
        var multiAccountEnabled = _config.MultiAccount.Enabled;

        if (candidateUserIds.Count == 1)
        {
            // R4.2 — Single-user continuation: byte-equivalent với spec gốc phone-otp-login.
            return await SignInSingleCandidateAsync(
                userId: candidateUserIds[0],
                tenantKey: payload.TenantKey,
                returnUrl: model.ReturnUrl,
                model: model).ConfigureAwait(false);
        }

        if (!multiAccountEnabled)
        {
            // R4.3 — Defensive: ngữ cảnh chỉ xảy ra nếu IssueAsync đã rò rỉ multi-record qua
            // race điều kiện flag rotation. Re-render Verify với Generic_Verify_Error,
            // KHÔNG SignInAsync, KHÔNG raise UserLoginSuccessEvent. Clear session cookie để
            // user phải bắt đầu lại flow từ trang Login.
            _logger.LogWarning(
                "PhoneOtpVerify: defensive reject — multi-candidate record while flag off. {Event} {TenantKey} {RemoteIp} {Outcome} {CandidateCount}",
                "PhoneOtpVerify",
                payload.TenantKey,
                GetRemoteIp(),
                "Rejected",
                candidateUserIds.Count);
            ClearSessionCookie();
            return RenderVerifyWithError(model);
        }

        // R4.4 — Multi-account continuation: clear session cookie BEFORE setting
        // account-select cookie (R6.5 — hai cookie không bao giờ coexist), set
        // phone_otp_account_select cookie với AccountSelectContext, log Information,
        // 302 đến /Account/LoginWithPhone/SelectAccount giữ returnUrl.
        if (_selectCodec is null)
        {
            // DI invariant: khi MultiAccount.Enabled = true, AddPhoneOtpLogin SHALL register
            // PhoneOtpAccountSelectCookieCodec (Task 6 + R6.12). Nếu null tại đây — DI bug.
            throw new InvalidOperationException(
                "PhoneOtpAccountSelectCookieCodec chưa được register dù MultiAccount.Enabled = true. Đây là DI bug.");
        }

        // R4.6 — Clear session cookie TRƯỚC khi append account-select cookie để
        // hai cookie không coexist trong cùng response.
        ClearSessionCookie();

        var now = _timeProvider.GetUtcNow();
        var ttl = TimeSpan.FromSeconds(_config.MultiAccount.SelectTtlSeconds);
        var ctx = new AccountSelectContext(
            TenantKey: payload.TenantKey,
            PhoneE164Hash: payload.PhoneE164Hash,
            CandidateUserIds: candidateUserIds,
            IssuedAtUtc: now,
            ExpiresAtUtc: now.Add(ttl),
            OtpRecordKey: $"{payload.TenantKey}:{payload.PhoneE164Hash}",
            Version: 1);

        Response.Cookies.Append(
            PhoneOtpAccountSelectCookieCodec.CookieName,
            _selectCodec.Protect(ctx),
            BuildAccountSelectCookieOptions(ctx.ExpiresAtUtc));

        // Section 4.5 design — handover masked phone qua TempData để SelectAccount GET
        // không phải lookup record (record đã delete bởi VerifyAsync). PhoneE164 trên
        // verifyResult là server-only payload; chỉ MaskedPhone (4 dot + last 4) được expose
        // ra view (R10.5).
        if (!string.IsNullOrEmpty(verifyResult.PhoneE164))
        {
            TempData["PhoneOtpMaskedPhone"] = _normalizer.MaskLast4(verifyResult.PhoneE164);
        }

        // R4.5, R10.2 — log Information với CandidateCount + PhoneSha8 redacted (KHÔNG log
        // raw user-id, raw phone, raw cookie payload).
        _logger.LogInformation(
            "PhoneOtpAccountSelectShown: redirecting to chooser. {Event} {TenantKey} {PhoneSha8} {RemoteIp} {CandidateCount}",
            "PhoneOtpAccountSelectShown",
            payload.TenantKey,
            Sha8(payload.PhoneE164Hash),
            GetRemoteIp(),
            candidateUserIds.Count);

        return Redirect(BuildSelectAccountRedirectUrl(model.ReturnUrl));
    }

    /// <summary>
    /// Single-user continuation extracted from Verify POST (R4.2). Behaviour byte-equivalent
    /// với spec gốc phone-otp-login: clear session cookie, SignInAsync, raise
    /// UserLoginSuccessEvent, hand off theo cascade
    /// (GetAuthorizationContextAsync → IsNativeClient → IsLocalUrl → ~/).
    /// </summary>
    private async Task<IActionResult> SignInSingleCandidateAsync(
        string userId,
        string tenantKey,
        string? returnUrl,
        PhoneVerifyViewModel model)
    {
        var user = await _userManager.FindByIdAsync(userId).ConfigureAwait(false);
        if (user is null)
        {
            _logger.LogError(
                "PhoneOtpVerify: verify succeeded but user not found by id. {Event} {TenantKey} {Outcome}",
                "PhoneOtpVerify",
                tenantKey,
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
            tenantKey,
            GetRemoteIp(),
            "Succeeded",
            LoginTypePhoneOtp);

        return await ContinueWithReturnUrlAsync(returnUrl).ConfigureAwait(false);
    }

    /// <summary>
    /// Continuation cascade theo cùng pattern với <c>AccountController.Login</c>:
    /// <c>(GetAuthorizationContextAsync, IsNativeClient, IsLocalUrl)</c>. Extracted so cả
    /// single-user (R4.2) và future flows (Task 10 multi-select POST, R7.3) có thể tái dùng.
    /// </summary>
    private async Task<IActionResult> ContinueWithReturnUrlAsync(string? returnUrl)
    {
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

    /// <summary>
    /// Multi-account chooser GET. Render <c>SelectAccount.cshtml</c> sau khi user verify OTP
    /// thành công và record gắn nhiều candidate user (Section 4.5 design). Mọi nhánh fail —
    /// cookie absent, decrypt fail, tenant mismatch, TTL expired, candidate set rỗng sau filter
    /// — đều redirect về <c>/Account/Login</c> giữ nguyên <c>returnUrl</c> (R5.2..R5.6, R5.15,
    /// R9.2, R9.3). Action filter <see cref="PhoneOtpMultiAccountFeatureGateAttribute"/> trả 404
    /// khi flag <c>MultiAccount.Enabled = false</c> (R14.4).
    /// </summary>
    [HttpGet("SelectAccount")]
    [PhoneOtpMultiAccountFeatureGate]
    public async Task<IActionResult> SelectAccountGet([FromQuery] string? returnUrl, CancellationToken ct)
    {
        if (_selectCodec is null || _tokenProtector is null)
        {
            // DI invariant (Task 6): khi MultiAccount.Enabled = true, cả hai service phải được
            // register. Nếu null tại đây, đó là DI bug — fail-fast với message rõ ràng.
            throw new InvalidOperationException(
                "PhoneOtpAccountSelectCookieCodec / ISelectionTokenProtector chưa được register dù MultiAccount.Enabled = true. Đây là DI bug.");
        }

        var raw = Request.Cookies[PhoneOtpAccountSelectCookieCodec.CookieName];
        if (string.IsNullOrEmpty(raw))
        {
            // R5.2 — cookie absent: 302 về Login không xoá gì (không có gì để xoá).
            return RedirectToLoginPreservingReturnUrl(returnUrl);
        }

        if (!_selectCodec.TryUnprotect(raw, out var ctx))
        {
            // R6.6.a — decrypt/signature fail: clear cookie + 302 (KHÔNG count phone failure ở GET).
            ClearAccountSelectCookie();
            return RedirectToLoginPreservingReturnUrl(returnUrl);
        }

        var tenantKey = _tenantContextAccessor.Current?.TenantKey;
        if (string.IsNullOrEmpty(tenantKey)
            || !string.Equals(tenantKey, ctx.TenantKey, StringComparison.Ordinal))
        {
            // R5.3, R9.2 — tenant mismatch: clear cookie + 302.
            ClearAccountSelectCookie();
            return RedirectToLoginPreservingReturnUrl(returnUrl);
        }

        if (_timeProvider.GetUtcNow() > ctx.ExpiresAtUtc)
        {
            // R5.4 — TTL expired: clear cookie + log Warning + 302.
            ClearAccountSelectCookie();
            _logger.LogWarning(
                "PhoneOtpAccountSelectExpired: cookie expired. {Event} {TenantKey} {PhoneSha8} {Outcome}",
                "PhoneOtpAccountSelectExpired",
                tenantKey,
                Sha8(ctx.PhoneE164Hash),
                "Rejected");
            return RedirectToLoginPreservingReturnUrl(returnUrl);
        }

        // Load candidate users — silent omit deleted/disabled (R5.6). Filter theo tenant +
        // PhoneNumberConfirmed để tránh leak candidate đã bị off-board (R9.3).
        var candidateIds = ctx.CandidateUserIds?.ToArray() ?? Array.Empty<string>();
        var users = await _userManager.Users
            .Where(u => candidateIds.Contains(u.Id)
                     && u.TenantKey == tenantKey
                     && u.PhoneNumberConfirmed)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (users.Count == 0)
        {
            // R5.15 — empty candidate set sau filter: clear cookie + TempData generic error + 302.
            ClearAccountSelectCookie();
            TempData["PhoneOtpError"] = _localizer["LoginWithPhone.SelectAccount.GenericError"].Value;
            return RedirectToLoginPreservingReturnUrl(returnUrl);
        }

        // R5.5 — preserve deterministic order locked-in từ cookie payload (R2.3).
        var byId = users.ToDictionary(u => u.Id, StringComparer.Ordinal);
        var ordered = ctx.CandidateUserIds
            .Where(id => byId.ContainsKey(id))
            .Where(id => !string.IsNullOrEmpty(byId[id].UserName)) // R12.9 — omit empty UserName
            .Select(id => new CandidateOption(
                SelectionToken: _tokenProtector.Issue(id),
                UserName: byId[id].UserName!))
            .ToList();

        if (ordered.Count == 0)
        {
            // Edge case: toàn bộ surviving candidates có UserName rỗng (R12.9). Treat như R5.15.
            ClearAccountSelectCookie();
            TempData["PhoneOtpError"] = _localizer["LoginWithPhone.SelectAccount.GenericError"].Value;
            return RedirectToLoginPreservingReturnUrl(returnUrl);
        }

        // MaskedPhone được carry từ Verify-success pipeline qua TempData (Section 4.5 design).
        // Khi không có (vd direct GET sau refresh), fallback về 4 dot.
        var maskedPhone = (TempData["PhoneOtpMaskedPhone"] as string) ?? "••••";
        // Giữ key nếu user refresh trang chưa submit — tránh mask biến thành fallback ngay round-trip thứ 2.
        TempData.Keep("PhoneOtpMaskedPhone");

        var error = TempData["PhoneOtpSelectError"] as string;

        var model = new SelectAccountViewModel
        {
            MaskedPhone = maskedPhone,
            Candidates = ordered,
            ReturnUrl = returnUrl,
            Error = error,
        };

        return View("~/Views/Account/LoginWithPhone/SelectAccount.cshtml", model);
    }

    /// <summary>
    /// Multi-account chooser POST. Resolve <c>SelectionToken</c> → <c>userId</c>, validate
    /// (cookie integrity, TTL, tenant, membership in candidate set, lockout) qua chuỗi 9 gate
    /// theo Section 2.2 + Section 4.6 design. Mọi rejection áp DelayJitter 100..300ms để
    /// timing-side-channel free (R11.4, R11.5, R18.7). Success branch clear cookie BEFORE
    /// SignInAsync (R6.9), raise <c>UserLoginSuccessEvent</c> với
    /// <c>LoginType="phone-otp-multi"</c> (R7.2), continuation cascade theo
    /// <c>(GetAuthorizationContextAsync, IsNativeClient, IsLocalUrl)</c> (R7.3, R7.4).
    /// </summary>
    [HttpPost("SelectAccount")]
    [ValidateAntiForgeryToken]
    [PhoneOtpMultiAccountFeatureGate]
    public async Task<IActionResult> SelectAccountPost(
        [FromForm] string SelectionToken,
        [FromForm] string? ReturnUrl,
        CancellationToken ct)
    {
        if (_selectCodec is null || _tokenProtector is null || _rateLimiter is null)
        {
            // DI invariant (Task 1 + Task 5 + Task 6): khi MultiAccount.Enabled = true cả 3
            // service phải được register. Null tại đây = DI bug, fail-fast với message rõ ràng.
            throw new InvalidOperationException(
                "PhoneOtpAccountSelectCookieCodec / ISelectionTokenProtector / IPhoneOtpRateLimiter "
                + "chưa được register dù MultiAccount.Enabled = true. Đây là DI bug.");
        }

        // -----------------------------------------------------------------
        // Gate 1 (R18.5, R18.6): IP rate-limit BEFORE cookie decrypt.
        //
        // SHA-256 hash IP để KHÔNG log raw IP (R10.5, R18.4). Counter incremented every
        // POST regardless of outcome (R18.5) để cookie tampered/missing vẫn tiêu IP budget.
        // -----------------------------------------------------------------
        var rawIp = GetRemoteIp();
        var ipHash = Sha256HexShort(rawIp, chars: 64); // full 64-hex for cache key
        var ipHashShort = Sha256HexShort(rawIp, chars: 8); // 8-hex for log

        await _rateLimiter.RegisterIpSelectAttemptAsync(ipHash, ct).ConfigureAwait(false);
        var ipDecision = await _rateLimiter.CheckIpSelectAsync(ipHash, ct).ConfigureAwait(false);

        if (!ipDecision.Allowed)
        {
            // R18.3, R18.4, R18.7 — log Warning + DelayJitter + TempData generic error + 302.
            _logger.LogWarning(
                "PhoneOtpAccountSelectIpRateLimited: per-IP rate-limit exceeded. "
                + "{Event} {IpHash} {TenantKey} {Outcome} {RateLimitReason}",
                "PhoneOtpAccountSelectIpRateLimited",
                ipHashShort,
                _tenantContextAccessor.Current?.TenantKey ?? "<none>",
                "RateLimited",
                ipDecision.Reason ?? "IpSelectWindow");

            await DelayJitterAsync(ct).ConfigureAwait(false);
            TempData["PhoneOtpError"] = _localizer["LoginWithPhone.SelectAccount.GenericError"].Value;
            return RedirectToLoginPreservingReturnUrl(ReturnUrl);
        }

        // -----------------------------------------------------------------
        // Gate 2: read cookie raw — absent → DelayJitter + 302 (no log, no phone counter).
        // -----------------------------------------------------------------
        var cookieRaw = Request.Cookies[PhoneOtpAccountSelectCookieCodec.CookieName];
        if (string.IsNullOrEmpty(cookieRaw))
        {
            await DelayJitterAsync(ct).ConfigureAwait(false);
            return RedirectToLoginPreservingReturnUrl(ReturnUrl);
        }

        // -----------------------------------------------------------------
        // Gate 3: decrypt cookie — fail → clear cookie + DelayJitter + 302.
        //
        // KHÔNG RegisterVerifyFailureAsync (R11.1): tampered cookie ≠ trustworthy phone identity,
        // không được phép tiêu phone-failure budget từ payload không tin cậy.
        // -----------------------------------------------------------------
        if (!_selectCodec.TryUnprotect(cookieRaw, out var ctx))
        {
            ClearAccountSelectCookie();
            await DelayJitterAsync(ct).ConfigureAwait(false);
            return RedirectToLoginPreservingReturnUrl(ReturnUrl);
        }

        // -----------------------------------------------------------------
        // Gate 4: TTL — now > ExpiresAtUtc → clear cookie + log Warning + TempData expired
        // error + DelayJitter + 302 (R5.4, R8.2).
        // -----------------------------------------------------------------
        var now = _timeProvider.GetUtcNow();
        if (now > ctx.ExpiresAtUtc)
        {
            ClearAccountSelectCookie();
            _logger.LogWarning(
                "PhoneOtpAccountSelectExpired: cookie expired on POST. "
                + "{Event} {TenantKey} {PhoneSha8} {Outcome}",
                "PhoneOtpAccountSelectExpired",
                ctx.TenantKey,
                Sha8(ctx.PhoneE164Hash),
                "Rejected");
            TempData["PhoneOtpError"] = _localizer["LoginWithPhone.SelectAccount.ExpiredError"].Value;
            await DelayJitterAsync(ct).ConfigureAwait(false);
            return RedirectToLoginPreservingReturnUrl(ReturnUrl);
        }

        // -----------------------------------------------------------------
        // Gate 5: tenant match — mismatch → clear cookie + RegisterVerifyFailureAsync (R11.1
        // — cookie qua decrypt, phone identity tin cậy) + log Warning + DelayJitter + 302
        // (R6.6.c, R9.2).
        // -----------------------------------------------------------------
        var tenantKey = _tenantContextAccessor.Current?.TenantKey;
        if (string.IsNullOrEmpty(tenantKey)
            || !string.Equals(tenantKey, ctx.TenantKey, StringComparison.Ordinal))
        {
            ClearAccountSelectCookie();
            await _rateLimiter
                .RegisterVerifyFailureAsync(ctx.TenantKey, ctx.PhoneE164Hash, ct)
                .ConfigureAwait(false);
            _logger.LogWarning(
                "PhoneOtpAccountSelected: tenant mismatch on POST. "
                + "{Event} {CookieTenantKey} {CurrentTenantKey} {PhoneSha8} {Outcome}",
                "PhoneOtpAccountSelected",
                ctx.TenantKey,
                tenantKey ?? "<none>",
                Sha8(ctx.PhoneE164Hash),
                "TenantMismatch");
            await DelayJitterAsync(ct).ConfigureAwait(false);
            return RedirectToLoginPreservingReturnUrl(ReturnUrl);
        }

        // -----------------------------------------------------------------
        // Gate 6: SelectionToken resolve — fail → RegisterVerifyFailureAsync + log Warning +
        // DelayJitter + 302 (R8.6).
        // -----------------------------------------------------------------
        if (!_tokenProtector.TryResolve(SelectionToken ?? string.Empty, out var resolvedUserId))
        {
            await _rateLimiter
                .RegisterVerifyFailureAsync(tenantKey, ctx.PhoneE164Hash, ct)
                .ConfigureAwait(false);
            _logger.LogWarning(
                "PhoneOtpAccountSelectTokenInvalid: SelectionToken decryption failed. "
                + "{Event} {TenantKey} {PhoneSha8} {Outcome} {Reason}",
                "PhoneOtpAccountSelectTokenInvalid",
                tenantKey,
                Sha8(ctx.PhoneE164Hash),
                "Rejected",
                "tokenDecryptFail");
            await DelayJitterAsync(ct).ConfigureAwait(false);
            return RedirectToLoginPreservingReturnUrl(ReturnUrl);
        }

        // -----------------------------------------------------------------
        // Gate 7: userId membership in CandidateUserIds set — not in set →
        // RegisterVerifyFailureAsync + log Warning + DelayJitter + 302 (R6.6.d, R8.6).
        //
        // Defense-in-depth: ngay cả khi attacker forge một token hợp lệ cho userId KHÔNG thuộc
        // tập (rất khó vì cần data-protection key), request vẫn bị reject.
        // -----------------------------------------------------------------
        if (ctx.CandidateUserIds is null
            || !ctx.CandidateUserIds.Contains(resolvedUserId, StringComparer.Ordinal))
        {
            await _rateLimiter
                .RegisterVerifyFailureAsync(tenantKey, ctx.PhoneE164Hash, ct)
                .ConfigureAwait(false);
            _logger.LogWarning(
                "PhoneOtpAccountSelectTokenInvalid: resolved userId not in candidate set. "
                + "{Event} {TenantKey} {PhoneSha8} {Outcome} {Reason}",
                "PhoneOtpAccountSelectTokenInvalid",
                tenantKey,
                Sha8(ctx.PhoneE164Hash),
                "Rejected",
                "userIdNotInSet");
            await DelayJitterAsync(ct).ConfigureAwait(false);
            return RedirectToLoginPreservingReturnUrl(ReturnUrl);
        }

        // -----------------------------------------------------------------
        // Gate 8: reload UserIdentity — null/disabled → RegisterVerifyFailureAsync + log
        // Warning + DelayJitter + RE-RENDER SelectAccount với surviving candidates + GIỮ
        // cookie (R8.5).
        //
        // Filter (TenantKey + PhoneNumberConfirmed) đảm bảo không leak candidate đã off-board
        // (R9.3). KHÔNG clear cookie ở Gate này — user có thể chọn lại candidate khác.
        // -----------------------------------------------------------------
        var user = await _userManager.Users
            .Where(u => u.Id == resolvedUserId
                     && u.TenantKey == tenantKey
                     && u.PhoneNumberConfirmed)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (user is null)
        {
            await _rateLimiter
                .RegisterVerifyFailureAsync(tenantKey, ctx.PhoneE164Hash, ct)
                .ConfigureAwait(false);
            _logger.LogWarning(
                "PhoneOtpAccountSelected: candidate user not found at POST time. "
                + "{Event} {TenantKey} {PhoneSha8} {Outcome}",
                "PhoneOtpAccountSelected",
                tenantKey,
                Sha8(ctx.PhoneE164Hash),
                "UserNotFound");
            await DelayJitterAsync(ct).ConfigureAwait(false);

            // R8.5 — re-render chooser với surviving candidates. Redirect 302 đến GET handler
            // — đơn giản hơn (GET đã handle filter deleted users theo R5.6, R12.9, R5.15);
            // TempData carry generic error qua `PhoneOtpSelectError` (key được GET handler
            // đọc và đẩy vào model.Error).
            TempData["PhoneOtpSelectError"] = _localizer["LoginWithPhone.SelectAccount.GenericError"].Value;
            return Redirect(BuildSelectAccountRedirectUrl(ReturnUrl));
        }

        // -----------------------------------------------------------------
        // Gate 9: lockout — locked → RegisterVerifyFailureAsync + log Warning + DelayJitter
        // + 302 (R7.7, R6.6.e, R11.1).
        // -----------------------------------------------------------------
        if (user.LockoutEnabled
            && user.LockoutEnd is { } lockoutEnd
            && lockoutEnd > now)
        {
            await _rateLimiter
                .RegisterVerifyFailureAsync(tenantKey, ctx.PhoneE164Hash, ct)
                .ConfigureAwait(false);
            _logger.LogWarning(
                "PhoneOtpAccountSelected: candidate user is locked out. "
                + "{Event} {TenantKey} {PhoneSha8} {Outcome}",
                "PhoneOtpAccountSelected",
                tenantKey,
                Sha8(ctx.PhoneE164Hash),
                "UserLockedOut");
            await DelayJitterAsync(ct).ConfigureAwait(false);
            return RedirectToLoginPreservingReturnUrl(ReturnUrl);
        }

        // -----------------------------------------------------------------
        // SUCCESS branch: clear cookie BEFORE SignInAsync (R6.9), SignInAsync (R7.1),
        // raise UserLoginSuccessEvent (R7.2), log Info (R7.5, R10.3), continuation cascade
        // (R7.3, R7.4). KHÔNG DelayJitter (R11.5).
        // -----------------------------------------------------------------
        ClearAccountSelectCookie();

        await _signInManager.SignInAsync(user, isPersistent: false).ConfigureAwait(false);

        await _events
            .RaiseAsync(new UserLoginSuccessEvent(
                user.UserName,
                user.Id,
                user.UserName,
                clientId: null))
            .ConfigureAwait(false);

        _logger.LogInformation(
            "PhoneOtpAccountSelected: sign-in succeeded. "
            + "{Event} {TenantKey} {PhoneSha8} {UserIdHash} {Outcome} {LoginType}",
            "PhoneOtpAccountSelected",
            tenantKey,
            Sha8(ctx.PhoneE164Hash),
            Sha256HexShort(user.Id, chars: 8),
            "Succeeded",
            LoginTypePhoneOtpMulti);

        return await ContinueWithReturnUrlAsync(ReturnUrl).ConfigureAwait(false);
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

    private static CookieOptions BuildAccountSelectCookieOptions(DateTimeOffset expiresAtUtc)
    {
        // R6.1, R6.4 — phone_otp_account_select cookie với HttpOnly+Secure+SameSite=Lax+IsEssential,
        // expiry = AccountSelectContext.ExpiresAtUtc (= IssuedAtUtc + MultiAccount.SelectTtlSeconds).
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Path = "/",
            Expires = expiresAtUtc,
        };
    }

    private static string BuildSelectAccountRedirectUrl(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl))
        {
            return SelectAccountRedirectPath;
        }

        return $"{SelectAccountRedirectPath}?returnUrl={Uri.EscapeDataString(returnUrl)}";
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

    private void ClearAccountSelectCookie()
    {
        Response.Cookies.Delete(PhoneOtpAccountSelectCookieCodec.CookieName);
    }

    private static string Sha8(string input)
    {
        // Defensive trim — payload PhoneE164Hash đã là SHA-256 hex (~64 chars), nhưng giữ
        // helper an toàn với input ngắn hơn để không IndexOutOfRange ở log path.
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }
        return input.Length <= 8 ? input : input.Substring(0, 8);
    }

    /// <summary>
    /// SHA-256 hex của <paramref name="input"/> truncated to <paramref name="chars"/>.
    /// Dùng cho IP hash (full 64-hex cho cache key, 8-hex cho log) và User_Id_Hash (8-hex
    /// cho log audit, R10.3, R10.5). Không bao giờ log <paramref name="input"/> raw.
    /// </summary>
    private static string Sha256HexShort(string input, int chars = 8)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var hex = Convert.ToHexStringLower(bytes);
        return hex.Length <= chars ? hex : hex.Substring(0, chars);
    }

    /// <summary>
    /// Random delay 100..300 ms cho mọi rejection branch của POST <c>/SelectAccount</c>
    /// (R11.4, R11.5, R18.7). Cancellation-safe: nếu <paramref name="ct"/> được cancel
    /// trong delay, swallow exception (caller sẽ tiếp tục return EmptyResult/redirect).
    /// </summary>
    private static async Task DelayJitterAsync(CancellationToken ct)
    {
        var delayMs = RandomNumberGenerator.GetInt32(100, 301);
        try
        {
            await Task.Delay(delayMs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Swallow — caller decides next action (likely EmptyResult).
        }
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
