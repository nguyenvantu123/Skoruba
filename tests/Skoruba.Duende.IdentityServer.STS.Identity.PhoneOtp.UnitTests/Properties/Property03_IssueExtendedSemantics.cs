// Feature: phone-otp-multi-account-select, Property 3: Issue branches correctly across (Count, MultiAccount.Enabled)
//
// Validates: Requirements 1.3, 1.4, 2.1, 2.2, 2.4
//
// Acceptance matrix (Section 4.1 design, Section 10.3 Property 3):
//   Count == 0                         -> Rejected
//   Count == 1, flag = false           -> Issued, Candidate_Set.Count == 1
//   Count == 1, flag = true            -> Issued, Candidate_Set.Count == 1
//   Count >= 2, flag = false           -> Rejected (R1.3, R2.2 fail-closed)
//   Count >= 2, flag = true            -> Issued, Candidate_Set.Count == Count
// Plus invariant for the Issued branch: record.UserId == record.CandidateUserIds[0] (R2.4).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

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

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.UnitTests.Properties;

public sealed class Property03_IssueExtendedSemantics
{
    private const string TenantKey = "tenant-a";
    private const string PhoneE164 = "+84334336232";
    private const string RemoteIp = "127.0.0.1";

    public sealed record IssueScenario(int Count, bool FlagEnabled);

    public static class Arbs
    {
        // Count ∈ {0..5}, flag ∈ {true, false} per task spec generator.
        public static Arbitrary<IssueScenario> Scenario()
            => (from c in Gen.Choose(0, 5)
                from f in Gen.Elements(true, false)
                select new IssueScenario(c, f))
               .ToArbitrary();
    }

    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Arbs) })]
    public async Task Issue_OutcomeMatchesAcceptanceMatrix(IssueScenario scenario)
    {
        await using var fixture = await IssueFixture.CreateAsync(scenario.Count, scenario.FlagEnabled);

        var request = new IssueOtpRequest(PhoneE164, TenantKey, RemoteIp, ReturnUrl: "/");
        var result = await fixture.Service.IssueAsync(request, CancellationToken.None);

        var issuedExpected =
            scenario.Count == 1 ||
            (scenario.Count >= 2 && scenario.FlagEnabled);

        if (!issuedExpected)
        {
            result.Outcome.Should().Be(
                IssueOutcome.Rejected,
                because: "(Count={0}, flag={1}) belongs to the rejection cell of the matrix",
                scenario.Count, scenario.FlagEnabled);
            result.CandidateUserIds.Should().BeNull();
            fixture.Store.LastRecord.Should().BeNull();
            return;
        }

        // Issued branch.
        result.Outcome.Should().Be(IssueOutcome.Issued);
        result.CandidateUserIds.Should().NotBeNull();
        result.CandidateUserIds!.Count.Should().Be(scenario.Count);

        // R2.4 invariant: record.UserId == record.CandidateUserIds[0].
        var record = fixture.Store.LastRecord!;
        record.UserId.Should().Be(record.CandidateUserIds[0]);
        record.UserId.Should().Be(result.CandidateUserIds[0]);
        record.CandidateUserIds.Should().Equal(result.CandidateUserIds);

        // R2.2: even when single-user flow runs with the flag on, the persisted
        // set must match the issued result so VerifyAsync can branch on it.
        fixture.Store.WriteCount.Should().Be(1);
    }

    private sealed class IssueFixture : IAsyncDisposable
    {
        public PhoneOtpService Service { get; }
        public InMemoryStore Store { get; }

        private readonly ServiceProvider _root;
        private readonly IServiceScope _scope;

        private IssueFixture(PhoneOtpService service, InMemoryStore store, ServiceProvider root, IServiceScope scope)
        {
            Service = service;
            Store = store;
            _root = root;
            _scope = scope;
        }

        public static async Task<IssueFixture> CreateAsync(int userCount, bool multiEnabled)
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
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
            var ctx = scope.ServiceProvider.GetRequiredService<AdminIdentityDbContext>();
            var um = scope.ServiceProvider.GetRequiredService<UserManager<UserIdentity>>();

            for (var i = 0; i < userCount; i++)
            {
                var name = $"user-{i:D2}";
                ctx.Users.Add(new UserIdentity
                {
                    Id = $"u-{i}-{Guid.NewGuid():N}",
                    UserName = name,
                    NormalizedUserName = name.ToUpperInvariant(),
                    Email = $"{name}@example.com",
                    NormalizedEmail = $"{name}@example.com".ToUpperInvariant(),
                    PhoneNumber = PhoneE164,
                    PhoneNumberConfirmed = true,
                    TenantKey = TenantKey,
                    LockoutEnabled = false,
                    LockoutEnd = null,
                    SecurityStamp = Guid.NewGuid().ToString(),
                });
            }
            await ctx.SaveChangesAsync();

            var rateLimiter = Substitute.For<IPhoneOtpRateLimiter>();
            rateLimiter.CheckIpAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new RateLimitDecision(true, null, null));
            rateLimiter.CheckPhoneCooldownAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new RateLimitDecision(true, null, null));
            rateLimiter.CheckPhoneLockoutAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new RateLimitDecision(true, null, null));

            var store = new InMemoryStore();
            var config = new PhoneOtpLoginConfiguration
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
                    Enabled = multiEnabled,
                    SelectTtlSeconds = 60,
                    IpSelectRateLimitWindowSeconds = 600,
                    IpSelectRateLimitMaxRequests = 30,
                },
            };

            var service = new PhoneOtpService(
                store,
                rateLimiter,
                new PhoneNumberNormalizer(),
                new FakeSmsSender(),
                um,
                new EphemeralDataProtectionProvider(),
                Options.Create(config),
                NullLogger<PhoneOtpService>.Instance,
                TimeProvider.System);

            return new IssueFixture(service, store, root, scope);
        }

        public async ValueTask DisposeAsync()
        {
            _scope.Dispose();
            await _root.DisposeAsync();
        }
    }

    private sealed class InMemoryStore : IPhoneOtpStore
    {
        private readonly Dictionary<string, OtpStoreRecord> _data = new(StringComparer.Ordinal);
        public OtpStoreRecord? LastRecord { get; private set; }
        public int WriteCount { get; private set; }

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
}
