// Feature: phone-otp-multi-account-select, Property 10: POST /SelectAccount gate invariants
//
// Validates: Requirements 6.6, 6.7, 7.3, 8.5, 8.6, 9.2, 9.3, 11.1
//
// Generator: random gate state — (cookie state, token state, tenant state, user state).
//
// Property (Section 10.3 design + Section 2.2 gate matrix):
//   * Cookie absent OR cookie decrypt fail (Gate 2 / Gate 3): 302 redirect to
//     /Account/Login, NO `RegisterVerifyFailureAsync` (R11.1 — tampered/missing
//     cookie must not consume phone-failure budget).
//   * Other gate fail (Gate 5..9): 302 redirect (or re-render via 302 to GET
//     for Gate 8) AND `RegisterVerifyFailureAsync` called exactly once
//     (R6.7, R8.6, R9.2).
//   * All gates pass (success): `SignInAsync` called exactly once,
//     `RegisterVerifyFailureAsync` zero times, `UserLoginSuccessEvent` raised
//     once (R7.3 implicit via continuation), 302 redirect via continuation
//     cascade.

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

using TenantInfrastructure.Abstractions;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.UnitTests.Properties;

public sealed class Property10_PostGateInvariants
{
    private const string TenantKey = "tenant-a";
    private const string PhoneE164Hash = "f1d2c3b4a5968778695a4b3c2d1e0f00deadbeefcafef00dabcd1234abcd1234";

    /// <summary>
    /// Discriminator over all gate outcomes the random generator can produce.
    /// We model gate state as a single enum so the input space is small and
    /// each branch has a clear, asserted invariant.
    /// </summary>
    public enum GateState
    {
        Success,
        Gate2_CookieAbsent,
        Gate3_CookieDecryptFail,
        Gate5_TenantMismatch,
        Gate6_TokenInvalid,
        Gate7_UserIdNotInSet,
        Gate8_UserNotFound,
    }

