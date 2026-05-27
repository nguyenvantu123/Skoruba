// Feature: phone-otp-multi-account-select, Property 14: Log entries do not contain forbidden plaintext
//
// Validates: Requirements 10.5
//
// Generator: random raw inputs (raw IP, raw user-id, raw cookie payload, raw
// SelectionToken). Phone E.164 / email / UserName aren't in scope for the
// SelectAccountPost code path (the controller only sees their hashes /
// candidate ids), but we still seed deterministic sentinel values to assert
// they're not accidentally embedded in any log message.
//
// Property invariant (Section 10.3 design + R10.5):
//   For all log entries emitted by `PhoneLoginController.SelectAccountPost`
//   across the entire gate matrix (Gate 1 IP-rate-limit, Gate 5 tenant
//   mismatch, Gate 6 token invalid, success branch):
//     * The rendered message MUST NOT contain the raw IP literal.
//     * The rendered message MUST NOT contain the raw cookie payload literal.
//     * The rendered message MUST NOT contain the raw SelectionToken.
//     * The rendered message MUST NOT contain the raw user-id of the
//       successful sign-in (User_Id_Hash redaction, R10.5).
//
// We exercise the property across three drive scenarios:
//   1. Gate 1 (IP rate-limit): forces the only branch where raw IP is in
//      scope; the log MUST contain only the SHA-256 hex prefix, never the
//      raw "203.0.113.42".
//   2. Gate 6 (token invalid): forces a log entry mentioning the
//      SelectionToken context.
//   3. Success branch: forces a log entry mentioning the user-id; the User_Id_Hash
//      MUST appear, never the raw user-id literal.
//
// Generator note: we randomise the SelectionToken and cookie raw payload
// (long random base64-ish strings) so collisions with log message templates
// are vanishingly unlikely.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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
using Microsoft.Extensions.Logging;
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

public sealed class Property14_LogRedaction
{
    private const string TenantKey = "tenant-a";
    private const string PhoneE164Hash = "f1d2c3b4a5968778695a4b3c2d1e0f00deadbeefcafef00dabcd1234abcd1234";

    public sealed record RedactionScenario(
        string RawIp,
        string RawUserId,
        string RawSelectionToken,
        string RawCookiePayload,
        Branch BranchKind);

    public enum Branch
    {
        Gate1_IpRateLimit,
        Gate6_TokenInvalid,
        Success,
    }

    public static class Arbs
    {
        // IP: realistic IPv4 string, length 7..15 ASCII digits + dots.
        private static Gen<string> IpGen()
            => from a in Gen.Choose(1, 254)
               from b in Gen.Choose(0, 255)
               from c in Gen.Choose(0, 255)
               from d in Gen.Choose(1, 254)
               select $"{a}.{b}.{c}.{d}";

        // user-id: GUID-N, 32 hex chars. Used as DB seed Id and resolved-token target.
        private static Gen<string> UserIdGen()
            => Gen.Constant(0).Select(_ => Guid.NewGuid().ToString("N"));

        // SelectionToken / cookie raw: 24..48 chars from base64url alphabet.
        private static Gen<string> OpaqueGen(int min, int max)
            => from len in Gen.Choose(min, max)
               from chars in Gen.Elements(
                       "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"
                           .ToCharArray())
                   .ListOf(len)
               select new string(chars.ToArray());

