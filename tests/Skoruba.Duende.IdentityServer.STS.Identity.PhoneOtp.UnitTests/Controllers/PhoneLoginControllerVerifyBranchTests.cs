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
using Skoruba.Duende.IdentityServer.STS.Identity.ViewModels.Account;

using TenantInfrastructure.Abstractions;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.UnitTests.Controllers;

/// <summary>
/// Unit tests for the post-verify branching introduced in Task 9 (Section 4.5
/// design). Each test exercises one of the three branches in
/// <see cref="PhoneLoginController.Verify(PhoneVerifyViewModel, CancellationToken)"/>:
/// <list type="bullet">
///   <item>Single candidate (Count == 1) — preserves the legacy single-user
///   continuation (R4.2, R14.1, R14.3).</item>
///   <item>Multiple candidates with <c>MultiAccount.Enabled = true</c> — sets
///   <c>phone_otp_account_select</c>, clears <c>phone_otp_session</c>, redirects
///   to the chooser (R4.4, R6.1, R6.4, R6.5, R10.2).</item>
///   <item>Multiple candidates with <c>MultiAccount.Enabled = false</c> —
///   defensive reject with Generic_Verify_Error (R4.3).</item>
/// </list>
/// Validates: Requirements 4.1, 4.2, 4.3, 4.4, 4.5, 4.6, 6.1, 6.4, 6.5, 10.2,
/// 14.1, 14.3.
/// </summary>
public sealed class PhoneLoginControllerVerifyBranchTests : IDisposable
{
    private const string TenantKey = "tenant-a";
    private const string PhoneE164Hash = "f1d2c3b4a5968778695a4b3c2d1e0f00deadbeefcafef00dabcd1234abcd1234";
    private const string PhoneE164 = "+84334336232";
    private const string CorrectOtp = "123456";

    private readonly ServiceProvider _rootProvider;
    private readonly IServiceScope _scope;
    private readonly AdminIdentityDbContext _dbContext;
    private readonly UserManager<UserIdentity> _userManager;
    private readonly EphemeralDataProtectionProvider _dataProtection;
    private readonly PhoneOtpSessionCookieCodec _sessionCodec;
    private readonly PhoneOtpAccountSelectCookieCodec _selectCodec;
    private readonly TestTimeProvider _timeProvider;
    private readonly TestTenantContextAccessor _tenant;
    private readonly StubStringLocalizer _localizer;
    private readonly RecordingLogger<PhoneLoginController> _logger;
    private readonly IPhoneOtpService _otpService;
    private readonly IEventService _events;
    private readonly RecordingSignInManager _signInManager;

