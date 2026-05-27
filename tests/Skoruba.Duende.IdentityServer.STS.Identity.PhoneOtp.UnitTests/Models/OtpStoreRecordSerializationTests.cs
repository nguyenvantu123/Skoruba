using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Configuration;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Models;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Storage;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.UnitTests.Models;

/// <summary>
/// Tests cho serialize/deserialize <see cref="OtpStoreRecord"/> qua
/// <see cref="RedisPhoneOtpStore"/> — bao gồm shape mới (có
/// <c>candidateUserIds</c>) và backward-compat fallback cho record JSON cũ
/// (Section 3.1 design — Requirements 2.6, 14.4, 16.8).
/// </summary>
public class OtpStoreRecordSerializationTests
{
    private const string TenantKey = "tenant-a";
    private const string PhoneE164Hash = "f1d2deadbeef";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static (RedisPhoneOtpStore Store, IDistributedCache Cache) BuildStore()
    {
        IDistributedCache cache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));

        var options = Options.Create(new PhoneOtpLoginConfiguration
        {
            RedisKeyPrefix = "otp:",
        });

        var store = new RedisPhoneOtpStore(cache, options);
        return (store, cache);
    }

    private static string BuildKey(string tenantKey, string phoneE164Hash)
        => $"otp:rec:{tenantKey}:{phoneE164Hash}";

    [Fact]
    public void RoundTrip_NewShape()
    {
        var record = new OtpStoreRecord
        {
            OtpHash = new byte[] { 1, 2, 3, 4 },
            TenantKey = TenantKey,
            PhoneE164 = "+84334336232",
            UserId = "u-1",
            CandidateUserIds = new[] { "u-1", "u-2", "u-3" },
            CreatedAtUtc = new DateTimeOffset(2025, 1, 5, 8, 0, 0, TimeSpan.Zero),
            ExpiresAtUtc = new DateTimeOffset(2025, 1, 5, 8, 5, 0, TimeSpan.Zero),
            AttemptCount = 0,
        };

        var json = JsonSerializer.Serialize(record, JsonOptions);

        json.Should().Contain("\"candidateUserIds\":[\"u-1\",\"u-2\",\"u-3\"]");
        json.Should().Contain("\"userId\":\"u-1\"");

        var result = JsonSerializer.Deserialize<OtpStoreRecord>(json, JsonOptions);

        result.Should().NotBeNull();
        result!.UserId.Should().Be("u-1");
        result.CandidateUserIds.Should().BeEquivalentTo(new[] { "u-1", "u-2", "u-3" },
            opts => opts.WithStrictOrdering());
        result.Should().BeEquivalentTo(record);
    }

    [Fact]
    public async Task LegacyJson_FallsBackToSingleElementCandidateSet()
    {
        var (store, cache) = BuildStore();

        // Legacy JSON từ pre-MultiAccount deploy: KHÔNG có field candidateUserIds.
        // Shape match exactly với JsonNamingPolicy.CamelCase output của shape cũ.
        const string legacyJson = "{" +
            "\"otpHash\":\"AQIDBA==\"," +
            "\"tenantKey\":\"tenant-a\"," +
            "\"phoneE164\":\"+84334336232\"," +
            "\"userId\":\"u-7\"," +
            "\"createdAtUtc\":\"2025-01-05T08:00:00+00:00\"," +
            "\"expiresAtUtc\":\"2025-01-05T08:05:00+00:00\"," +
            "\"attemptCount\":0" +
            "}";

        await cache.SetStringAsync(BuildKey(TenantKey, PhoneE164Hash), legacyJson, CancellationToken.None);

        var result = await store.GetAsync(TenantKey, PhoneE164Hash, CancellationToken.None);

        result.Should().NotBeNull();
        result!.UserId.Should().Be("u-7");
        result.CandidateUserIds.Should().Equal(new[] { "u-7" });
    }

    [Fact]
    public async Task Legacy_Single_PreservesUserId()
    {
        var (store, cache) = BuildStore();

        const string legacyJson = "{" +
            "\"otpHash\":\"AQIDBA==\"," +
            "\"tenantKey\":\"tenant-a\"," +
            "\"phoneE164\":\"+84334336232\"," +
            "\"userId\":\"u-9\"," +
            "\"createdAtUtc\":\"2025-01-05T08:00:00+00:00\"," +
            "\"expiresAtUtc\":\"2025-01-05T08:05:00+00:00\"," +
            "\"attemptCount\":0" +
            "}";

        await cache.SetStringAsync(BuildKey(TenantKey, PhoneE164Hash), legacyJson, CancellationToken.None);

        var result = await store.GetAsync(TenantKey, PhoneE164Hash, CancellationToken.None);

        result.Should().NotBeNull();
        result!.UserId.Should().Be("u-9");
        result.CandidateUserIds.Should().HaveCount(1);
        result.CandidateUserIds.Single().Should().Be("u-9");
    }
}