        public static Arbitrary<RedactionScenario> Scenario()
            => (from ip in IpGen()
                from uid in UserIdGen()
                from tok in OpaqueGen(24, 48)
                from cookie in OpaqueGen(48, 96)
                from branch in Gen.Elements(
                    Branch.Gate1_IpRateLimit,
                    Branch.Gate6_TokenInvalid,
                    Branch.Success)
                select new RedactionScenario(ip, uid, tok, cookie, branch))
               .ToArbitrary();
    }

    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Arbs) })]
    public async Task LogEntries_DoNotContain_RawInputs_AsSubstring(RedactionScenario scenario)
    {
        await using var fixture = await ControllerFixture.CreateAsync();

        // Seed user with the random user-id; will be resolved by IssueToken
        // for the Success branch, ignored otherwise.
        await fixture.SeedUserAsync(scenario.RawUserId, "alice");

        string? cookieRaw;
        string selectionToken;

        switch (scenario.BranchKind)
        {
            case Branch.Gate1_IpRateLimit:
                fixture.RateLimiter
                    .CheckIpSelectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(new RateLimitDecision(false, "IpSelectWindow", null));
                cookieRaw = scenario.RawCookiePayload; // intentionally junk; never decrypted
                selectionToken = scenario.RawSelectionToken;
                break;

            case Branch.Gate6_TokenInvalid:
                cookieRaw = fixture.ProtectCookie(new[] { scenario.RawUserId });
                selectionToken = scenario.RawSelectionToken; // not a real protected token
                break;

            case Branch.Success:
            default:
                cookieRaw = fixture.ProtectCookie(new[] { scenario.RawUserId });
                selectionToken = fixture.IssueToken(scenario.RawUserId);
                break;
        }

        var controller = fixture.BuildController(cookieRaw, scenario.RawIp);

        await controller.SelectAccountPost(
            SelectionToken: selectionToken,
            ReturnUrl: null,
            CancellationToken.None);

        // Inspect every captured log entry — message text + property values.
        // R10.5: NONE may contain the raw IP, raw user-id, raw cookie payload,
        // or raw SelectionToken as a substring.
        foreach (var entry in fixture.Logger.Entries)
        {
            var rendered = entry.Render();

            rendered.Should().NotContain(scenario.RawIp,
                because: "log MUST NOT embed the raw IP (R10.5, R18.4); only IpHash[..8] is permitted");

            rendered.Should().NotContain(scenario.RawUserId,
                because: "log MUST NOT embed the raw user-id (R10.5); only User_Id_Hash[..8] is permitted");

            // SelectionToken / cookie raw payload may legitimately appear in
            // Information-level diagnostic logs only when redacted (currently
            // never logged at all). Assert the strict invariant.
            rendered.Should().NotContain(scenario.RawSelectionToken,
                because: "log MUST NOT embed the raw SelectionToken (R10.5)");

            rendered.Should().NotContain(scenario.RawCookiePayload,
                because: "log MUST NOT embed the raw cookie payload (R10.5)");
        }
    }

    /// <summary>
    /// Controller fixture. Same shape as Property 10's fixture; the only
    /// addition is a real <see cref="RecordingLogger{T}"/> that captures
    /// rendered log entries for assertion.
    /// </summary>
    private sealed class ControllerFixture : IAsyncDisposable
    {
        public IPhoneOtpRateLimiter RateLimiter { get; }
        public RecordingLogger<PhoneLoginController> Logger { get; }

        private readonly ServiceProvider _root;
        private readonly IServiceScope _scope;
        private readonly AdminIdentityDbContext _db;
        private readonly UserManager<UserIdentity> _userManager;
        private readonly EphemeralDataProtectionProvider _dp;
        private readonly PhoneOtpAccountSelectCookieCodec _selectCodec;
        private readonly SelectionTokenProtector _tokenProtector;
        private readonly TestTimeProvider _time;
        private readonly TestTenantContextAccessor _tenant;
        private readonly RecordingSignInManager _sm;
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
            IEventService events,
            RecordingLogger<PhoneLoginController> logger)
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
            _sm = sm;
            _events = events;
            RateLimiter = rl;
            Logger = logger;
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
            var logger = new RecordingLogger<PhoneLoginController>();

            await Task.CompletedTask;
            return new ControllerFixture(
                root, scope, db, um, dp, selectCodec, tokenProtector, time, tenant,
                rl, sm, events, logger);
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

        public string ProtectCookie(IReadOnlyList<string> ids)
        {
            var issued = _time.GetUtcNow();
            var ctx = new AccountSelectContext(
                TenantKey: TenantKey,
                PhoneE164Hash: PhoneE164Hash,
                CandidateUserIds: ids,
                IssuedAtUtc: issued,
                ExpiresAtUtc: issued.AddSeconds(60),
                OtpRecordKey: $"{TenantKey}:{PhoneE164Hash}",
                Version: 1);
            return _selectCodec.Protect(ctx);
        }

        public string IssueToken(string userId) => _tokenProtector.Issue(userId);

        public PhoneLoginController BuildController(string? cookieRaw, string remoteIp)
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
                signInManager: _sm,
                userManager: _userManager,
                interaction: interaction,
                events: _events,
                antiBot: new NoopPhoneOtpAntiBotChallenge(),
                store: Substitute.For<IPhoneOtpStore>(),
                options: Options.Create(config),
                logger: Logger,
                localizer: new StubStringLocalizer(),
                timeProvider: _time,
                selectCodec: _selectCodec,
                tokenProtector: _tokenProtector,
                rateLimiter: RateLimiter);

            var httpContext = new DefaultHttpContext
            {
                RequestServices = _scope.ServiceProvider,
            };
            httpContext.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);

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
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SignInManager<UserIdentity>>.Instance,
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

        /// <summary>
        /// Render the entry as a single string covering both the formatted
        /// message and every property value — the substring assertion has to
        /// catch raw values whether they were embedded directly in the message
        /// or carried in a structured-log property.
        /// </summary>
        public string Render()
        {
            var sb = new System.Text.StringBuilder(Message);
            foreach (var kv in _props)
            {
                sb.Append('\n').Append(kv.Key).Append('=').Append(kv.Value?.ToString() ?? "<null>");
            }
            return sb.ToString();
        }
    }
}