    public PhoneLoginControllerVerifyBranchTests()
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
        _sessionCodec = new PhoneOtpSessionCookieCodec(_dataProtection);
        _selectCodec = new PhoneOtpAccountSelectCookieCodec(_dataProtection);
        _timeProvider = new TestTimeProvider(new DateTimeOffset(2025, 1, 5, 8, 5, 0, TimeSpan.Zero));
        _tenant = new TestTenantContextAccessor(new TenantContext(TenantKey, new Dictionary<string, string>()));
        _localizer = new StubStringLocalizer();
        _logger = new RecordingLogger<PhoneLoginController>();
        _otpService = Substitute.For<IPhoneOtpService>();
        _events = Substitute.For<IEventService>();
        _signInManager = new RecordingSignInManager(_userManager,
            _scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<UserIdentity>>());
    }

    public void Dispose()
    {
        _scope.Dispose();
        _rootProvider.Dispose();
    }

    private async Task SeedUserAsync(string id, string userName)
    {
        var user = new UserIdentity
        {
            Id = id,
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Email = $"{userName}@example.com",
            NormalizedEmail = $"{userName}@example.com".ToUpperInvariant(),
            PhoneNumber = PhoneE164,
            PhoneNumberConfirmed = true,
            TenantKey = TenantKey,
            SecurityStamp = Guid.NewGuid().ToString(),
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();
    }

    private PhoneLoginController BuildController(MultiAccountConfiguration multi)
    {
        var config = new PhoneOtpLoginConfiguration
        {
            Enabled = true,
            DefaultRegion = "VN",
            OtpLength = 6,
            OtpTtlSeconds = 300,
            MultiAccount = multi,
        };

        var interaction = Substitute.For<IIdentityServerInteractionService>();
        interaction
            .GetAuthorizationContextAsync(Arg.Any<string?>())
            .Returns(Task.FromResult<AuthorizationRequest?>(null));

        var controller = new PhoneLoginController(
            phoneOtpService: _otpService,
            cookieCodec: _sessionCodec,
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
            selectCodec: _selectCodec,
            tokenProtector: new SelectionTokenProtector(_dataProtection));

        var httpContext = new DefaultHttpContext
        {
            RequestServices = _scope.ServiceProvider,
        };

        // Wire valid phone_otp_session cookie so TryReadAndValidateSessionCookie passes.
        var sessionPayload = new SessionCookiePayload(
            TenantKey: TenantKey,
            PhoneE164Hash: PhoneE164Hash,
            ExpiresAtUtc: _timeProvider.GetUtcNow().AddSeconds(300));
        var sessionRaw = _sessionCodec.Protect(sessionPayload);
        httpContext.Request.Headers["Cookie"] =
            $"{PhoneOtpSessionCookieCodec.CookieName}={sessionRaw}";

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

    // ----------------------------------------------------------------------
    // Branch 1: single candidate → SignInAsync + UserLoginSuccessEvent
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Count1_FlagOn_SignsInDirectly_NoSelectCookie()
    {
        // R4.2 — single candidate path: SignInAsync, UserLoginSuccessEvent, 302
        // continuation (~/), không set phone_otp_account_select cookie, không có
        // log entry PhoneOtpAccountSelectShown.
        await SeedUserAsync("u-1", "alice");
        _otpService
            .VerifyAsync(Arg.Any<VerifyOtpRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VerifyOtpResult(
                VerifyOutcome.Succeeded, "u-1", AttemptCount: 1,
                CandidateUserIds: new[] { "u-1" },
                PhoneE164: PhoneE164));

        var controller = BuildController(new MultiAccountConfiguration { Enabled = true, SelectTtlSeconds = 60 });

        var result = await controller.Verify(
            new PhoneVerifyViewModel { Otp = CorrectOtp, ReturnUrl = null },
            CancellationToken.None);

        // Continuation: GetAuthorizationContextAsync = null + IsLocalUrl = false +
        // returnUrl null → Redirect("~/").
        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("~/");

        // SignInAsync invoked exactly once với u-1.
        _signInManager.SignInCalls.Should().HaveCount(1);
        _signInManager.SignInCalls.Single().UserId.Should().Be("u-1");

        // UserLoginSuccessEvent raised.
        await _events.Received(1).RaiseAsync(Arg.Any<UserLoginSuccessEvent>());

        // No phone_otp_account_select cookie set.
        var setCookies = controller.Response.Headers["Set-Cookie"].ToString();
        setCookies.Should().NotContain(PhoneOtpAccountSelectCookieCodec.CookieName);

        // No PhoneOtpAccountSelectShown log entry.
        _logger.Entries.Should().NotContain(e => e.Message.Contains("PhoneOtpAccountSelectShown"));
    }

    // ----------------------------------------------------------------------
    // Branch 2: many candidates + flag on → 302 SelectAccount, set cookie, clear session
    // ----------------------------------------------------------------------

    [Fact]
    public async Task CountMany_FlagOn_RedirectsToSelectAccount_SetsCookie_ClearsSession_LogsEvent()
    {
        // R4.4, R4.5, R4.6, R6.1, R6.4, R6.5, R10.2 — multi-account path.
        await SeedUserAsync("u-1", "alice");
        await SeedUserAsync("u-2", "bob");
        await SeedUserAsync("u-3", "carol");
        var candidateIds = new[] { "u-1", "u-2", "u-3" };

        _otpService
            .VerifyAsync(Arg.Any<VerifyOtpRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VerifyOtpResult(
                VerifyOutcome.Succeeded, "u-1", AttemptCount: 1,
                CandidateUserIds: candidateIds,
                PhoneE164: PhoneE164));

        var controller = BuildController(new MultiAccountConfiguration { Enabled = true, SelectTtlSeconds = 60 });

        var result = await controller.Verify(
            new PhoneVerifyViewModel { Otp = CorrectOtp, ReturnUrl = "/protected/area" },
            CancellationToken.None);

        // 302 to /Account/LoginWithPhone/SelectAccount?returnUrl=...
        var redirect = result.Should().BeOfType<RedirectResult>().Which;
        redirect.Url.Should().Be(
            "/Account/LoginWithPhone/SelectAccount?returnUrl="
            + Uri.EscapeDataString("/protected/area"));

        // phone_otp_account_select cookie set với protected payload.
        var setCookies = controller.Response.Headers["Set-Cookie"]
            .ToArray();
        var selectCookie = setCookies.FirstOrDefault(c =>
            c.StartsWith(PhoneOtpAccountSelectCookieCodec.CookieName + "=")
            && !c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
        selectCookie.Should().NotBeNull("phone_otp_account_select cookie should be set");
        selectCookie!.Should().Contain("httponly", because: "R6.1 requires HttpOnly");
        selectCookie!.Should().Contain("secure", because: "R6.1 requires Secure");
        selectCookie!.Should().Contain("samesite=lax", because: "R6.1 requires SameSite=Lax");

        // Decode cookie và verify CandidateUserIds locked-in.
        var raw = ExtractCookieValue(selectCookie!, PhoneOtpAccountSelectCookieCodec.CookieName);
        _selectCodec.TryUnprotect(raw, out var ctx).Should().BeTrue();
        ctx.TenantKey.Should().Be(TenantKey);
        ctx.PhoneE164Hash.Should().Be(PhoneE164Hash);
        ctx.CandidateUserIds.Should().Equal(candidateIds);
        ctx.IssuedAtUtc.Should().Be(_timeProvider.GetUtcNow());
        ctx.ExpiresAtUtc.Should().Be(_timeProvider.GetUtcNow().AddSeconds(60));
        ctx.OtpRecordKey.Should().Be($"{TenantKey}:{PhoneE164Hash}");
        ctx.Version.Should().Be(1);

        // phone_otp_session cookie cleared (R6.5) — Set-Cookie với expires past.
        setCookies.Should().Contain(c =>
            c.StartsWith(PhoneOtpSessionCookieCodec.CookieName + "=")
            && c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));

        // SignInAsync NOT invoked, UserLoginSuccessEvent NOT raised.
        _signInManager.SignInCalls.Should().BeEmpty();
        await _events.DidNotReceive().RaiseAsync(Arg.Any<UserLoginSuccessEvent>());

        // PhoneOtpAccountSelectShown log entry với CandidateCount=3.
        var logEntry = _logger.Entries.Should().ContainSingle(e =>
            e.Message.Contains("PhoneOtpAccountSelectShown")).Which;
        logEntry.GetProperty("CandidateCount").Should().Be(3);
        logEntry.GetProperty("TenantKey").Should().Be(TenantKey);
        logEntry.Level.Should().Be(LogLevel.Information);

        // TempData "PhoneOtpMaskedPhone" populated từ MaskLast4(PhoneE164).
        controller.TempData["PhoneOtpMaskedPhone"].Should().NotBeNull();
        ((string)controller.TempData["PhoneOtpMaskedPhone"]!).Should().EndWith("6232");
    }

    // ----------------------------------------------------------------------
    // Branch 3: many candidates + flag off → defensive reject
    // ----------------------------------------------------------------------

    [Fact]
    public async Task CountMany_FlagOff_DefensiveRejectsWithGenericError()
    {
        // R4.3 — multi-candidate record while flag off (race condition / flag rotation):
        // re-render Verify với Generic_Verify_Error, KHÔNG SignInAsync, KHÔNG raise
        // UserLoginSuccessEvent, phone_otp_session cookie cleared.
        await SeedUserAsync("u-1", "alice");
        await SeedUserAsync("u-2", "bob");

        _otpService
            .VerifyAsync(Arg.Any<VerifyOtpRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VerifyOtpResult(
                VerifyOutcome.Succeeded, "u-1", AttemptCount: 1,
                CandidateUserIds: new[] { "u-1", "u-2" },
                PhoneE164: PhoneE164));

        var controller = BuildController(new MultiAccountConfiguration { Enabled = false });

        var result = await controller.Verify(
            new PhoneVerifyViewModel { Otp = CorrectOtp, ReturnUrl = "/x" },
            CancellationToken.None);

        // Re-render Verify với GenericError (ViewResult, not Redirect).
        var view = result.Should().BeOfType<ViewResult>().Which;
        view.ViewName.Should().Be("~/Views/Account/LoginWithPhone/Verify.cshtml");
        // Generic_Verify_Error wired vào ViewData.
        view.ViewData["PhoneOtpVerifyError"].Should().Be("Generic_Verify_Error");

        // No SignInAsync, no UserLoginSuccessEvent.
        _signInManager.SignInCalls.Should().BeEmpty();
        await _events.DidNotReceive().RaiseAsync(Arg.Any<UserLoginSuccessEvent>());

        // No phone_otp_account_select cookie set.
        var setCookies = controller.Response.Headers["Set-Cookie"].ToArray();
        setCookies.Should().NotContain(c =>
            c.StartsWith(PhoneOtpAccountSelectCookieCodec.CookieName + "=")
            && !c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));

        // phone_otp_session cookie cleared (R4.3 + R6.5 invariant).
        setCookies.Should().Contain(c =>
            c.StartsWith(PhoneOtpSessionCookieCodec.CookieName + "=")
            && c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));

        // Warning log entry chỉ ra defensive reject.
        var warning = _logger.Entries.Should().ContainSingle(e =>
            e.Message.Contains("defensive reject")).Which;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.GetProperty("CandidateCount").Should().Be(2);
    }

    // ----------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------

    private static string ExtractCookieValue(string setCookieHeader, string cookieName)
    {
        // setCookieHeader = "<name>=<value>; path=/; ..."
        var prefix = cookieName + "=";
        var startIndex = setCookieHeader.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        var endIndex = setCookieHeader.IndexOf(';', startIndex);
        if (endIndex < 0) endIndex = setCookieHeader.Length;
        return setCookieHeader.Substring(startIndex, endIndex - startIndex);
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

    /// <summary>
    /// Test double for <see cref="ApplicationSignInManager{TUser}"/> that records
    /// invocations of <c>SignInAsync(user, isPersistent)</c> without touching
    /// <c>HttpContext.AuthenticateAsync</c> or
    /// <see cref="Duende.IdentityServer.Services.IUserSession"/>. The base class
    /// requires construction with full Identity infrastructure, which is more
    /// than this unit test needs.
    /// </summary>
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
