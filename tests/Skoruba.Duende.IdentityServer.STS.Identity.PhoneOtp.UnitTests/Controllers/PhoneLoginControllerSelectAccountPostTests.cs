using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

using Duende.IdentityServer.Events;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;

using FluentAssertions;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.DbContexts;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.Entities.Identity;
using Skoruba.Duende.IdentityServer.STS.Identity.Controllers;
using Skoruba.Duende.IdentityServer.STS.Identity.Helpers;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Configuration;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Models;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Services;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Storage;

using TenantInfrastructure.Abstractions;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.UnitTests.Controllers;

/// <summary>
/// Unit tests cho <c>PhoneLoginController.SelectAccountPost</c> (Section 4.6 + Section 2.2
/// design). Mỗi test exercise đúng 1 trong 9 gate hoặc success branch và assert:
/// <list type="bullet">
///   <item>HTTP result (Redirect vs Redirect-to-SelectAccount vs Continuation).</item>
///   <item>Counter side-effects (IP counter incremented mọi POST; phone-failure counter
///   incremented chỉ Gate 5..9; Gate 2/3 KHÔNG count phone failure per R11.1).</item>
///   <item>Log entry shape (Event tag, redacted IP-hash / phone-sha8 / user-id-hash).</item>
///   <item>Cookie state (cleared cho Gate 3/4/5/success; preserved cho Gate 8 re-render).</item>
///   <item>Sign-in side-effects (chỉ ở success branch).</item>
/// </list>
/// Validates: Requirements 6.6, 6.7, 6.8, 6.9, 6.10, 7.1, 7.2, 7.3, 7.5, 8.5, 8.6, 9.2, 9.3,
/// 10.3, 10.5, 11.1, 11.4, 11.5, 18.3, 18.4, 18.5, 18.6, 18.7.
/// </summary>
public sealed class PhoneLoginControllerSelectAccountPostTests : IDisposable
{
    private const string TenantKey = "tenant-a";
    private const string PhoneE164Hash = "f1d2c3b4a5968778695a4b3c2d1e0f00deadbeefcafef00dabcd1234abcd1234";

    private readonly ServiceProvider _rootProvider;
    private readonly IServiceScope _scope;
    private readonly AdminIdentityDbContext _dbContext;
    private readonly UserManager<UserIdentity> _userManager;
    private readonly EphemeralDataProtectionProvider _dataProtection;
    private readonly PhoneOtpAccountSelectCookieCodec _selectCodec;
    private readonly SelectionTokenProtector _tokenProtector;
    private readonly TestTimeProvider _timeProvider;
    private readonly TestTenantContextAccessor _tenant;
    private readonly StubStringLocalizer _localizer;
    private readonly RecordingLogger<PhoneLoginController> _logger;
    private readonly IPhoneOtpRateLimiter _rateLimiter;
    private readonly IEventService _events;
    private readonly RecordingSignInManager _signInManager;

    public PhoneLoginControllerSelectAccountPostTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddDbContext<AdminIdentityDbContext>(o =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services
            .AddIdentityCore<UserIdentity>()
            .AddRoles<UserIdentityRole>()
            .AddEntityFrameworkStores<AdminIdentityDbContext>()
            .AddDefaultTokenProviders();

        _rootProvider = services.BuildServiceProvider();
        _scope = _rootProvider.CreateScope();
        _dbContext = _scope.ServiceProvider.GetRequiredService<AdminIdentityDbContext>();
        _userManager = _scope.ServiceProvider.GetRequiredService<UserManager<UserIdentity>>();

        _dataProtection = new EphemeralDataProtectionProvider();
        _selectCodec = new PhoneOtpAccountSelectCookieCodec(_dataProtection);
        _tokenProtector = new SelectionTokenProtector(_dataProtection);
        _timeProvider = new TestTimeProvider(new DateTimeOffset(2025, 1, 5, 8, 5, 0, TimeSpan.Zero));
        _tenant = new TestTenantContextAccessor(new TenantContext(TenantKey, new Dictionary<string, string>()));
        _localizer = new StubStringLocalizer();
        _logger = new RecordingLogger<PhoneLoginController>();

        // Default rate limiter: allow-all (each test customises CheckIpSelectAsync as needed).
        _rateLimiter = Substitute.For<IPhoneOtpRateLimiter>();
        _rateLimiter
            .CheckIpSelectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RateLimitDecision(true, null, null));

