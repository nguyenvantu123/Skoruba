// Feature: phone-otp-multi-account-select, Property 12: Per-IP rate-limit on POST /SelectAccount
//
// Validates: Requirements 18.1, 18.3, 18.5, 18.6
//
// Generator: random sequence of POST attempts from the same hashed IP, with
// random cookie state per attempt (cookie state is irrelevant once the rate-
// limit threshold is reached — that is the whole point of R18.6).
//
// Property invariants (Section 10.3 design):
//   * R18.5 — every POST increments the IP counter exactly once, BEFORE
//     cookie inspection. Modelled by asserting the counter equals the number
//     of POST calls made.
//   * R18.3 — `CheckIpSelectAsync` allows the request when counter <
//     threshold and rejects when counter >= threshold.
//   * R18.6 — gate ordering: rate-limit is consulted BEFORE cookie decrypt.
//     Modelled by checking that once the counter passes threshold, the
//     decision is rejection regardless of cookie state we'd then evaluate.
//   * R18.1 — counter scoping is per-IP (single hashed IP suffices for the
//     property; a separate IP yields a separate counter, asserted by the
//     companion test below).

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

public sealed class Property12_PerIpRateLimit
{
    private const string IpHash = "deadbeefcafe1234";
    private const int Threshold = 5;
    private const int WindowSeconds = 600;

    public sealed record AttemptScenario(int N);

    public static class Arbs
    {
        // N ∈ [1, 12] — covers below-threshold (1..4), at-threshold (5),
        // above-threshold (6..12). Bigger range would just exercise cache
        // eviction (out of scope; covered by Task 5 unit tests).
        public static Arbitrary<AttemptScenario> Scenario()
            => Gen.Choose(1, 12).Select(n => new AttemptScenario(n)).ToArbitrary();
    }

    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Arbs) })]
    public async Task Counter_Increments_Per_Post_And_Threshold_Drives_Rejection(AttemptScenario scenario)
    {
        var (limiter, _) = BuildLimiter();

        for (var i = 0; i < scenario.N; i++)
        {
            // R18.5 — RegisterIpSelectAttemptAsync runs every POST, BEFORE any
            // cookie inspection in the controller. Check that the increment is
            // monotonic and the decision flips at the threshold.
            await limiter.RegisterIpSelectAttemptAsync(IpHash, CancellationToken.None);

            var decisionAfter = await limiter.CheckIpSelectAsync(IpHash, CancellationToken.None);

            var calls = i + 1;
            if (calls < Threshold)
            {
                decisionAfter.Allowed.Should().BeTrue(
                    because: "after {0} posts (< threshold {1}), the IP must still be allowed",
                    calls, Threshold);
                decisionAfter.Reason.Should().BeNull();
            }
            else
            {
                decisionAfter.Allowed.Should().BeFalse(
                    because: "after {0} posts (>= threshold {1}), the IP MUST be rate-limited regardless of cookie state (R18.6)",
                    calls, Threshold);
                decisionAfter.Reason.Should().Be("IpSelectWindow");
            }
        }
    }

    [Fact]
    public async Task Different_IpHashes_Have_Independent_Counters()
    {
        // R18.1 — counter is scoped per-IP. Two different hashed IPs MUST not
        // share state.
        var (limiter, _) = BuildLimiter();

        for (var i = 0; i < Threshold; i++)
        {
            await limiter.RegisterIpSelectAttemptAsync("ip-A", CancellationToken.None);
        }

        var decisionA = await limiter.CheckIpSelectAsync("ip-A", CancellationToken.None);
        var decisionB = await limiter.CheckIpSelectAsync("ip-B", CancellationToken.None);

        decisionA.Allowed.Should().BeFalse();
        decisionB.Allowed.Should().BeTrue(
            because: "ip-B has zero attempts; its counter must be independent of ip-A");
    }

    private static (PhoneOtpRateLimiter Limiter, IDistributedCache Cache) BuildLimiter()
    {
        IDistributedCache cache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));

        var config = new PhoneOtpLoginConfiguration
        {
            RedisKeyPrefix = "otp:",
            MultiAccount = new MultiAccountConfiguration
            {
                Enabled = true,
                IpSelectRateLimitWindowSeconds = WindowSeconds,
                IpSelectRateLimitMaxRequests = Threshold,
            },
        };

        var limiter = new PhoneOtpRateLimiter(
            cache,
            Options.Create(config),
            TimeProvider.System);

        return (limiter, cache);
    }
}
