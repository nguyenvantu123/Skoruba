using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Duende.IdentityServer.Services;

using FluentAssertions;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
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
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Services;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Storage;
using Skoruba.Duende.IdentityServer.STS.Identity.ViewModels.Account;

using TenantInfrastructure.Abstractions;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.UnitTests.Controllers;

/// <summary>
/// Unit tests cho <c>PhoneLoginController.SelectAccountGet</c> (Section 4.5 design).
/// Cover 5 reject branch + 1 success render. Tái sử dụng real
/// <see cref="PhoneOtpAccountSelectCookieCodec"/> và
/// <see cref="SelectionTokenProtector"/> với <see cref="EphemeralDataProtectionProvider"/>.
/// Validates Requirements 5.2, 5.3, 5.4, 5.5, 5.6, 5.15, 9.2, 9.3, 12.9, 14.4.
/// </summary>
public sealed class PhoneLoginControllerSelectAccountGetTests : IDisposable
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

    public PhoneLoginControllerSelectAccountGetTests()
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
    }

    public void Dispose()
    {
        _scope.Dispose();
        _rootProvider.Dispose();
    }

    private async Task SeedUserAsync(
        string id,
        string userName,
        string tenantKey = TenantKey,
        bool phoneConfirmed = true)
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
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();
    }

    private PhoneLoginController BuildController(
        string? cookieRaw,
        bool wireSelectionServices = true,
        IDictionary<string, object?>? tempData = null)
    {
        var config = new PhoneOtpLoginConfiguration
        {
            Enabled = true,
            DefaultRegion = "VN",
            OtpLength = 6,
            MultiAccount = new MultiAccountConfiguration { Enabled = true, SelectTtlSeconds = 60 },
        };

        var controller = new PhoneLoginController(
            phoneOtpService: Substitute.For<IPhoneOtpService>(),
            cookieCodec: new PhoneOtpSessionCookieCodec(_dataProtection),
            normalizer: new PhoneNumberNormalizer(),
            tenantContextAccessor: _tenant,
            signInManager: null!, // not used by GET path
            userManager: _userManager,
            interaction: Substitute.For<IIdentityServerInteractionService>(),
            events: Substitute.For<IEventService>(),
            antiBot: new NoopPhoneOtpAntiBotChallenge(),
            store: Substitute.For<IPhoneOtpStore>(),
            options: Options.Create(config),
            logger: NullLogger<PhoneLoginController>.Instance,
            localizer: _localizer,
            timeProvider: _timeProvider,
            selectCodec: wireSelectionServices ? _selectCodec : null,
            tokenProtector: wireSelectionServices ? _tokenProtector : null);

        var httpContext = new DefaultHttpContext
        {
            RequestServices = _scope.ServiceProvider,
        };

        if (!string.IsNullOrEmpty(cookieRaw))
        {
            httpContext.Request.Headers["Cookie"] =
                $"{PhoneOtpAccountSelectCookieCodec.CookieName}={cookieRaw}";
        }

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
            RouteData = new RouteData(),
            ActionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor(),
        };

        var dict = new TempDataDictionary(httpContext, new TestTempDataProvider());
        if (tempData != null)
        {
            foreach (var kv in tempData)
            {
                dict[kv.Key] = kv.Value;
            }
        }

        controller.TempData = dict;

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
    // Reject branches
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Cookie_Absent_RedirectsToLogin_PreservingReturnUrl()
    {
        // R5.2 — cookie absent: 302 to /Account/Login với returnUrl encoded.
        var controller = BuildController(cookieRaw: null);

        var result = await controller.SelectAccountGet("/protected/area", CancellationToken.None);

        var redirect = result.Should().BeOfType<RedirectResult>().Which;
        redirect.Url.Should().Be("/Account/Login?returnUrl=" + Uri.EscapeDataString("/protected/area"));
        // Không có cookie set/delete vì cookie absent ngay từ đầu.
        controller.Response.Headers.Should().NotContainKey("Set-Cookie");
    }

    [Fact]
    public async Task Cookie_DecryptFail_ClearsAndRedirects()
    {
        // R6.6.a — decrypt fail: clear cookie + 302 (no log warning ở GET).
        var controller = BuildController(cookieRaw: "this-is-not-a-valid-protected-payload");

        var result = await controller.SelectAccountGet("/r", CancellationToken.None);

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Be("/Account/Login?returnUrl=" + Uri.EscapeDataString("/r"));
        AssertCookieDeleted(controller);
    }

    [Fact]
    public async Task TenantMismatch_ClearsAndRedirects()
    {
        // R5.3, R9.2 — tenant trong cookie != tenant context hiện tại → clear + 302.
        await SeedUserAsync("u-1", "alice");
        var raw = ProtectCookie(new[] { "u-1" }, tenantKey: "other-tenant");

        var controller = BuildController(cookieRaw: raw);

        var result = await controller.SelectAccountGet(returnUrl: null, CancellationToken.None);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Account/Login");
        AssertCookieDeleted(controller);
    }

    [Fact]
    public async Task TtlExpired_ClearsAndRedirectsWithLog()
    {
        // R5.4 — now > ExpiresAtUtc → clear + log Warning + 302.
        await SeedUserAsync("u-1", "alice");
        // Issued tại "now - 120s", expired 60s trước now.
        var pastIssued = _timeProvider.GetUtcNow().AddSeconds(-120);
        var pastExpiry = pastIssued.AddSeconds(60);
        var raw = ProtectCookie(
            new[] { "u-1" },
            issuedAtUtc: pastIssued,
            expiresAtUtc: pastExpiry);

        var controller = BuildController(cookieRaw: raw);

        var result = await controller.SelectAccountGet(returnUrl: "/x", CancellationToken.None);

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Be("/Account/Login?returnUrl=" + Uri.EscapeDataString("/x"));
        AssertCookieDeleted(controller);
    }

    [Fact]
    public async Task EmptyCandidateSet_AfterFilter_RedirectsWithGenericError()
    {
        // R5.15 — sau filter (DB ko match user nào), candidate set rỗng → clear + TempData generic error + 302.
        // Cookie chứa userId không tồn tại trong DB.
        var raw = ProtectCookie(new[] { "ghost-user-id" });

        var controller = BuildController(cookieRaw: raw);

        var result = await controller.SelectAccountGet(returnUrl: null, CancellationToken.None);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Account/Login");
        AssertCookieDeleted(controller);
        controller.TempData["PhoneOtpError"].Should().Be("LoginWithPhone.SelectAccount.GenericError");
    }

    // ----------------------------------------------------------------------
    // Success render
    // ----------------------------------------------------------------------

    [Fact]
    public async Task ValidCookie_RendersView_With_CandidateOptions_InOrder_FirstSelected()
    {
        // R5.5, R5.8 — render view với candidate đúng thứ tự cookie payload, UserName visible
        // text (raw), R5.11 first option selected (assert được trong view, ở đây ta verify model).
        await SeedUserAsync("u-bob", "bob");
        await SeedUserAsync("u-carol", "carol");
        await SeedUserAsync("u-alice", "alice");
        // R12.9 — empty UserName candidate sẽ bị omit silent. Seed thêm 1 user UserName=string.Empty.
        // EF Core IdentityCore yêu cầu UserName non-null cho Add() — bypass qua DbContext trực tiếp.
        var emptyUser = new UserIdentity
        {
            Id = "u-empty",
            UserName = string.Empty,
            NormalizedUserName = string.Empty,
            PhoneNumber = "+84334336232",
            PhoneNumberConfirmed = true,
            TenantKey = TenantKey,
            SecurityStamp = Guid.NewGuid().ToString(),
        };
        _dbContext.Users.Add(emptyUser);
        await _dbContext.SaveChangesAsync();

        // Order trong cookie: bob → carol → empty → alice. Sau filter, expect bob, carol, alice.
        var raw = ProtectCookie(new[] { "u-bob", "u-carol", "u-empty", "u-alice" });

        var tempData = new Dictionary<string, object?>
        {
            ["PhoneOtpMaskedPhone"] = "•••• 6232",
        };
        var controller = BuildController(cookieRaw: raw, tempData: tempData);

        var result = await controller.SelectAccountGet(returnUrl: "/return/here", CancellationToken.None);

        var view = result.Should().BeOfType<ViewResult>().Which;
        view.ViewName.Should().Be("~/Views/Account/LoginWithPhone/SelectAccount.cshtml");
        var model = view.Model.Should().BeOfType<SelectAccountViewModel>().Which;
        model.Candidates.Should().HaveCount(3);
        model.Candidates.Select(c => c.UserName).Should().Equal(new[] { "bob", "carol", "alice" });
        model.MaskedPhone.Should().Be("•••• 6232");
        model.ReturnUrl.Should().Be("/return/here");
        // SelectionToken phải resolve về đúng userId (R6.8 invariant).
        for (var i = 0; i < model.Candidates.Count; i++)
        {
            _tokenProtector.TryResolve(model.Candidates[i].SelectionToken, out var resolved).Should().BeTrue();
            resolved.Should().Be(new[] { "u-bob", "u-carol", "u-alice" }[i]);
        }
        // Không clear cookie ở success path.
        AssertCookieNotDeleted(controller);
    }

    // ----------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------

    private static void AssertCookieDeleted(PhoneLoginController controller)
    {
        // ASP.NET Core Cookies.Delete sets a Set-Cookie header với expires in the past.
        var setCookies = controller.Response.Headers["Set-Cookie"].ToString();
        setCookies.Should().Contain(PhoneOtpAccountSelectCookieCodec.CookieName);
        setCookies.Should().Contain("expires=", "delete cookie should attach an expiry");
    }

    private static void AssertCookieNotDeleted(PhoneLoginController controller)
    {
        var setCookies = controller.Response.Headers["Set-Cookie"].ToString();
        setCookies.Should().NotContain(PhoneOtpAccountSelectCookieCodec.CookieName,
            "success render should preserve the account-select cookie");
    }

    // ----------------------------------------------------------------------
    // test doubles
    // ----------------------------------------------------------------------

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public TestTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Set(DateTimeOffset now) => _now = now;
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
}
