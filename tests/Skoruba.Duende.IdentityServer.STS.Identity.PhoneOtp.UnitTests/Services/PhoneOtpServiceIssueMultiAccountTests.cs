using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.DbContexts;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.Entities.Identity;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Configuration;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Models;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Services;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Sms;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Storage;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.UnitTests.Services;

/// <summary>
/// Tests cho <see cref="PhoneOtpService.IssueAsync"/> trên matrix
/// (<c>users.Count</c>, <c>MultiAccount.Enabled</c>) — Section 4.1 design
/// (Property 3 acceptance matrix). Covers Requirements 1.3, 1.4, 2.1–2.8,
/// 9.1, 10.1, 14.4.
/// </summary>
public class PhoneOtpServiceIssueMultiAccountTests : IDisposable
{
    private const string TenantKey = "tenant-a";
    private const string PhoneE164 = "+84334336232";
    private const string RemoteIp = "127.0.0.1";

    private readonly ServiceProvider _rootProvider;
    private readonly IServiceScope _scope;
    private readonly AdminIdentityDbContext _dbContext;
    private readonly UserManager<UserIdentity> _userManager;
    private readonly InMemoryPhoneOtpStore _store;
    private readonly RecordingLogger<PhoneOtpService> _logger;
    private readonly FakeSmsSender _smsSender;
    private readonly IPhoneOtpRateLimiter _rateLimiter;
    private readonly PhoneOtpLoginConfiguration _config;

