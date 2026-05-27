// Feature: phone-otp-multi-account-select, Property 4: OtpStoreRecord serialization round-trip and backward compatibility
//
// Validates: Requirements 2.6, 14.4
//
// Two invariants in one property (single FsCheck property per file per
// Section 10.3 design):
//   (a) For every random OtpStoreRecord r:
//          Deserialize(Serialize(r)) == r
//       i.e. JSON round-trip via System.Text.Json with camelCase preserves
//       field-by-field equality on the record.
//   (b) For every legacy JSON shape j (no `candidateUserIds` field):
//          (await store.GetAsync(...)).CandidateUserIds == [j.UserId]
//       i.e. RedisPhoneOtpStore.GetAsync applies the documented
//       backward-compat fallback (Section 3.1 design, R2.6).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
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
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Models;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Storage;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.UnitTests.Properties;

public sealed class Property04_RecordSerializationRoundTrip
{
    private const string TenantKeyForCache = "tenant-a";
    private const string PhoneE164HashForCache = "f1d2deadbeef";
    private const string Prefix = "otp:";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Wraps an arbitrary <see cref="OtpStoreRecord"/> alongside a reference
    /// payload used to derive the legacy JSON shape (no
    /// <c>candidateUserIds</c> field). Keeping both pieces of data in one
    /// scenario makes the property assert (a) and (b) on the SAME random
    /// inputs.
    /// </summary>
    public sealed record SerializationScenario(OtpStoreRecord Record, string LegacyUserId);

    public static class Arbs
    {
        // Bound the shape of random data to keep the property fast while still
        // exercising the documented input space.
        //   - otpHash: 16..32 random bytes.
        //   - tenantKey, phoneE164, userId: short ascii pool — JSON-safe.
        //   - candidateUserIds: 1..5 elements, first element MUST equal userId
        //     to satisfy R2.4 (UserId == CandidateUserIds[0]).
        //   - dates: bounded UTC range to avoid DateTimeOffset overflow.
        private static readonly char[] AsciiPool =
            "abcdefghijklmnopqrstuvwxyz0123456789-_".ToCharArray();

        private static Gen<string> ShortIdGen()
            => from len in Gen.Choose(1, 8)
               from chars in Gen.Elements(AsciiPool).ListOf(len)
               select new string(chars.ToArray());

        private static Gen<byte[]> HashGen()
            => from len in Gen.Choose(16, 32)
               from values in Gen.Choose(0, 255).ListOf(len)
               select values.Select(v => (byte)v).ToArray();

        private static Gen<DateTimeOffset> DateGen()
            => from offsetSeconds in Gen.Choose(0, 365 * 24 * 3600)
               select new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(offsetSeconds);

        public static Arbitrary<SerializationScenario> Scenario()
        {
            var gen =
                from hash in HashGen()
                from tenant in ShortIdGen()
                from phone in ShortIdGen()
                from primary in ShortIdGen()
                from extraCount in Gen.Choose(0, 4)
                from extras in ShortIdGen().ListOf(extraCount)
                from createdOffset in Gen.Choose(0, 365 * 24 * 3600)
                from ttlSeconds in Gen.Choose(60, 600)
                from attempts in Gen.Choose(0, 5)
                let createdAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(createdOffset)
                let candidateIds = (new[] { primary }).Concat(extras).ToArray()
                select new SerializationScenario(
                    new OtpStoreRecord
                    {
                        OtpHash = hash,
                        TenantKey = tenant,
                        PhoneE164 = "+" + phone,
                        UserId = primary,
                        CandidateUserIds = candidateIds,
                        CreatedAtUtc = createdAt,
                        ExpiresAtUtc = createdAt.AddSeconds(ttlSeconds),
                        AttemptCount = attempts,
                    },
                    primary);
            return gen.ToArbitrary();
        }
    }

    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Arbs) })]
    public async Task NewShape_RoundTrips_AndLegacyJson_FallsBackToSingleElementCandidateSet(SerializationScenario scenario)
    {
        // (a) Round-trip.
        var json = JsonSerializer.Serialize(scenario.Record, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<OtpStoreRecord>(json, JsonOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.OtpHash.Should().Equal(scenario.Record.OtpHash);
        roundTripped.TenantKey.Should().Be(scenario.Record.TenantKey);
        roundTripped.PhoneE164.Should().Be(scenario.Record.PhoneE164);
        roundTripped.UserId.Should().Be(scenario.Record.UserId);
        roundTripped.CandidateUserIds.Should().Equal(scenario.Record.CandidateUserIds);
        roundTripped.CreatedAtUtc.Should().Be(scenario.Record.CreatedAtUtc);
        roundTripped.ExpiresAtUtc.Should().Be(scenario.Record.ExpiresAtUtc);
        roundTripped.AttemptCount.Should().Be(scenario.Record.AttemptCount);

        // (b) Legacy JSON: build by removing `candidateUserIds` from the new
        // shape. Use JsonNode so we mutate field-shape without rebuilding the
        // whole document by hand.
        var legacyNode = JsonNode.Parse(json)!.AsObject();
        legacyNode.Remove("candidateUserIds");
        var legacyJson = legacyNode.ToJsonString();

        // Sanity check: the synthetic legacy shape really lacks the field.
        legacyJson.Should().NotContain("candidateUserIds");

        // Plant the legacy JSON directly into the cache backing the store and
        // exercise GetAsync — that is the documented public surface where the
        // backward-compat fallback applies (Section 3.1 design).
        var (store, cache) = BuildStore();
        await cache.SetStringAsync(BuildKey(TenantKeyForCache, PhoneE164HashForCache), legacyJson, CancellationToken.None);

        var read = await store.GetAsync(TenantKeyForCache, PhoneE164HashForCache, CancellationToken.None);

        read.Should().NotBeNull();
        read!.UserId.Should().Be(scenario.LegacyUserId);
        read.CandidateUserIds.Should().Equal(new[] { scenario.LegacyUserId },
            because: "legacy records without candidateUserIds must fall back to [UserId] (R2.6)");
    }

    private static (RedisPhoneOtpStore Store, IDistributedCache Cache) BuildStore()
    {
        IDistributedCache cache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));

        var options = Options.Create(new PhoneOtpLoginConfiguration
        {
            RedisKeyPrefix = Prefix,
        });

        return (new RedisPhoneOtpStore(cache, options), cache);
    }

    private static string BuildKey(string tenantKey, string phoneE164Hash)
        => $"{Prefix}rec:{tenantKey}:{phoneE164Hash}";
}
