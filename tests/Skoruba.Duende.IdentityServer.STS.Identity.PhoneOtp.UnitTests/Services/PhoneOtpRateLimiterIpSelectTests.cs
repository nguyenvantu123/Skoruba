using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Configuration;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Services;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.UnitTests.Services;

/// <summary>
/// Tests cho phần per-IP rate-limit của POST <c>/Account/LoginWithPhone/SelectAccount</c>
/// (<see cref="IPhoneOtpRateLimiter.RegisterIpSelectAttemptAsync"/> +
/// <see cref="IPhoneOtpRateLimiter.CheckIpSelectAsync"/>) — Section 4.2 design,
/// Requirements 18.1, 18.3, 18.5.
/// </summary>
public class PhoneOtpRateLimiterIpSelectTests
{
    private const string IpHash = "abc123";
    private const string Prefix = "otp:";
    private static readonly string ExpectedKey = $"{Prefix}rl:ip-select:{IpHash}";

    private static (PhoneOtpRateLimiter Limiter, IDistributedCache Cache) Build(
        int windowSeconds = 600,
        int maxRequests = 30)
    {
        IDistributedCache cache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));

        var config = new PhoneOtpLoginConfiguration
        {
            RedisKeyPrefix = Prefix,
            MultiAccount = new MultiAccountConfiguration
            {
                Enabled = true,
                IpSelectRateLimitWindowSeconds = windowSeconds,
                IpSelectRateLimitMaxRequests = maxRequests,
            },
        };

        var limiter = new PhoneOtpRateLimiter(
            cache,
            Options.Create(config),
            TimeProvider.System);

        return (limiter, cache);
    }

    [Fact]
    public async Task RegisterIpSelectAttempt_IncrementsCounter()
    {
        var (limiter, cache) = Build();

        await limiter.RegisterIpSelectAttemptAsync(IpHash, CancellationToken.None);
        await limiter.RegisterIpSelectAttemptAsync(IpHash, CancellationToken.None);
        await limiter.RegisterIpSelectAttemptAsync(IpHash, CancellationToken.None);

        var stored = await cache.GetStringAsync(ExpectedKey, CancellationToken.None);

        stored.Should().Be("3");
    }

    [Fact]
    public async Task CheckIpSelect_AllowedBelowThreshold()
    {
        var (limiter, cache) = Build(maxRequests: 30);

        // Pre-populate counter = 5 (below threshold 30).
        await cache.SetStringAsync(ExpectedKey, "5", CancellationToken.None);

        var decision = await limiter.CheckIpSelectAsync(IpHash, CancellationToken.None);

        decision.Allowed.Should().BeTrue();
        decision.Reason.Should().BeNull();
    }

    [Fact]
    public async Task CheckIpSelect_RejectsAtThreshold()
    {
        var (limiter, cache) = Build(maxRequests: 30);

        await cache.SetStringAsync(ExpectedKey, "30", CancellationToken.None);

        var decision = await limiter.CheckIpSelectAsync(IpHash, CancellationToken.None);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be("IpSelectWindow");
    }

    [Fact]
    public async Task Counter_ExpiresAfterWindow()
    {
        // Window/threshold validation chỉ áp dụng tại AddPhoneOtpLogin (Task 1).
        // Trong unit test ta build PhoneOtpRateLimiter trực tiếp với window = 1s
        // và assert counter expire qua MemoryDistributedCache thực tế. KHÔNG cần
        // FakeTimeProvider vì DistributedCacheEntryOptions.AbsoluteExpirationRelativeToNow
        // được respect bởi MemoryDistributedCache dựa trên wall-clock.
        var (limiter, cache) = Build(windowSeconds: 1);

        await limiter.RegisterIpSelectAttemptAsync(IpHash, CancellationToken.None);

        var immediate = await cache.GetStringAsync(ExpectedKey, CancellationToken.None);
        immediate.Should().Be("1");

        await Task.Delay(TimeSpan.FromMilliseconds(1100));

        var afterExpiry = await cache.GetStringAsync(ExpectedKey, CancellationToken.None);
        afterExpiry.Should().BeNull();

        // Sau khi expire, lần register kế tiếp phải bắt đầu lại từ 1
        // (không cộng dồn vào counter cũ).
        await limiter.RegisterIpSelectAttemptAsync(IpHash, CancellationToken.None);
        var fresh = await cache.GetStringAsync(ExpectedKey, CancellationToken.None);
        fresh.Should().Be("1");
    }
}
