// Feature: phone-otp-multi-account-select, Property 2: Candidate set is tenant-scoped
//
// Validates: Requirements 9.1 (and supporting 2.1, 2.4)
//
// Statement: For every pair of distinct tenants tA, tB and every population of
// users sharing the same phone number across both tenants, IssueAsync invoked
// with TenantKey = tA SHALL produce a Candidate_Set whose every element belongs
// to tA. No user from tB SHALL leak into the candidate set or the persisted
// OtpStoreRecord.
//
// Setup uses the EF InMemory provider (same fixture pattern as Task 3
// PhoneOtpServiceIssueMultiAccountTests) seeded with N_a users in tA and
// N_b users in tB, all sharing the same phone E.164. We assert the candidate
// list reads back from the OtpStoreRecord includes only userIds from tA.

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

public sealed class Property02_CandidateSetTenantScoping
{
    private const string PhoneE164 = "+84334336232";
    private const string RemoteIp = "127.0.0.1";

    public sealed record TenantPopulation(string TenantA, string TenantB, int CountA, int CountB);

    public static class Arbs
    {
        private static readonly string[] TenantPool = { "t-alpha", "t-beta", "t-gamma", "t-delta" };

        public static Arbitrary<TenantPopulation> Population()
        {
            // Pick two distinct tenants and counts in [1..3] each. Bound counts
            // small to keep the InMemory database fast while still exercising
            // the multi-user branch on both sides.
            return (
                from a in Gen.Elements(TenantPool)
                from b in Gen.Elements(TenantPool).Where(x => !string.Equals(x, a, StringComparison.Ordinal))
                from countA in Gen.Choose(1, 3)
                from countB in Gen.Choose(1, 3)
                select new TenantPopulation(a, b, countA, countB))
                .ToArbitrary();
        }
    }

    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Arbs) })]
    public async Task Issue_InTenantA_ProducesCandidateSet_ContainingOnlyTenantAUsers(TenantPopulation pop)
    {
        await using var fixture = await TenantScopedFixture.CreateAsync(pop, PhoneE164);

        var request = new IssueOtpRequest(PhoneE164, pop.TenantA, RemoteIp, ReturnUrl: "/");
        var result = await fixture.Service.IssueAsync(request, CancellationToken.None);

        // R2.1: with multi flag on AND users.Count >= 1, IssueAsync proceeds.
        result.Outcome.Should().Be(IssueOutcome.Issued);
        result.CandidateUserIds.Should().NotBeNull();
        result.CandidateUserIds!.Should().HaveCount(pop.CountA);

        // R9.1 — every candidate in the set belongs to tenant A.
        var tenantAIds = fixture.GetUserIdsForTenant(pop.TenantA);
        var tenantBIds = fixture.GetUserIdsForTenant(pop.TenantB);

        result.CandidateUserIds.Should().BeSubsetOf(tenantAIds);
        result.CandidateUserIds.Should().NotIntersectWith(tenantBIds);

        // R2.4 — persisted record mirrors the result; UserId == CandidateUserIds[0].
        var record = fixture.Store.LastRecord!;
        record.TenantKey.Should().Be(pop.TenantA);
        record.CandidateUserIds.Should().Equal(result.CandidateUserIds);
        record.UserId.Should().Be(result.CandidateUserIds![0]);
    }

    private sealed class TenantScopedFixture : IAsyncDisposable
    {
        public PhoneOtpService Service { get; }
        public InMemoryStore Store { get; }
        public Dictionary<string, List<string>> UsersByTenant { get; }

        private readonly ServiceProvider _root;
        private readonly IServiceScope _scope;

        private TenantScopedFixture(
            PhoneOtpService service,
            InMemoryStore store,
            Dictionary<string, List<string>> usersByTenant,
            ServiceProvider root,
            IServiceScope scope)
        {
            Service = service;
            Store = store;
            UsersByTenant = usersByTenant;
            _root = root;
            _scope = scope;
        }

        public IReadOnlyList<string> GetUserIdsForTenant(string tenantKey)
            => UsersByTenant.TryGetValue(tenantKey, out var ids) ? ids : Array.Empty<string>();

        public static async Task<TenantScopedFixture> CreateAsync(TenantPopulation pop, string sharedPhone)
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
            services.AddDataProtection();

            // Each property iteration gets its own InMemory database to keep
            // populations independent — FsCheck reuses the test instance.
            var dbName = Guid.NewGuid().ToString();
            services.AddDbContext<AdminIdentityDbContext>(o => o.UseInMemoryDatabase(dbName));
            services
                .AddIdentityCore<UserIdentity>()
                .AddRoles<UserIdentityRole>()
                .AddEntityFrameworkStores<AdminIdentityDbContext>()
                .AddDefaultTokenProviders();

            var root = services.BuildServiceProvider();
            var scope = root.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<AdminIdentityDbContext>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserIdentity>>();

            var byTenant = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            await SeedTenantAsync(ctx, byTenant, pop.TenantA, pop.CountA, sharedPhone);
            await SeedTenantAsync(ctx, byTenant, pop.TenantB, pop.CountB, sharedPhone);

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
                    Enabled = true,
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
                userManager,
                new EphemeralDataProtectionProvider(),
                Options.Create(config),
                NullLogger<PhoneOtpService>.Instance,
                TimeProvider.System);

            return new TenantScopedFixture(service, store, byTenant, root, scope);
        }

        private static async Task SeedTenantAsync(
            AdminIdentityDbContext ctx,
            Dictionary<string, List<string>> byTenant,
            string tenantKey,
            int count,
            string phoneE164)
        {
            var ids = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                var id = $"u-{tenantKey}-{i}-{Guid.NewGuid():N}";
                var userName = $"user-{tenantKey}-{i}";
                ctx.Users.Add(new UserIdentity
                {
                    Id = id,
                    UserName = userName,
                    NormalizedUserName = userName.ToUpperInvariant(),
                    Email = $"{userName}@example.com",
                    NormalizedEmail = $"{userName}@example.com".ToUpperInvariant(),
                    PhoneNumber = phoneE164,
                    PhoneNumberConfirmed = true,
                    TenantKey = tenantKey,
                    LockoutEnabled = false,
                    LockoutEnd = null,
                    SecurityStamp = Guid.NewGuid().ToString(),
                });
                ids.Add(id);
            }
            await ctx.SaveChangesAsync();
            byTenant[tenantKey] = ids;
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

        public Task<OtpStoreRecord?> GetAsync(string tenantKey, string phoneE164Hash, CancellationToken ct)
            => Task.FromResult(_data.TryGetValue(Key(tenantKey, phoneE164Hash), out var r) ? r : null);

        public Task SetAsync(string tenantKey, string phoneE164Hash, OtpStoreRecord record, TimeSpan ttl, CancellationToken ct)
        {
            _data[Key(tenantKey, phoneE164Hash)] = record;
            LastRecord = record;
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