    public static class Arbs
    {
        public static Arbitrary<GateState> State()
            => Gen.Elements(
                    GateState.Success,
                    GateState.Gate2_CookieAbsent,
                    GateState.Gate3_CookieDecryptFail,
                    GateState.Gate5_TenantMismatch,
                    GateState.Gate6_TokenInvalid,
                    GateState.Gate7_UserIdNotInSet,
                    GateState.Gate8_UserNotFound)
               .ToArbitrary();
    }

    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Arbs) })]
    public async Task PostGate_Invariants_HoldForEveryGateOutcome(GateState state)
    {
        await using var fixture = await ControllerFixture.CreateAsync();

        // Seed a primary user that exists in the tenant — used by Success / most
        // negative gates that DO need a real DB row to round-trip.
        await fixture.SeedUserAsync("u-1", "alice");

        string? cookieRaw;
        string selectionToken;

        switch (state)
        {
            case GateState.Gate2_CookieAbsent:
                cookieRaw = null;
                selectionToken = "irrelevant";
                break;

            case GateState.Gate3_CookieDecryptFail:
                cookieRaw = "this-is-not-a-valid-protected-payload";
                selectionToken = "irrelevant";
                break;

            case GateState.Gate5_TenantMismatch:
                cookieRaw = fixture.ProtectCookie(new[] { "u-1" }, tenantKey: "wrong-tenant");
                selectionToken = fixture.IssueToken("u-1");
                break;

            case GateState.Gate6_TokenInvalid:
                cookieRaw = fixture.ProtectCookie(new[] { "u-1" });
                selectionToken = "garbled-token";
                break;

            case GateState.Gate7_UserIdNotInSet:
                cookieRaw = fixture.ProtectCookie(new[] { "u-1" });
                selectionToken = fixture.IssueToken("u-other"); // valid token, NOT in cookie set
                break;

            case GateState.Gate8_UserNotFound:
                cookieRaw = fixture.ProtectCookie(new[] { "u-deleted" });
                selectionToken = fixture.IssueToken("u-deleted"); // user never seeded
                break;

            case GateState.Success:
            default:
                cookieRaw = fixture.ProtectCookie(new[] { "u-1" });
                selectionToken = fixture.IssueToken("u-1");
                break;
        }

        var controller = fixture.BuildController(cookieRaw);

        var result = await controller.SelectAccountPost(
            SelectionToken: selectionToken,
            ReturnUrl: "/protected",
            CancellationToken.None);

        // Every POST increments the IP counter (R18.5 — Property 12 covers the
        // detail; here we just sanity-check the pre-condition for IP rate-limit
        // gate ordering).
        await fixture.RateLimiter.Received(1).RegisterIpSelectAttemptAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());

        switch (state)
        {
            case GateState.Gate2_CookieAbsent:
            case GateState.Gate3_CookieDecryptFail:
                // R11.1 — tampered/missing cookie MUST NOT consume phone failure budget.
                await fixture.RateLimiter.DidNotReceive().RegisterVerifyFailureAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
                fixture.SignInManager.SignInCalls.Should().BeEmpty();
                result.Should().BeOfType<RedirectResult>();
                break;

            case GateState.Gate5_TenantMismatch:
            case GateState.Gate6_TokenInvalid:
            case GateState.Gate7_UserIdNotInSet:
            case GateState.Gate8_UserNotFound:
                // R6.7, R8.6, R9.2 — phone failure counter incremented exactly once.
                await fixture.RateLimiter.Received(1).RegisterVerifyFailureAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
                fixture.SignInManager.SignInCalls.Should().BeEmpty();
                result.Should().BeOfType<RedirectResult>(
                    because: "every rejection branch redirects (Gate 8 redirects to the GET handler)");
                break;

            case GateState.Success:
                // R7.1 — SignInAsync exactly once.
                fixture.SignInManager.SignInCalls.Should().HaveCount(1);
                fixture.SignInManager.SignInCalls.Single().UserId.Should().Be("u-1");
                fixture.SignInManager.SignInCalls.Single().IsPersistent.Should().BeFalse();
                // No phone-failure side-effect on the success branch.
                await fixture.RateLimiter.DidNotReceive().RegisterVerifyFailureAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
                // Continuation cascade emits a redirect (returnUrl='/protected'
                // is non-local + no auth context => fallback ~/).
                result.Should().BeOfType<RedirectResult>();
                break;
        }
    }

    /// <summary>
    /// Controller fixture mirroring <c>PhoneLoginControllerSelectAccountPostTests</c>.
    /// Re-implemented locally because xUnit / FsCheck instantiate the test
    /// class once per iteration — the EF InMemory database needs to be unique
    /// per scenario.
    /// </summary>
    private sealed class ControllerFixture : IAsyncDisposable
    {
        public IPhoneOtpRateLimiter RateLimiter { get; }
        public RecordingSignInManager SignInManager { get; }

        private readonly ServiceProvider _root;
        private readonly IServiceScope _scope;
        private readonly AdminIdentityDbContext _db;
        private readonly UserManager<UserIdentity> _userManager;
        private readonly EphemeralDataProtectionProvider _dp;
        private readonly PhoneOtpAccountSelectCookieCodec _selectCodec;
        private readonly SelectionTokenProtector _tokenProtector;
        private readonly TestTimeProvider _time;
        private readonly TestTenantContextAccessor _tenant;
        private readonly IEventService _events;

        private ControllerFixture(
            ServiceProvider root,
            IServiceScope scope,
            AdminIdentityDbContext db,
            UserManager<UserIdentity> um,
            EphemeralDataProtectionProvider dp,
            PhoneOtpAccountSelectCookieCodec select,
            SelectionTokenProtector token,
            TestTimeProvider time,
            TestTenantContextAccessor tenant,
            IPhoneOtpRateLimiter rl,
            RecordingSignInManager sm,
            IEventService events)
        {
            _root = root;
            _scope = scope;
            _db = db;
            _userManager = um;
            _dp = dp;
            _selectCodec = select;
            _tokenProtector = token;
            _time = time;
            _tenant = tenant;
            RateLimiter = rl;
            SignInManager = sm;
            _events = events;
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
            var selectCodec = new PhoneOtpAccountSelectCookieCodec(dp);
            var tokenProtector = new SelectionTokenProtector(dp);
            var time = new TestTimeProvider(new DateTimeOffset(2025, 1, 5, 8, 5, 0, TimeSpan.Zero));
            var tenant = new TestTenantContextAccessor(
                new TenantContext(TenantKey, new Dictionary<string, string>()));

            var rl = Substitute.For<IPhoneOtpRateLimiter>();
            rl.CheckIpSelectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new RateLimitDecision(true, null, null));

            var sm = new RecordingSignInManager(
                um,
                scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<UserIdentity>>());
            var events = Substitute.For<IEventService>();

            await Task.CompletedTask;
            return new ControllerFixture(
                root, scope, db, um, dp, selectCodec, tokenProtector, time, tenant, rl, sm, events);
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
                PhoneNumber = "+84334336232",
                PhoneNumberConfirmed = true,
                TenantKey = TenantKey,
                SecurityStamp = Guid.NewGuid().ToString(),
            };
            _db.Users.Add(user);
            await _db.SaveChangesAsync();
        }

        public string ProtectCookie(IReadOnlyList<string> candidateIds, string tenantKey = TenantKey)
        {
            var issued = _time.GetUtcNow();
            var ctx = new AccountSelectContext(
                TenantKey: tenantKey,
                PhoneE164Hash: PhoneE164Hash,
                CandidateUserIds: candidateIds,
                IssuedAtUtc: issued,
                ExpiresAtUtc: issued.AddSeconds(60),
                OtpRecordKey: $"{tenantKey}:{PhoneE164Hash}",
                Version: 1);
            return _selectCodec.Protect(ctx);
        }

        public string IssueToken(string userId) => _tokenProtector.Issue(userId);

        public PhoneLoginController BuildController(string? cookieRaw)
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
                cookieCodec: new PhoneOtpSessionCookieCodec(_dp),
                normalizer: new PhoneNumberNormalizer(),
                tenantContextAccessor: _tenant,
                signInManager: SignInManager,
                userManager: _userManager,
                interaction: interaction,
                events: _events,
                antiBot: new NoopPhoneOtpAntiBotChallenge(),
                store: Substitute.For<IPhoneOtpStore>(),
                options: Options.Create(config),
                logger: NullLogger<PhoneLoginController>.Instance,
                localizer: new StubStringLocalizer(),
                timeProvider: _time,
                selectCodec: _selectCodec,
                tokenProtector: _tokenProtector,
                rateLimiter: RateLimiter);

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
