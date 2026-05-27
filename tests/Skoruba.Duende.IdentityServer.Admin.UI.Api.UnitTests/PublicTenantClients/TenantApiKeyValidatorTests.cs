// Feature: tenant-client-cache-public-read, Task 2
//
// Example-based tests for TenantApiKeyValidator covering acceptance criteria
// R1.6 (hot reload visibility), R3.1 (missing entry), R3.2 (constant-time
// comparison), R3.5 (no caching across requests).

#nullable enable

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

using FluentAssertions;

using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.PublicTenantClients;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache.Helpers;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.PublicTenantClients;

public class TenantApiKeyValidatorTests
{
    private static string Sha256HexLower(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static (TenantApiKeyValidator validator, StubOptionsMonitor<TenantClientCachePublicReadOptions> monitor)
        Build(IDictionary<string, string>? apiKeys = null)
    {
        var options = new TenantClientCachePublicReadOptions();
        if (apiKeys is not null)
        {
            options.ApiKeys = new Dictionary<string, string>(apiKeys, StringComparer.Ordinal);
        }

        var monitor = new StubOptionsMonitor<TenantClientCachePublicReadOptions>(options);
        return (new TenantApiKeyValidator(monitor), monitor);
    }

    [Fact]
    public void MatchingHash_Returns_True()
    {
        const string tenant = "acme";
        const string apiKey = "super-secret-token-1234";
        var (validator, _) = Build(new Dictionary<string, string>
        {
            [tenant] = Sha256HexLower(apiKey),
        });

        validator.TryValidate(tenant, apiKey.AsSpan()).Should().BeTrue();
    }

    [Fact]
    public void MismatchedHash_Returns_False()
    {
        const string tenant = "acme";
        var (validator, _) = Build(new Dictionary<string, string>
        {
            [tenant] = Sha256HexLower("the-real-key"),
        });

        validator.TryValidate(tenant, "the-wrong-key".AsSpan()).Should().BeFalse();
    }

    [Fact]
    public void UnregisteredTenant_Returns_False()
    {
        var (validator, _) = Build(new Dictionary<string, string>
        {
            ["acme"] = Sha256HexLower("key"),
        });

        // Unregistered tenant — even with a plaintext that hashes to *some*
        // configured value, the lookup must miss.
        validator.TryValidate("contoso", "key".AsSpan()).Should().BeFalse();
    }

    [Fact]
    public void EmptyStore_Returns_False()
    {
        // R1.7 boundary — empty Api_Key_Store, every request must return false
        // (handler then translates to 401 invalid_api_key).
        var (validator, _) = Build();

        validator.TryValidate("acme", "any-key".AsSpan()).Should().BeFalse();
    }

    [Fact]
    public void Whitespace_ApiKey_Computes_DifferentHash_DoesNotCrash()
    {
        const string tenant = "acme";
        const string realKey = "real-key";
        var (validator, _) = Build(new Dictionary<string, string>
        {
            [tenant] = Sha256HexLower(realKey),
        });

        // A whitespace-only header is hashed and compared like any other
        // plaintext — must not match the configured digest, must not throw.
        validator.TryValidate(tenant, "   ".AsSpan()).Should().BeFalse();
        validator.TryValidate(tenant, "\t".AsSpan()).Should().BeFalse();
        validator.TryValidate(tenant, ReadOnlySpan<char>.Empty).Should().BeFalse();

        // Sanity check: real key still validates after the empty-span call,
        // i.e. the validator did not corrupt internal state.
        validator.TryValidate(tenant, realKey.AsSpan()).Should().BeTrue();
    }

    [Fact]
    public void TryParseHexLower_Rejects_Mixed_Case()
    {
        // Defensive — a configuration that bypassed the validator and stores
        // an uppercase digest must still fail closed (return false), never
        // succeed accidentally. We exercise this by storing an uppercase
        // version of the correct hash; even a matching plaintext must fail.
        const string tenant = "acme";
        const string apiKey = "any-key";
        var lowerHash = Sha256HexLower(apiKey);
        var upperHash = lowerHash.ToUpperInvariant();
        var (validator, _) = Build(new Dictionary<string, string>
        {
            [tenant] = upperHash,
        });

        validator.TryValidate(tenant, apiKey.AsSpan()).Should().BeFalse(
            "TryParseHexLower MUST reject uppercase hex even when the bytes would otherwise match");
    }

    [Fact]
    public void TryParseHexLower_Rejects_Wrong_Length()
    {
        // Defense-in-depth — wrong-length digest must fail closed.
        const string tenant = "acme";
        const string apiKey = "any-key";
        var hash = Sha256HexLower(apiKey);

        var (validator, _) = Build(new Dictionary<string, string>
        {
            [tenant] = hash[..63], // 63 chars
        });
        validator.TryValidate(tenant, apiKey.AsSpan()).Should().BeFalse();
    }

    [Fact]
    public void HotReload_New_Hash_Validates_On_Next_Call()
    {
        // R1.6 + R3.5 — every call re-reads IOptionsMonitor.CurrentValue,
        // so swapping the underlying snapshot is observable on the very
        // next call without restarting the validator.
        const string tenant = "acme";
        const string oldKey = "old-key";
        const string newKey = "new-key";

        var initial = new TenantClientCachePublicReadOptions();
        initial.ApiKeys[tenant] = Sha256HexLower(oldKey);
        var monitor = new StubOptionsMonitor<TenantClientCachePublicReadOptions>(initial);
        var validator = new TenantApiKeyValidator(monitor);

        validator.TryValidate(tenant, oldKey.AsSpan()).Should().BeTrue();

        // Swap the hash — this is exactly what IConfiguration.Reload would do.
        var rotated = new TenantClientCachePublicReadOptions();
        rotated.ApiKeys[tenant] = Sha256HexLower(newKey);
        monitor.Set(rotated);

        validator.TryValidate(tenant, newKey.AsSpan()).Should().BeTrue();
        validator.TryValidate(tenant, oldKey.AsSpan()).Should()
            .BeFalse("R1.6: hot reload revokes the previous key on the next request");
    }

    [Fact]
    public void Caller_Must_Pre_Normalize_TenantKey()
    {
        // R2.3 — caller MUST normalize the tenant key before invoking the
        // validator. Passing a non-normalized key (e.g. "ACME") simply
        // misses the lookup; this test documents that contract.
        const string tenant = "acme";
        const string apiKey = "key";
        var (validator, _) = Build(new Dictionary<string, string>
        {
            [tenant] = Sha256HexLower(apiKey),
        });

        validator.TryValidate("ACME", apiKey.AsSpan()).Should().BeFalse();
        validator.TryValidate("  acme  ", apiKey.AsSpan()).Should().BeFalse();
        validator.TryValidate(tenant, apiKey.AsSpan()).Should().BeTrue();
    }

    [Fact]
    public void Constructor_Throws_On_Null_Options()
    {
        Action act = () => _ = new TenantApiKeyValidator(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("options");
    }
}
