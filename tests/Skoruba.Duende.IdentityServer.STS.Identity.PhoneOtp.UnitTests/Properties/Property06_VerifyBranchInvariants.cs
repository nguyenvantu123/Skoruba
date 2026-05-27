// Feature: phone-otp-multi-account-select, Property 6: Post-verify branching invariants
//
// Validates: Requirements 4.2, 4.3, 4.4, 4.6, 6.5, 6.9, 6.10, 7.1, 8.1
//
// Generator: random (Count ∈ {1, 2, 3, 5}, MultiAccount.Enabled flag).
//
// Property (Section 10.3 design, Property 6):
//   * Count == 1 (any flag value): SignInAsync called exactly once,
//     UserLoginSuccessEvent raised once, NO `phone_otp_account_select` cookie set,
//     phone_otp_session cookie cleared (R4.2, R6.5, R7.1).
//   * Count > 1 AND flag == true: NO SignInAsync, NO UserLoginSuccessEvent,
//     phone_otp_account_select cookie set, phone_otp_session cookie cleared
//     (the two cookies never coexist), 302 redirect to /SelectAccount preserving
//     returnUrl (R4.4, R4.6, R6.5, R6.10).
//   * Count > 1 AND flag == false: defensive reject — NO SignInAsync,
//     NO UserLoginSuccessEvent, NO `phone_otp_account_select` cookie set,
//     ViewResult re-rendering Verify with Generic_Verify_Error (R4.3).

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

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

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

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.UnitTests.Properties;

public sealed class Property06_VerifyBranchInvariants
{
    private const string TenantKey = "tenant-a";
    private const string PhoneE164Hash = "f1d2c3b4a5968778695a4b3c2d1e0f00deadbeefcafef00dabcd1234abcd1234";
    private const string PhoneE164 = "+84334336232";
    private const string CorrectOtp = "123456";

    public sealed record BranchScenario(int Count, bool MultiEnabled);

