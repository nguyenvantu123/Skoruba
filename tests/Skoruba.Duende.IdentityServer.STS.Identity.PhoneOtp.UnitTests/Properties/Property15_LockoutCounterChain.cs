// Feature: phone-otp-multi-account-select, Property 15: Lockout counter chains into Issue rejection
//
// Validates: Requirements 11.2
//
// Statement: For every N rejection-count registered via
// PhoneOtpRateLimiter.RegisterVerifyFailureAsync against the same
// (tenantKey, phoneE164Hash) pair, when N >= PhoneVerifyLockoutMaxFailures,
// every subsequent IssueAsync for that phone SHALL return Rejected for the
// remainder of the window. The reject path SHALL fire BEFORE the user lookup
// (Step 4 of IssueAsync, see PhoneOtpService.cs) — this property focuses on
// the rate-limiter half of the chain (CheckPhoneLockoutAsync.Allowed becomes
// false) so we don't have to seed an InMemory database or PhoneOtpService
// fixture; the integration test in Task 12 covers the IssueAsync end-to-end.

using System;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Configuration;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Services;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.UnitTests.Properties;

public sealed class Property15_LockoutCounterChain
{
    private const string TenantKey = "tenant-a";
    private const string PhoneE164Hash = "deadbeef";
    private const int Threshold = 10;          // PhoneVerifyLockoutMaxFailures
    private const int WindowSeconds = 3600;     // PhoneVerifyLockoutWindowSeconds

    public sealed record FailureScenario(int N);

    public static class Arbs
    {
        // N ∈ [1, 20]: covers below-threshold (1..9), at-threshold (10), and
        // well-above-threshold (11..20) per task spec generator.
        public static Arbitrary<FailureScenario> Scenario()
            => Gen.Choose(1, 20).Select(n => new FailureScenario(n)).ToArbitrary();
    }

    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Arbs) })]
    public async Task RegisterVerifyFailures_ChainIntoLockout_PerThreshold(FailureScenario scenario)
    {
        var (limiter, _) = BuildLimiter();

        for (var i = 0; i < scenario.N; i++)
        {
            await limiter.RegisterVerifyFailureAsync(TenantKey, PhoneE164Hash, CancellationToken.None);
        }

        var decision = await limiter.CheckPhoneLockoutAsync(TenantKey, PhoneE164Hash, CancellationToken.None);

        if (scenario.N >= Threshold)
        {
            decision.Allowed.Should().BeFalse(
                because: "N={0} reached or exceeded threshold={1}; the lockout SHALL chain into IssueAsync (R11.2)",
                scenario.N, Threshold);
            decision.Reason.Should().Be("PhoneLockout");
        }
        else
        {
            decision.Allowed.Should().BeTrue(
                because: "N={0} is below threshold={1}; lockout MUST NOT trigger yet",
                scenario.N, Threshold);
            decision.Reason.Should().BeNull();
        }
    }

    private static (PhoneOtpRateLimiter Limiter, IDistributedCache Cache) BuildLimiter()
    {
        // Fresh MemoryDistributedCache per scenario so counters from prior
        // FsCheck iterations don't leak across runs.
        IDistributedCache cache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));

        var config = new PhoneOtpLoginConfiguration
        {
            RedisKeyPrefix = "otp:",
            PhoneVerifyLockoutMaxFailures = Threshold,
            PhoneVerifyLockoutWindowSeconds = WindowSeconds,
            MultiAccount = new MultiAccountConfiguration(),
        };

        var limiter = new PhoneOtpRateLimiter(
            cache,
            Options.Create(config),
            TimeProvider.System);

        return (limiter, cache);
    }
}
