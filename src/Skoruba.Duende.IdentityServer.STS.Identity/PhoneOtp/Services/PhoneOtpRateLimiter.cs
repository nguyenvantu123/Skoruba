using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Configuration;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Models;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Services;

public sealed class PhoneOtpRateLimiter : IPhoneOtpRateLimiter
{
    private readonly IDistributedCache _cache;
    private readonly PhoneOtpLoginConfiguration _config;
    private readonly TimeProvider _timeProvider;
    private readonly string _prefix;

    public PhoneOtpRateLimiter(
        IDistributedCache cache,
        IOptions<PhoneOtpLoginConfiguration> options,
        TimeProvider timeProvider)
    {
        _cache = cache;
        _config = options.Value;
        _timeProvider = timeProvider;
        _prefix = _config.RedisKeyPrefix;
    }

    public async Task<RateLimitDecision> CheckPhoneCooldownAsync(string tenantKey, string phoneE164Hash, CancellationToken ct)
    {
        var key = BuildPhoneCooldownKey(tenantKey, phoneE164Hash);
        var stored = await _cache.GetStringAsync(key, ct);

        if (stored is null)
        {
            return new RateLimitDecision(true, null, null);
        }

        if (!long.TryParse(stored, out var issuedUnixSeconds))
        {
            return new RateLimitDecision(true, null, null);
        }

        var nowUnixSeconds = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var remaining = issuedUnixSeconds + _config.ResendCooldownSeconds - nowUnixSeconds;

        if (remaining > 0)
        {
            return new RateLimitDecision(false, "PhoneCooldown", (int)remaining);
        }

        return new RateLimitDecision(true, null, null);
    }

    public async Task RegisterPhoneIssuanceAsync(string tenantKey, string phoneE164Hash, CancellationToken ct)
    {
        var key = BuildPhoneCooldownKey(tenantKey, phoneE164Hash);
        var nowUnixSeconds = _timeProvider.GetUtcNow().ToUnixTimeSeconds().ToString();

        await _cache.SetStringAsync(key, nowUnixSeconds, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_config.ResendCooldownSeconds)
        }, ct);
    }

    public async Task<RateLimitDecision> CheckIpAsync(string ipHash, CancellationToken ct)
    {
        var key = BuildIpKey(ipHash);
        var stored = await _cache.GetStringAsync(key, ct);

        if (stored is null)
        {
            return new RateLimitDecision(true, null, null);
        }

        if (!int.TryParse(stored, out var counter))
        {
            return new RateLimitDecision(true, null, null);
        }

        if (counter >= _config.IpRateLimitMaxRequests)
        {
            return new RateLimitDecision(false, "IpWindow", null);
        }

        return new RateLimitDecision(true, null, null);
    }

    public async Task RegisterIpIssuanceAsync(string ipHash, CancellationToken ct)
    {
        var key = BuildIpKey(ipHash);
        var stored = await _cache.GetStringAsync(key, ct);

        int counter;
        if (stored is null || !int.TryParse(stored, out counter))
        {
            counter = 0;
        }

        counter++;

        await _cache.SetStringAsync(key, counter.ToString(), new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_config.IpRateLimitWindowSeconds)
        }, ct);
    }

    public async Task<RateLimitDecision> CheckPhoneLockoutAsync(string tenantKey, string phoneE164Hash, CancellationToken ct)
    {
        var key = BuildLockoutKey(tenantKey, phoneE164Hash);
        var stored = await _cache.GetStringAsync(key, ct);

        if (stored is null)
        {
            return new RateLimitDecision(true, null, null);
        }

        if (!int.TryParse(stored, out var counter))
        {
            return new RateLimitDecision(true, null, null);
        }

        if (counter >= _config.PhoneVerifyLockoutMaxFailures)
        {
            return new RateLimitDecision(false, "PhoneLockout", null);
        }

        return new RateLimitDecision(true, null, null);
    }

    public async Task RegisterVerifyFailureAsync(string tenantKey, string phoneE164Hash, CancellationToken ct)
    {
        var key = BuildLockoutKey(tenantKey, phoneE164Hash);
        var stored = await _cache.GetStringAsync(key, ct);

        int counter;
        if (stored is null || !int.TryParse(stored, out counter))
        {
            counter = 0;
        }

        counter++;

        await _cache.SetStringAsync(key, counter.ToString(), new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_config.PhoneVerifyLockoutWindowSeconds)
        }, ct);
    }

    public async Task<RateLimitDecision> CheckIpSelectAsync(string ipHash, CancellationToken ct)
    {
        var key = BuildIpSelectKey(ipHash);
        var stored = await _cache.GetStringAsync(key, ct);

        if (stored is null || !int.TryParse(stored, out var counter))
        {
            return new RateLimitDecision(true, null, null);
        }

        if (counter >= _config.MultiAccount.IpSelectRateLimitMaxRequests)
        {
            return new RateLimitDecision(false, "IpSelectWindow", null);
        }

        return new RateLimitDecision(true, null, null);
    }

    public async Task RegisterIpSelectAttemptAsync(string ipHash, CancellationToken ct)
    {
        var key = BuildIpSelectKey(ipHash);
        var stored = await _cache.GetStringAsync(key, ct);

        int counter;
        if (stored is null || !int.TryParse(stored, out counter))
        {
            counter = 0;
        }

        counter++;

        await _cache.SetStringAsync(key, counter.ToString(), new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_config.MultiAccount.IpSelectRateLimitWindowSeconds)
        }, ct);
    }

    private string BuildPhoneCooldownKey(string tenantKey, string phoneE164Hash)
        => $"{_prefix}rl:phone:{tenantKey}:{phoneE164Hash}";

    private string BuildIpKey(string ipHash)
        => $"{_prefix}rl:ip:{ipHash}";

    private string BuildIpSelectKey(string ipHash)
        => $"{_prefix}rl:ip-select:{ipHash}";

    private string BuildLockoutKey(string tenantKey, string phoneE164Hash)
        => $"{_prefix}lockout:phone:{tenantKey}:{phoneE164Hash}";
}