    public static class Arbs
    {
        public static Arbitrary<BranchScenario> Scenario()
            => (from count in Gen.Elements(1, 2, 3, 5)
                from flag in Gen.Elements(true, false)
                select new BranchScenario(count, flag))
               .ToArbitrary();
    }

    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Arbs) })]
    public async Task Verify_Branches_PreservesInvariants(BranchScenario scenario)
    {
        await using var fixture = await ControllerFixture.CreateAsync();

        // Seed Count distinct candidate users in the tenant.
        var candidateIds = new List<string>();
        for (var i = 0; i < scenario.Count; i++)
        {
            var id = $"u-{i}-{Guid.NewGuid():N}";
            candidateIds.Add(id);
            await fixture.SeedUserAsync(id, $"user-{i:D2}");
        }

        // Pretend VerifyAsync succeeds and returns the seeded candidate set.
        fixture.OtpService
            .VerifyAsync(Arg.Any<VerifyOtpRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VerifyOtpResult(
                VerifyOutcome.Succeeded,
                UserId: candidateIds[0],
                AttemptCount: 1,
                CandidateUserIds: candidateIds,
                PhoneE164: PhoneE164));

        var controller = fixture.BuildController(scenario.MultiEnabled);

        var result = await controller.Verify(
            new PhoneVerifyViewModel { Otp = CorrectOtp, ReturnUrl = "/protected/area" },
            CancellationToken.None);

        var setCookies = controller.Response.Headers["Set-Cookie"].ToArray()
            .Where(c => !string.IsNullOrEmpty(c))
            .Cast<string>()
            .ToArray();
        var sessionDeleted = setCookies.Any(c =>
            c.StartsWith(PhoneOtpSessionCookieCodec.CookieName + "=", StringComparison.Ordinal)
            && c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
        var selectCookieSet = setCookies.Any(c =>
            c.StartsWith(PhoneOtpAccountSelectCookieCodec.CookieName + "=", StringComparison.Ordinal)
            && !c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));

        if (scenario.Count == 1)
        {
            // R4.2 — single-user continuation. SignInAsync called once,
            // UserLoginSuccessEvent raised once, NO select cookie, session cleared.
            fixture.SignInManager.SignInCalls.Should().HaveCount(1);
            fixture.SignInManager.SignInCalls.Single().UserId.Should().Be(candidateIds[0]);
            await fixture.Events.Received(1).RaiseAsync(Arg.Any<UserLoginSuccessEvent>());
            selectCookieSet.Should().BeFalse(
                because: "single-candidate branch must not issue phone_otp_account_select cookie");
            sessionDeleted.Should().BeTrue(
                because: "single-candidate branch must clear phone_otp_session cookie");
            result.Should().BeOfType<RedirectResult>();
            return;
        }

        // Count > 1 — branch on flag.
        if (scenario.MultiEnabled)
        {
            // R4.4, R4.6, R6.5, R6.10 — set select cookie, clear session, 302 to chooser.
            fixture.SignInManager.SignInCalls.Should().BeEmpty(
                because: "multi-candidate branch must NOT call SignInAsync");
            await fixture.Events.DidNotReceive().RaiseAsync(Arg.Any<UserLoginSuccessEvent>());
            selectCookieSet.Should().BeTrue(
                because: "multi-candidate branch must set phone_otp_account_select cookie");
            sessionDeleted.Should().BeTrue(
                because: "multi-candidate branch must clear phone_otp_session cookie");
            var redirect = result.Should().BeOfType<RedirectResult>().Which;
            redirect.Url.Should().Be(
                "/Account/LoginWithPhone/SelectAccount?returnUrl="
                + Uri.EscapeDataString("/protected/area"));
            return;
        }

        // Count > 1, flag off — R4.3 defensive reject.
        fixture.SignInManager.SignInCalls.Should().BeEmpty(
            because: "defensive reject must NOT call SignInAsync");
        await fixture.Events.DidNotReceive().RaiseAsync(Arg.Any<UserLoginSuccessEvent>());
        selectCookieSet.Should().BeFalse(
            because: "defensive reject must NOT issue phone_otp_account_select cookie");
        var view = result.Should().BeOfType<ViewResult>().Which;
        view.ViewName.Should().Be("~/Views/Account/LoginWithPhone/Verify.cshtml");
        // Session cookie should be cleared per R4.3 + R6.5 invariant.
        sessionDeleted.Should().BeTrue(
            because: "defensive reject must clear phone_otp_session cookie");
    }

    /// <summary>
    /// Controller fixture mirroring <c>PhoneLoginControllerVerifyBranchTests</c>.
    /// Re-implemented here (rather than referenced) because xUnit / FsCheck
    /// re-instantiate the test class per iteration, and the ASP.NET Core
    /// Identity stack needs a fresh DbContext per scenario to avoid cross-test
    /// state.
    /// </summary>
    private sealed class ControllerFixture : IAsyncDisposable
    {
        public IPhoneOtpService OtpService { get; }
        public IEventService Events { get; }
        public RecordingSignInManager SignInManager { get; }

        private readonly ServiceProvider _root;
        private readonly IServiceScope _scope;
        private readonly AdminIdentityDbContext _db;
        private readonly UserManager<UserIdentity> _userManager;
        private readonly EphemeralDataProtectionProvider _dp;
        private readonly PhoneOtpSessionCookieCodec _sessionCodec;
        private readonly PhoneOtpAccountSelectCookieCodec _selectCodec;
        private readonly TestTimeProvider _time;
        private readonly TestTenantContextAccessor _tenant;

        private ControllerFixture(
            ServiceProvider root,
            IServiceScope scope,
            AdminIdentityDbContext db,
            UserManager<UserIdentity> um,
            EphemeralDataProtectionProvider dp,
            PhoneOtpSessionCookieCodec session,
            PhoneOtpAccountSelectCookieCodec select,
            TestTimeProvider time,
            TestTenantContextAccessor tenant,
            IPhoneOtpService otp,
            IEventService events,
            RecordingSignInManager sm)
        {
            _root = root;
            _scope = scope;
            _db = db;
            _userManager = um;
            _dp = dp;
            _sessionCodec = session;
            _selectCodec = select;
            _time = time;
            _tenant = tenant;
            OtpService = otp;
            Events = events;
            SignInManager = sm;
        }

        public static async Task<ControllerFixture> CreateAsync()
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

            var root = services.BuildServiceProvider();
            var scope = root.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AdminIdentityDbContext>();
            var um = scope.ServiceProvider.GetRequiredService<UserManager<UserIdentity>>();

            var dp = new EphemeralDataProtectionProvider();
            var session = new PhoneOtpSessionCookieCodec(dp);
            var select = new PhoneOtpAccountSelectCookieCodec(dp);
            var time = new TestTimeProvider(new DateTimeOffset(2025, 1, 5, 8, 5, 0, TimeSpan.Zero));
            var tenant = new TestTenantContextAccessor(
                new TenantContext(TenantKey, new Dictionary<string, string>()));

            var sm = new RecordingSignInManager(
                um,
                scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<UserIdentity>>());

            await Task.CompletedTask;
            return new ControllerFixture(
                root, scope, db, um, dp, session, select, time, tenant,
                otp: Substitute.For<IPhoneOtpService>(),
                events: Substitute.For<IEventService>(),
                sm);
        }

        public async Task SeedUserAsync(string id, string userName)
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
            _db.Users.Add(user);
            await _db.SaveChangesAsync();
        }

        public PhoneLoginController BuildController(bool multiEnabled)
        {
            var config = new PhoneOtpLoginConfiguration
            {
                Enabled = true,
                DefaultRegion = "VN",
                OtpLength = 6,
                OtpTtlSeconds = 300,
                MultiAccount = new MultiAccountConfiguration
                {
                    Enabled = multiEnabled,
                    SelectTtlSeconds = 60,
                },
            };

            var interaction = Substitute.For<IIdentityServerInteractionService>();
            interaction
                .GetAuthorizationContextAsync(Arg.Any<string?>())
                .Returns(Task.FromResult<AuthorizationRequest?>(null));

            var controller = new PhoneLoginController(
                phoneOtpService: OtpService,
                cookieCodec: _sessionCodec,
                normalizer: new PhoneNumberNormalizer(),
                tenantContextAccessor: _tenant,
                signInManager: SignInManager,
                userManager: _userManager,
                interaction: interaction,
                events: Events,
                antiBot: new NoopPhoneOtpAntiBotChallenge(),
                store: Substitute.For<IPhoneOtpStore>(),
                options: Options.Create(config),
                logger: NullLogger<PhoneLoginController>.Instance,
                localizer: new StubStringLocalizer(),
                timeProvider: _time,
                selectCodec: _selectCodec,
                tokenProtector: new SelectionTokenProtector(_dp));

            var httpContext = new DefaultHttpContext
            {
                RequestServices = _scope.ServiceProvider,
            };

            var sessionPayload = new SessionCookiePayload(
                TenantKey: TenantKey,
                PhoneE164Hash: PhoneE164Hash,
                ExpiresAtUtc: _time.GetUtcNow().AddSeconds(300));
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

        public async ValueTask DisposeAsync()
        {
            _scope.Dispose();
            await _root.DisposeAsync();
        }
    }

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
            foreach (var kv in values) _data[kv.Key] = kv.Value;
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
}
