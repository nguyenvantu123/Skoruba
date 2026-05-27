using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Configuration;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Models;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Storage;

public sealed class RedisPhoneOtpStore : IPhoneOtpStore
{
    private readonly IDistributedCache _cache;
    private readonly string _prefix;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public RedisPhoneOtpStore(IDistributedCache cache, IOptions<PhoneOtpLoginConfiguration> options)
    {
        _cache = cache;
        _prefix = options.Value.RedisKeyPrefix;
    }

    public async Task<OtpStoreRecord?> GetAsync(string tenantKey, string phoneE164Hash, CancellationToken ct)
    {
        var key = BuildKey(tenantKey, phoneE164Hash);
        var json = await _cache.GetStringAsync(key, ct);

        if (string.IsNullOrEmpty(json))
            return null;

        var record = JsonSerializer.Deserialize<OtpStoreRecord>(json, JsonOptions);
        if (record is null)
            return null;

        return EnsureCandidateUserIds(record);
    }

    public async Task SetAsync(string tenantKey, string phoneE164Hash, OtpStoreRecord record, TimeSpan ttl, CancellationToken ct)
    {
        var key = BuildKey(tenantKey, phoneE164Hash);
        var json = JsonSerializer.Serialize(record, JsonOptions);

        await _cache.SetStringAsync(key, json, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl
        }, ct);
    }

    public async Task<int> IncrementAttemptAsync(string tenantKey, string phoneE164Hash, CancellationToken ct)
    {
        var key = BuildKey(tenantKey, phoneE164Hash);
        var json = await _cache.GetStringAsync(key, ct);

        if (string.IsNullOrEmpty(json))
            return 0;

        var record = JsonSerializer.Deserialize<OtpStoreRecord>(json, JsonOptions);
        if (record is null)
            return 0;

        record = EnsureCandidateUserIds(record);
        var updatedRecord = record with { AttemptCount = record.AttemptCount + 1 };

        var remainingTtl = record.ExpiresAtUtc - DateTimeOffset.UtcNow;
        if (remainingTtl <= TimeSpan.Zero)
            return updatedRecord.AttemptCount;

        var updatedJson = JsonSerializer.Serialize(updatedRecord, JsonOptions);
        await _cache.SetStringAsync(key, updatedJson, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = remainingTtl
        }, ct);

        return updatedRecord.AttemptCount;
    }

    public async Task DeleteAsync(string tenantKey, string phoneE164Hash, CancellationToken ct)
    {
        var key = BuildKey(tenantKey, phoneE164Hash);
        await _cache.RemoveAsync(key, ct);
    }

    private string BuildKey(string tenantKey, string phoneE164Hash)
    {
        return $"{_prefix}rec:{tenantKey}:{phoneE164Hash}";
    }

    /// <summary>
    /// Backward-compat fallback (Requirement 2.6, 14.4, 16.8): record JSON cũ
    /// thiếu field <c>candidateUserIds</c> SHALL deserialize với
    /// <c>CandidateUserIds = [record.UserId]</c> để in-flight OTP đã issue
    /// trước deploy vẫn verify được sau deploy.
    /// </summary>
    private static OtpStoreRecord EnsureCandidateUserIds(OtpStoreRecord record)
    {
        if (record.CandidateUserIds is null || record.CandidateUserIds.Count == 0)
        {
            return record with { CandidateUserIds = new[] { record.UserId } };
        }

        return record;
    }

    private static string Sha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }
}