    public PhoneOtpServiceIssueMultiAccountTests()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(NullLoggerProvider.Instance));
        services.AddDataProtection();

        services.AddDbContext<AdminIdentityDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString()));

        services
            .AddIdentityCore<UserIdentity>()
            .AddRoles<UserIdentityRole>()
            .AddEntityFrameworkStores<AdminIdentityDbContext>()
            .AddDefaultTokenProviders();

        _rootProvider = services.BuildServiceProvider();
        _scope = _rootProvider.CreateScope();
        _dbContext = _scope.ServiceProvider.GetRequiredService<AdminIdentityDbContext>();
        _userManager = _scope.ServiceProvider.GetRequiredService<UserManager<UserIdentity>>();

        _store = new InMemoryPhoneOtpStore();
        _logger = new RecordingLogger<PhoneOtpService>();
        _smsSender = new FakeSmsSender();

        // Allow-all rate limiter — multi-account branching không liên quan rate-limit.
        _rateLimiter = Substitute.For<IPhoneOtpRateLimiter>();
        _rateLimiter
            .CheckIpAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RateLimitDecision(true, null, null));
        _rateLimiter
            .CheckPhoneCooldownAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RateLimitDecision(true, null, null));
        _rateLimiter
            .CheckPhoneLockoutAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RateLimitDecision(true, null, null));

        _config = new PhoneOtpLoginConfiguration
        {
            Enabled = true,
            DefaultRegion = "VN",
            OtpLength = 6,
            OtpTtlSeconds = 300,
            ResendCooldownSeconds = 60,
            MaxVerifyAttemptsPerOtp = 5,
            IpRateLimitWindowSeconds = 600,
            IpRateLimitMaxRequests = 100,
            PhoneVerifyLockoutWindowSeconds = 3600,
            PhoneVerifyLockoutMaxFailures = 10,
            RedisKeyPrefix = "otp:",
            MultiAccount = new MultiAccountConfiguration
            {
                Enabled = false,
                SelectTtlSeconds = 60,
                IpSelectRateLimitWindowSeconds = 600,
                IpSelectRateLimitMaxRequests = 30,
            },
        };
    }

    public void Dispose()
    {
        _scope.Dispose();
        _rootProvider.Dispose();
    }

    private PhoneOtpService BuildService()
    {
        return new PhoneOtpService(
            _store,
            _rateLimiter,
            new PhoneNumberNormalizer(),
            _smsSender,
            _userManager,
            new EphemeralDataProtectionProvider(),
            Options.Create(_config),
            _logger,
            TimeProvider.System);
    }

    private async Task SeedUserAsync(string id, string userName, string phone = PhoneE164,
        string tenantKey = TenantKey, bool phoneConfirmed = true,
        bool lockoutEnabled = false, DateTimeOffset? lockoutEnd = null)
    {
        var user = new UserIdentity
        {
            Id = id,
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Email = $"{userName}@example.com",
            NormalizedEmail = $"{userName}@example.com".ToUpperInvariant(),
            PhoneNumber = phone,
            PhoneNumberConfirmed = phoneConfirmed,
            TenantKey = tenantKey,
            LockoutEnabled = lockoutEnabled,
            LockoutEnd = lockoutEnd,
            SecurityStamp = Guid.NewGuid().ToString(),
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();
    }

    private static IssueOtpRequest BuildRequest(string raw = PhoneE164)
        => new(raw, TenantKey, RemoteIp, ReturnUrl: "/");

    [Fact]
    public async Task Count0_Rejected()
    {
        // No user seeded → users.Count == 0.
        _config.MultiAccount.Enabled = false;
        var svc = BuildService();

        var result = await svc.IssueAsync(BuildRequest(), CancellationToken.None);

        result.Outcome.Should().Be(IssueOutcome.Rejected);
        result.CandidateUserIds.Should().BeNull();
        _store.WriteCount.Should().Be(0); // no record persisted (R2.2 + spec gốc)
        _smsSender.Sent.Should().BeEmpty();
        _logger.Entries.Should().Contain(e => e.Message.Contains("user lookup failed"));
    }

    [Fact]
    public async Task Count1_FlagOff_Issued_LegacyShape()
    {
        await SeedUserAsync("u-1", "alice");
        _config.MultiAccount.Enabled = false;
        var svc = BuildService();

        var result = await svc.IssueAsync(BuildRequest(), CancellationToken.None);

        result.Outcome.Should().Be(IssueOutcome.Issued);
        result.CandidateUserIds.Should().NotBeNull();
        result.CandidateUserIds!.Count.Should().Be(1);
        result.CandidateUserIds[0].Should().Be("u-1");

        var record = _store.LastRecord!;
        record.UserId.Should().Be("u-1");
        record.CandidateUserIds.Should().Equal(new[] { "u-1" });

        _smsSender.Sent.Should().HaveCount(1);
        _smsSender.Sent.Single().E164PhoneNumber.Should().Be(PhoneE164);

        var issued = AssertIssuedLogEntry();
        issued.GetProperty("CandidateCount").Should().Be(1);
        issued.GetProperty("Outcome").Should().Be("Issued");
    }

    [Fact]
    public async Task Count1_FlagOn_Issued_SingleElementCandidateSet()
    {
        await SeedUserAsync("u-1", "alice");
        _config.MultiAccount.Enabled = true;
        var svc = BuildService();

        var result = await svc.IssueAsync(BuildRequest(), CancellationToken.None);

        result.Outcome.Should().Be(IssueOutcome.Issued);
        result.CandidateUserIds.Should().NotBeNull();
        result.CandidateUserIds!.Count.Should().Be(1);
        result.CandidateUserIds.Single().Should().Be("u-1");

        var record = _store.LastRecord!;
        record.UserId.Should().Be("u-1");
        record.CandidateUserIds.Should().Equal(new[] { "u-1" });

        AssertIssuedLogEntry().GetProperty("CandidateCount").Should().Be(1);
    }

    [Fact]
    public async Task CountMany_FlagOff_Rejected()
    {
        await SeedUserAsync("u-1", "alice");
        await SeedUserAsync("u-2", "bob");
        await SeedUserAsync("u-3", "carol");
        _config.MultiAccount.Enabled = false;
        var svc = BuildService();

        var result = await svc.IssueAsync(BuildRequest(), CancellationToken.None);

        // Legacy fail-closed (R1.3, R2.2): nhiều user, flag off → reject, KHÔNG persist record.
        result.Outcome.Should().Be(IssueOutcome.Rejected);
        result.CandidateUserIds.Should().BeNull();
        _store.WriteCount.Should().Be(0);
        _smsSender.Sent.Should().BeEmpty();

        var rejected = _logger.Entries.Should().ContainSingle(e =>
            e.Message.Contains("user lookup failed (count=")).Which;
        rejected.GetProperty("Outcome").Should().Be("Rejected");
        rejected.GetProperty("UserCount").Should().Be(3);
    }

    [Fact]
    public async Task CountMany_FlagOn_Issued_PersistsCandidateSet_DeterministicOrder()
    {
        // Sort key: (LockoutEnabled ASC, LockoutEnd NULL FIRST then ASC, NormalizedUserName ASC).
        // Layout chọn để mọi cấp sort đều có ý nghĩa:
        //  - u-bob: LockoutEnabled = false              → đứng đầu (cấp 1)
        //  - u-carol: LockoutEnabled = true, LockoutEnd = null  → đứng giữa (NULL FIRST)
        //  - u-alice: LockoutEnabled = true, LockoutEnd = future → cuối
        // Để xác nhận tie-break NormalizedUserName ASC, thêm u-zzz cùng (true, null)
        // và assert CAROL trước ZZZ.
        var lockoutFuture = DateTimeOffset.UtcNow.AddMinutes(30);
        await SeedUserAsync("id-bob", "bob", lockoutEnabled: false);
        await SeedUserAsync("id-carol", "carol", lockoutEnabled: true, lockoutEnd: null);
        await SeedUserAsync("id-zzz", "zzz", lockoutEnabled: true, lockoutEnd: null);
        await SeedUserAsync("id-alice", "alice", lockoutEnabled: true, lockoutEnd: lockoutFuture);

        _config.MultiAccount.Enabled = true;
        var svc = BuildService();

        var result = await svc.IssueAsync(BuildRequest(), CancellationToken.None);

        result.Outcome.Should().Be(IssueOutcome.Issued);
        result.CandidateUserIds.Should().NotBeNull();
        result.CandidateUserIds!.Should().Equal(new[] { "id-bob", "id-carol", "id-zzz", "id-alice" });

        var record = _store.LastRecord!;
        record.UserId.Should().Be(record.CandidateUserIds[0]); // R2.4
        record.UserId.Should().Be("id-bob");
        record.CandidateUserIds.Should().Equal(result.CandidateUserIds);

        AssertIssuedLogEntry().GetProperty("CandidateCount").Should().Be(4);
    }

    private LogProperties AssertIssuedLogEntry()
    {
        var entry = _logger.Entries.Should().ContainSingle(e =>
            e.Message.Contains("OTP issued successfully")).Which;
        return entry;
    }

    // --- in-memory helpers ---

    private sealed class InMemoryPhoneOtpStore : IPhoneOtpStore
    {
        private readonly Dictionary<string, OtpStoreRecord> _data = new();
        public int WriteCount { get; private set; }
        public OtpStoreRecord? LastRecord { get; private set; }

        public Task<OtpStoreRecord?> GetAsync(string tenantKey, string phoneE164Hash, CancellationToken ct)
            => Task.FromResult(_data.TryGetValue(Key(tenantKey, phoneE164Hash), out var r) ? r : null);

        public Task SetAsync(string tenantKey, string phoneE164Hash, OtpStoreRecord record, TimeSpan ttl, CancellationToken ct)
        {
            _data[Key(tenantKey, phoneE164Hash)] = record;
            LastRecord = record;
            WriteCount++;
            return Task.CompletedTask;
        }

        public Task<int> IncrementAttemptAsync(string tenantKey, string phoneE164Hash, CancellationToken ct)
            => Task.FromResult(0);

        public Task DeleteAsync(string tenantKey, string phoneE164Hash, CancellationToken ct)
        {
            _data.Remove(Key(tenantKey, phoneE164Hash));
            return Task.CompletedTask;
        }

        private static string Key(string tenantKey, string hash) => $"{tenantKey}:{hash}";
    }

    /// <summary>
    /// Captures structured properties from <see cref="ILogger"/> calls so we can
    /// assert <c>{CandidateCount}</c> appears in the issued log entry without
    /// depending on Serilog or any test sink package.
    /// </summary>
    private sealed class LogProperties
    {
        public string Message { get; init; } = string.Empty;
        public LogLevel Level { get; init; }
        private readonly Dictionary<string, object?> _props = new(StringComparer.Ordinal);

        public void Add(string key, object? value) => _props[key] = value;
        public object? GetProperty(string name) => _props.TryGetValue(name, out var v) ? v : null;
        public bool ContainsProperty(string name) => _props.ContainsKey(name);
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
}