        _events = Substitute.For<IEventService>();
        _signInManager = new RecordingSignInManager(
            _userManager,
            _scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<UserIdentity>>());
    }

    public void Dispose()
    {
        _scope.Dispose();
        _rootProvider.Dispose();
    }

    private async Task<UserIdentity> SeedUserAsync(
        string id,
        string userName,
        string tenantKey = TenantKey,
        bool phoneConfirmed = true,
        bool lockoutEnabled = false,
        DateTimeOffset? lockoutEnd = null)
    {
        var user = new UserIdentity
        {
            Id = id,
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Email = $"{userName}@example.com",
            NormalizedEmail = $"{userName}@example.com".ToUpperInvariant(),
            PhoneNumber = "+84334336232",
            PhoneNumberConfirmed = phoneConfirmed,
            TenantKey = tenantKey,
            SecurityStamp = Guid.NewGuid().ToString(),
            LockoutEnabled = lockoutEnabled,
            LockoutEnd = lockoutEnd,
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();
        return user;
    }

    private PhoneLoginController BuildController(
        string? cookieRaw,
        bool wireSelectionServices = true,
        bool wireRateLimiter = true)
    {
        var config = new PhoneOtpLoginConfiguration
        {
            Enabled = true,
            DefaultRegion = "VN",
            OtpLength = 6,
            MultiAccount = new MultiAccountConfiguration
            {
                Enabled = true,
                SelectTtlSeconds = 60,
                IpSelectRateLimitWindowSeconds = 600,
                IpSelectRateLimitMaxRequests = 30,
            },
        };

        var interaction = Substitute.For<IIdentityServerInteractionService>();
        interaction
            .GetAuthorizationContextAsync(Arg.Any<string?>())
            .Returns(Task.FromResult<AuthorizationRequest?>(null));

        var controller = new PhoneLoginController(
            phoneOtpService: Substitute.For<IPhoneOtpService>(),
            cookieCodec: new PhoneOtpSessionCookieCodec(_dataProtection),
            normalizer: new PhoneNumberNormalizer(),
            tenantContextAccessor: _tenant,
            signInManager: _signInManager,
            userManager: _userManager,
            interaction: interaction,
            events: _events,
            antiBot: new NoopPhoneOtpAntiBotChallenge(),
            store: Substitute.For<IPhoneOtpStore>(),
            options: Options.Create(config),
            logger: _logger,
            localizer: _localizer,
            timeProvider: _timeProvider,
            selectCodec: wireSelectionServices ? _selectCodec : null,
            tokenProtector: wireSelectionServices ? _tokenProtector : null,
            rateLimiter: wireRateLimiter ? _rateLimiter : null);

        var httpContext = new DefaultHttpContext
        {
            RequestServices = _scope.ServiceProvider,
        };
        httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.42");

        if (!string.IsNullOrEmpty(cookieRaw))
        {
            httpContext.Request.Headers["Cookie"] =
                $"{PhoneOtpAccountSelectCookieCodec.CookieName}={cookieRaw}";
        }

        var urlHelper = Substitute.For<IUrlHelper>();
        urlHelper.IsLocalUrl(Arg.Any<string?>()).Returns(false);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
            RouteData = new RouteData(),
            ActionDescriptor = new ControllerActionDescriptor(),
        };
        controller.Url = urlHelper;
        controller.TempData = new TempDataDictionary(httpContext, new TestTempDataProvider());

        return controller;
    }

    private string ProtectCookie(
        IReadOnlyList<string> candidateIds,
        string tenantKey = TenantKey,
        DateTimeOffset? expiresAtUtc = null,
        DateTimeOffset? issuedAtUtc = null,
        string phoneE164Hash = PhoneE164Hash)
    {
        var issued = issuedAtUtc ?? _timeProvider.GetUtcNow();
        var expires = expiresAtUtc ?? issued.AddSeconds(60);
        var ctx = new AccountSelectContext(
            TenantKey: tenantKey,
            PhoneE164Hash: phoneE164Hash,
            CandidateUserIds: candidateIds,
            IssuedAtUtc: issued,
            ExpiresAtUtc: expires,
            OtpRecordKey: $"{tenantKey}:{phoneE164Hash}",
            Version: 1);
        return _selectCodec.Protect(ctx);
    }

    // ----------------------------------------------------------------------
    // Gate 1: IP rate-limit
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Gate1_IpRateLimit_Exceeded_Rejects_LogsWarning_NoCookieDecrypt()
    {
        // R18.3, R18.4, R18.5, R18.6, R18.7 — IP rate-limit BEFORE cookie decrypt.
        // RegisterIpSelectAttemptAsync incremented; rejection log Warning với IpHash redacted;
        // 302 /Account/Login. KHÔNG decrypt cookie, KHÔNG count phone failure.
        await SeedUserAsync("u-1", "alice");
        var raw = ProtectCookie(new[] { "u-1" });
        var token = _tokenProtector.Issue("u-1");

        _rateLimiter
            .CheckIpSelectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RateLimitDecision(false, "IpSelectWindow", null));

        var controller = BuildController(cookieRaw: raw);

        var result = await controller.SelectAccountPost(
            SelectionToken: token,
            ReturnUrl: "/protected",
            CancellationToken.None);

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Be("/Account/Login?returnUrl=" + Uri.EscapeDataString("/protected"));

        await _rateLimiter.Received(1).RegisterIpSelectAttemptAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _rateLimiter.Received(1).CheckIpSelectAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        // R11.1 — không tiêu phone failure khi IP rate-limited.
        await _rateLimiter.DidNotReceive().RegisterVerifyFailureAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Sign-in NOT invoked.
        _signInManager.SignInCalls.Should().BeEmpty();

        // Generic error TempData wired (R18.3 user feedback).
        controller.TempData["PhoneOtpError"].Should().Be("LoginWithPhone.SelectAccount.GenericError");

        // Log Warning với event tag + redacted IpHash (8 hex).
        var entry = _logger.Entries.Should().ContainSingle(e =>
            e.Message.Contains("PhoneOtpAccountSelectIpRateLimited")).Which;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.GetProperty("Outcome").Should().Be("RateLimited");
        var ipHash = entry.GetProperty("IpHash") as string;
        ipHash.Should().NotBeNullOrEmpty().And.HaveLength(8);
        // R18.4 — không log raw IP.
        entry.GetProperty("IpHash").Should().NotBe("203.0.113.42");
    }

    // ----------------------------------------------------------------------
    // Gate 2: cookie absent
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Gate2_CookieAbsent_Redirects_NoPhoneCounter()
    {
        // Cookie absent → DelayJitter + 302. IP counter incremented (R18.5), phone counter NOT
        // (no trustworthy phone identity).
        var controller = BuildController(cookieRaw: null);

        var result = await controller.SelectAccountPost(
            SelectionToken: "any-token",
            ReturnUrl: null,
            CancellationToken.None);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Account/Login");

        await _rateLimiter.Received(1).RegisterIpSelectAttemptAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _rateLimiter.DidNotReceive().RegisterVerifyFailureAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // No log entry for cookie-absent (no event emitted per design).
        _logger.Entries.Should().NotContain(e =>
            e.Message.Contains("PhoneOtpAccountSelected")
            || e.Message.Contains("PhoneOtpAccountSelectExpired"));

        _signInManager.SignInCalls.Should().BeEmpty();
    }

    // ----------------------------------------------------------------------
    // Gate 3: cookie decrypt fail
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Gate3_DecryptFail_ClearsCookie_NoPhoneCounter()
    {
        // R11.1 — tampered cookie KHÔNG được phép tiêu phone-failure budget từ payload không
        // tin cậy. IP counter incremented; cookie cleared; 302 to /Account/Login.
        var controller = BuildController(cookieRaw: "this-is-not-a-valid-protected-payload");

        var result = await controller.SelectAccountPost(
            SelectionToken: "any-token",
            ReturnUrl: "/r",
            CancellationToken.None);

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Be("/Account/Login?returnUrl=" + Uri.EscapeDataString("/r"));
        AssertCookieDeleted(controller);

        await _rateLimiter.Received(1).RegisterIpSelectAttemptAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        // R11.1.
        await _rateLimiter.DidNotReceive().RegisterVerifyFailureAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        _signInManager.SignInCalls.Should().BeEmpty();
    }

    // ----------------------------------------------------------------------
    // Gate 4: TTL expired
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Gate4_TtlExpired_ClearsCookie_LogsExpired_PhoneCounterNotIncremented()
    {
        // R5.4, R8.2 — cookie issued nhiều phút trước, expiry đã qua. Clear cookie + log
        // Warning + TempData expired error + 302. Note: Section 2.2 design ghi rõ Gate 4
        // KHÔNG count phone failure (chỉ log + redirect) — vì user đơn giản là quá chậm,
        // không phải brute-force attempt; bug-safety: cookie đã expired không còn giá trị
        // để brute-force.
        await SeedUserAsync("u-1", "alice");
        var pastIssued = _timeProvider.GetUtcNow().AddSeconds(-120);
        var pastExpiry = pastIssued.AddSeconds(60);
        var raw = ProtectCookie(new[] { "u-1" }, issuedAtUtc: pastIssued, expiresAtUtc: pastExpiry);
        var token = _tokenProtector.Issue("u-1");

        var controller = BuildController(cookieRaw: raw);

        var result = await controller.SelectAccountPost(
            SelectionToken: token,
            ReturnUrl: null,
            CancellationToken.None);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Account/Login");
        AssertCookieDeleted(controller);

        controller.TempData["PhoneOtpError"].Should().Be("LoginWithPhone.SelectAccount.ExpiredError");

        var entry = _logger.Entries.Should().ContainSingle(e =>
            e.Message.Contains("PhoneOtpAccountSelectExpired")).Which;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.GetProperty("TenantKey").Should().Be(TenantKey);
        entry.GetProperty("Outcome").Should().Be("Rejected");

        // Per Section 2.2 + R11.1 ngữ nghĩa: cookie expired không count phone failure
        // (giảm tải false-positive lockout cho user chậm).
        await _rateLimiter.DidNotReceive().RegisterVerifyFailureAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        _signInManager.SignInCalls.Should().BeEmpty();
    }

    // ----------------------------------------------------------------------
    // Gate 5: tenant mismatch
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Gate5_TenantMismatch_ClearsCookie_PhoneCounterIncremented()
    {
        // R6.6.c, R9.2, R11.1 — cookie tenant != current tenant. Clear cookie +
        // RegisterVerifyFailureAsync (cookie qua decrypt, phone identity tin cậy) + log
        // Warning Outcome="TenantMismatch" + 302.
        await SeedUserAsync("u-1", "alice");
        var raw = ProtectCookie(new[] { "u-1" }, tenantKey: "other-tenant");
        var token = _tokenProtector.Issue("u-1");

        var controller = BuildController(cookieRaw: raw);

        var result = await controller.SelectAccountPost(
            SelectionToken: token,
            ReturnUrl: null,
            CancellationToken.None);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Account/Login");
        AssertCookieDeleted(controller);

        await _rateLimiter.Received(1).RegisterVerifyFailureAsync(
            "other-tenant",
            PhoneE164Hash,
            Arg.Any<CancellationToken>());

        var entry = _logger.Entries.Should().ContainSingle(e =>
            e.Message.Contains("PhoneOtpAccountSelected")
            && e.GetProperty("Outcome") as string == "TenantMismatch").Which;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.GetProperty("CookieTenantKey").Should().Be("other-tenant");
        entry.GetProperty("CurrentTenantKey").Should().Be(TenantKey);

        _signInManager.SignInCalls.Should().BeEmpty();
    }

    // ----------------------------------------------------------------------
    // Gate 6: SelectionToken decrypt fail
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Gate6_TokenInvalid_PhoneCounterIncremented_LogsTokenInvalid()
    {
        // R8.6 — SelectionToken decrypt/signature fail → RegisterVerifyFailureAsync + log
        // Warning Event="PhoneOtpAccountSelectTokenInvalid" + 302.
        await SeedUserAsync("u-1", "alice");
        var raw = ProtectCookie(new[] { "u-1" });

        var controller = BuildController(cookieRaw: raw);

        var result = await controller.SelectAccountPost(
            SelectionToken: "not-a-real-token-xxx",
            ReturnUrl: null,
            CancellationToken.None);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Account/Login");

        await _rateLimiter.Received(1).RegisterVerifyFailureAsync(
            TenantKey, PhoneE164Hash, Arg.Any<CancellationToken>());

        var entry = _logger.Entries.Should().ContainSingle(e =>
            e.Message.Contains("PhoneOtpAccountSelectTokenInvalid")).Which;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.GetProperty("Reason").Should().Be("tokenDecryptFail");

        _signInManager.SignInCalls.Should().BeEmpty();
    }

    // ----------------------------------------------------------------------
    // Gate 7: userId not in candidate set
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Gate7_UserIdNotInSet_PhoneCounterIncremented_LogsTokenInvalid()
    {
        // R6.6.d, R8.6 — token resolves nhưng userId không thuộc cookie.CandidateUserIds.
        await SeedUserAsync("u-1", "alice");
        await SeedUserAsync("u-other", "outsider");
        var raw = ProtectCookie(new[] { "u-1" });
        // Token issued cho u-other, nhưng cookie chỉ chứa u-1 → membership fail.
        var token = _tokenProtector.Issue("u-other");

        var controller = BuildController(cookieRaw: raw);

        var result = await controller.SelectAccountPost(
            SelectionToken: token,
            ReturnUrl: null,
            CancellationToken.None);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Account/Login");

        await _rateLimiter.Received(1).RegisterVerifyFailureAsync(
            TenantKey, PhoneE164Hash, Arg.Any<CancellationToken>());

        var entry = _logger.Entries.Should().ContainSingle(e =>
            e.Message.Contains("PhoneOtpAccountSelectTokenInvalid")
            && e.GetProperty("Reason") as string == "userIdNotInSet").Which;
        entry.Level.Should().Be(LogLevel.Warning);

        _signInManager.SignInCalls.Should().BeEmpty();
    }

    // ----------------------------------------------------------------------
    // Gate 8: user not found / disabled — re-render với surviving candidates, GIỮ cookie
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Gate8_UserNotFound_PhoneCounterIncremented_RedirectsToSelectAccountWithError()
    {
        // R8.5 — cookie hợp lệ, token resolve thành userId trong candidate set, nhưng user
        // không còn trong DB (deleted between issue và select). RegisterVerifyFailureAsync +
        // log Warning Outcome="UserNotFound" + 302 đến GET /SelectAccount để re-render với
        // surviving candidates. GIỮ cookie để user có thể chọn candidate khác.
        await SeedUserAsync("u-survive", "bob");
        // u-deleted KHÔNG seed → query Gate 8 trả null.
        var raw = ProtectCookie(new[] { "u-survive", "u-deleted" });
        var token = _tokenProtector.Issue("u-deleted");

        var controller = BuildController(cookieRaw: raw);

        var result = await controller.SelectAccountPost(
            SelectionToken: token,
            ReturnUrl: "/back",
            CancellationToken.None);

        // Redirect đến GET /SelectAccount preserving returnUrl.
        var redirect = result.Should().BeOfType<RedirectResult>().Which;
        redirect.Url.Should().Be(
            "/Account/LoginWithPhone/SelectAccount?returnUrl="
            + Uri.EscapeDataString("/back"));

        await _rateLimiter.Received(1).RegisterVerifyFailureAsync(
            TenantKey, PhoneE164Hash, Arg.Any<CancellationToken>());

        var entry = _logger.Entries.Should().ContainSingle(e =>
            e.Message.Contains("PhoneOtpAccountSelected")
            && e.GetProperty("Outcome") as string == "UserNotFound").Which;
        entry.Level.Should().Be(LogLevel.Warning);

        // R8.5 — cookie KHÔNG bị clear (user có thể chọn lại candidate khác).
        AssertCookieNotDeleted(controller);

        // TempData chứa generic error để GET handler render alert region.
        controller.TempData["PhoneOtpSelectError"].Should().Be("LoginWithPhone.SelectAccount.GenericError");

        _signInManager.SignInCalls.Should().BeEmpty();
    }

    // ----------------------------------------------------------------------
    // Gate 9: lockout
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Gate9_UserLockedOut_PhoneCounterIncremented_LogsLockedOut()
    {
        // R7.7, R6.6.e, R11.1 — user resolved nhưng LockoutEnd > now. RegisterVerifyFailureAsync
        // + log Warning Outcome="UserLockedOut" + 302.
        var lockoutEnd = _timeProvider.GetUtcNow().AddMinutes(15);
        await SeedUserAsync("u-1", "alice", lockoutEnabled: true, lockoutEnd: lockoutEnd);
        var raw = ProtectCookie(new[] { "u-1" });
        var token = _tokenProtector.Issue("u-1");

        var controller = BuildController(cookieRaw: raw);

        var result = await controller.SelectAccountPost(
            SelectionToken: token,
            ReturnUrl: null,
            CancellationToken.None);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Account/Login");

        await _rateLimiter.Received(1).RegisterVerifyFailureAsync(
            TenantKey, PhoneE164Hash, Arg.Any<CancellationToken>());

        var entry = _logger.Entries.Should().ContainSingle(e =>
            e.Message.Contains("PhoneOtpAccountSelected")
            && e.GetProperty("Outcome") as string == "UserLockedOut").Which;
        entry.Level.Should().Be(LogLevel.Warning);

        _signInManager.SignInCalls.Should().BeEmpty();
    }

    // ----------------------------------------------------------------------
    // Success branch
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Success_ClearsCookie_SignsIn_RaisesEvent_LogsSucceeded()
    {
        // R6.9 (clear cookie BEFORE SignInAsync), R7.1 (SignInAsync isPersistent=false), R7.2
        // (raise UserLoginSuccessEvent), R7.3/R7.4 (continuation cascade), R7.5/R10.3
        // (PhoneOtpAccountSelected Succeeded log), R10.5 (User_Id_Hash redacted).
        await SeedUserAsync("u-1", "alice");
        var raw = ProtectCookie(new[] { "u-1" });
        var token = _tokenProtector.Issue("u-1");

        var controller = BuildController(cookieRaw: raw);

        var result = await controller.SelectAccountPost(
            SelectionToken: token,
            ReturnUrl: null,
            CancellationToken.None);

        // GetAuthorizationContextAsync = null + IsLocalUrl = false + returnUrl null → ~/.
        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("~/");

        AssertCookieDeleted(controller);

        _signInManager.SignInCalls.Should().HaveCount(1);
        _signInManager.SignInCalls.Single().UserId.Should().Be("u-1");
        _signInManager.SignInCalls.Single().IsPersistent.Should().BeFalse();

        await _events.Received(1).RaiseAsync(Arg.Any<UserLoginSuccessEvent>());

        var entry = _logger.Entries.Should().ContainSingle(e =>
            e.Message.Contains("PhoneOtpAccountSelected")
            && e.GetProperty("Outcome") as string == "Succeeded").Which;
        entry.Level.Should().Be(LogLevel.Information);
        entry.GetProperty("LoginType").Should().Be("phone-otp-multi");
        entry.GetProperty("TenantKey").Should().Be(TenantKey);
        // R10.5 — User_Id_Hash là 8 hex SHA-256 (NOT raw "u-1").
        var userIdHash = entry.GetProperty("UserIdHash") as string;
        userIdHash.Should().NotBeNullOrEmpty().And.HaveLength(8);
        userIdHash.Should().NotBe("u-1");
    }

    [Fact]
    public async Task Success_NoPhoneCounter_NoFailureSideEffects()
    {
        // Counter side-effect-free verification: success branch không gọi
        // RegisterVerifyFailureAsync (chỉ rejection branches gọi). IP counter VẪN incremented
        // (R18.5 — every POST regardless of outcome).
        await SeedUserAsync("u-1", "alice");
        var raw = ProtectCookie(new[] { "u-1" });
        var token = _tokenProtector.Issue("u-1");

        var controller = BuildController(cookieRaw: raw);

        var _ = await controller.SelectAccountPost(
            SelectionToken: token,
            ReturnUrl: null,
            CancellationToken.None);

        // R18.5 — IP counter still incremented.
        await _rateLimiter.Received(1).RegisterIpSelectAttemptAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Phone failure counter NOT touched on success.
        await _rateLimiter.DidNotReceive().RegisterVerifyFailureAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ----------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------

    private static void AssertCookieDeleted(PhoneLoginController controller)
    {
        var setCookies = controller.Response.Headers["Set-Cookie"].ToString();
        setCookies.Should().Contain(PhoneOtpAccountSelectCookieCodec.CookieName);
        setCookies.Should().Contain("expires=", "delete cookie should attach an expiry");
    }

    private static void AssertCookieNotDeleted(PhoneLoginController controller)
    {
        var setCookies = controller.Response.Headers["Set-Cookie"].ToString();
        setCookies.Should().NotContain(PhoneOtpAccountSelectCookieCodec.CookieName,
            "Gate 8 re-render should preserve the account-select cookie");
    }

    // ----------------------------------------------------------------------
    // test doubles
    // ----------------------------------------------------------------------

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public TestTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class TestTenantContextAccessor : ITenantContextAccessor
    {
        private TenantContext? _current;
        public TestTenantContextAccessor(TenantContext? current) => _current = current;
        public TenantContext? Current => _current;
        public void Set(TenantContext ctx) => _current = ctx;
        public void Clear() => _current = null;
    }

    private sealed class StubStringLocalizer : IStringLocalizer<PhoneLoginController>
    {
        public LocalizedString this[string name] => new(name, name, resourceNotFound: false);
        public LocalizedString this[string name, params object[] arguments]
            => new(name, string.Format(name, arguments), resourceNotFound: false);
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
            => Array.Empty<LocalizedString>();
    }

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        private readonly Dictionary<string, object> _data = new(StringComparer.Ordinal);
        public IDictionary<string, object> LoadTempData(HttpContext context) => _data;
        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
            _data.Clear();
            foreach (var kv in values)
            {
                _data[kv.Key] = kv.Value;
            }
        }
    }

    private sealed record SignInCall(string UserId, bool IsPersistent);

    private sealed class RecordingSignInManager : ApplicationSignInManager<UserIdentity>
    {
        public List<SignInCall> SignInCalls { get; } = new();

        public RecordingSignInManager(
            UserManager<UserIdentity> userManager,
            IUserClaimsPrincipalFactory<UserIdentity> claimsFactory)
            : base(
                userManager,
                new HttpContextAccessor(),
                Substitute.For<IUserSession>(),
                claimsFactory,
                Microsoft.Extensions.Options.Options.Create(new IdentityOptions()),
                NullLogger<SignInManager<UserIdentity>>.Instance,
                Substitute.For<IAuthenticationSchemeProvider>(),
                Substitute.For<IUserConfirmation<UserIdentity>>())
        {
        }

        public override Task SignInAsync(UserIdentity user, bool isPersistent, string? authenticationMethod = null)
        {
            SignInCalls.Add(new SignInCall(user.Id, isPersistent));
            return Task.CompletedTask;
        }

        public override Task SignInAsync(UserIdentity user, AuthenticationProperties authenticationProperties, string? authenticationMethod = null)
        {
            SignInCalls.Add(new SignInCall(user.Id, authenticationProperties?.IsPersistent ?? false));
            return Task.CompletedTask;
        }

        public override Task SignInWithClaimsAsync(UserIdentity user, AuthenticationProperties authenticationProperties, IEnumerable<Claim> additionalClaims)
        {
            SignInCalls.Add(new SignInCall(user.Id, authenticationProperties?.IsPersistent ?? false));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogProperties> Entries { get; } = new();

        IDisposable ILogger.BeginScope<TState>(TState state) => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var entry = new LogProperties
            {
                Message = formatter(state, exception),
                Level = logLevel,
            };

            if (state is IReadOnlyList<KeyValuePair<string, object?>> structured)
            {
                foreach (var kv in structured)
                {
                    entry.Add(kv.Key, kv.Value);
                }
            }

            Entries.Add(entry);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class LogProperties
    {
        public string Message { get; init; } = string.Empty;
        public LogLevel Level { get; init; }
        private readonly Dictionary<string, object?> _props = new(StringComparer.Ordinal);

        public void Add(string key, object? value) => _props[key] = value;
        public object? GetProperty(string name) => _props.TryGetValue(name, out var v) ? v : null;
    }
}
