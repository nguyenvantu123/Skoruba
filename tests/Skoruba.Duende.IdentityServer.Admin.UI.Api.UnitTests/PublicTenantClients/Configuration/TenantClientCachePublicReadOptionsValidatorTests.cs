// Feature: tenant-client-cache-public-read, Task 1
//
// Unit tests + Property01 for TenantClientCachePublicReadOptionsValidator.
// Covers acceptance criteria R1.1, R1.2, R1.3, R1.4, R1.5, R1.6, R1.7, R1.8,
// R1.9, R4.3, R4.4, R5.6, R5.7, R6.2, R9.6, R17.1.
//
// Property 01 (Validates: Requirements 1.4, 1.5, 4.3, 4.4, 5.6, 5.7, 6.2, 9.6):
//   ValidatorRejects_Without_Leaking_Values — for an arbitrary malformed
//   options instance, Validate() returns Fail AND no failure message
//   contains the raw API-key hash value substring.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using FluentAssertions;

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Moq;

using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.PublicTenantClients.Configuration;

public class TenantClientCachePublicReadOptionsValidatorTests
{
    private const string Section = TenantClientCachePublicReadOptions.SectionName;

    // ===== Helpers ==================================================

    private static IHostEnvironment Environment(string environmentName)
    {
        var mock = new Mock<IHostEnvironment>();
        mock.SetupGet(e => e.EnvironmentName).Returns(environmentName);
        mock.SetupGet(e => e.ApplicationName).Returns("UnitTest");
        mock.SetupGet(e => e.ContentRootPath).Returns(string.Empty);
        return mock.Object;
    }

    private static TenantClientCachePublicReadOptionsValidator NewValidator(
        string? environmentName = null) =>
        new(Environment(environmentName ?? Environments.Development));

    private static TenantClientCachePublicReadOptions Defaults() => new();

    // ===== Examples =================================================

    [Fact]
    public void Defaults_Are_Valid_When_ApiKeys_Empty_And_NotProduction()
    {
        // R1.7 — empty store is allowed (every request will 401 at runtime).
        // R1.2/R1.3 — defaults match the design glossary.
        var validator = NewValidator(Environments.Development);
        var options = Defaults();

        var result = validator.Validate(name: null, options);

        result.Succeeded.Should().BeTrue();
        result.Failures.Should().BeNull();
    }

    [Fact]
    public void ApiKey_Hash_Not_64_Hex_Lowercase_Fails_NamesKeyButNotValue()
    {
        // R1.4 — failure message must name the offending tenant key but
        // MUST NOT include the hash value (anti-leak).
        const string offendingHash = "deadbeef-not-a-valid-sha256-hash-but-long-enough-to-leak-fail";
        var options = Defaults();
        options.ApiKeys["acme"] = offendingHash;

        var result = NewValidator().Validate(name: null, options);

        result.Failed.Should().BeTrue();
        var failure = result.Failures!.Single();
        failure.Should().Contain($"{Section}:ApiKeys[acme]");
        failure.Should().NotContain(offendingHash,
            "R1.4: validator MUST NOT echo the API-key hash value in error messages");
    }

    [Theory]
    [InlineData("DEADBEEF1234567890DEADBEEF1234567890DEADBEEF1234567890DEADBEEF12")] // uppercase hex
    [InlineData("deadbeef1234567890deadbeef1234567890deadbeef1234567890deadbeef")]   // 62 chars
    [InlineData("deadbeef1234567890deadbeef1234567890deadbeef1234567890deadbeef1234")] // 66 chars
    [InlineData("zzzzbeef1234567890deadbeef1234567890deadbeef1234567890deadbeef12")] // non-hex char
    [InlineData("")]                                                                 // empty
    public void ApiKey_Hash_Wrong_Format_Fails(string hash)
    {
        // R1.4
        var options = Defaults();
        options.ApiKeys["acme"] = hash;

        var result = NewValidator().Validate(name: null, options);

        result.Failed.Should().BeTrue();
        var failure = result.Failures!.Single(f => f.Contains("ApiKeys[acme]"));
        failure.Should().Contain($"{Section}:ApiKeys[acme]");
        if (hash.Length > 0)
        {
            failure.Should().NotContain(hash, "R1.4: never echo the offending hash value");
        }
    }

    [Theory]
    [InlineData(" acme")]      // leading whitespace
    [InlineData("acme ")]      // trailing whitespace
    [InlineData(" acme ")]     // surrounding whitespace
    [InlineData("Acme")]       // uppercase
    [InlineData("ACME")]       // all uppercase
    public void ApiKey_TenantKey_Uppercase_Or_Whitespace_Fails(string tenantKey)
    {
        // R1.5 — tenant keys must be trimmed + lowercase.
        const string validHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var options = Defaults();
        options.ApiKeys[tenantKey] = validHash;

        var result = NewValidator().Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures!.Should().Contain(f => f.Contains($"'{tenantKey}'"));
        // Defensive: never leak hash even on tenant-key validation failures.
        result.Failures!.Should().NotContain(f => f.Contains(validHash));
    }

    [Theory]
    [InlineData(0)]      // below min
    [InlineData(-1)]     // negative
    [InlineData(10_001)] // above max
    public void RateLimit_TokenLimit_Out_Of_Range_Fails_NamesKeyAndValue(int tokenLimit)
    {
        // R4.3 — TokenLimit ∈ [1, 10000].
        var options = Defaults();
        options.RateLimit.TokenLimit = tokenLimit;

        var result = NewValidator().Validate(name: null, options);

        result.Failed.Should().BeTrue();
        var failure = result.Failures!.Single();
        failure.Should().Contain($"{Section}:RateLimit:TokenLimit");
        failure.Should().Contain(tokenLimit.ToString(CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("00:00:00")]  // exactly zero — below min
    [InlineData("01:00:01")]  // 1 sec over max
    [InlineData("02:00:00")]  // 2h over max
    public void RateLimit_ReplenishmentPeriod_Out_Of_Range_Fails(string period)
    {
        // R4.4 — ReplenishmentPeriod ∈ [00:00:01, 01:00:00].
        var options = Defaults();
        options.RateLimit.ReplenishmentPeriod = TimeSpan.Parse(period, CultureInfo.InvariantCulture);

        var result = NewValidator().Validate(name: null, options);

        result.Failed.Should().BeTrue();
        var failure = result.Failures!.Single();
        failure.Should().Contain($"{Section}:RateLimit:ReplenishmentPeriod");
    }

    [Theory]
    [InlineData("http://attacker.example")]  // non-https, non-localhost
    [InlineData("ftp://example.com")]         // wrong scheme
    [InlineData("not-a-url")]                 // not absolute
    [InlineData("https:///nohost")]           // malformed
    public void Cors_AllowedOrigins_NonHttps_NonLocalhost_Fails_NamesEntry(string origin)
    {
        // R5.6 — allowed origins must be https (or http+localhost).
        var options = Defaults();
        options.Cors.AllowedOrigins.Add(origin);

        var result = NewValidator().Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures!.Should().Contain(f => f.Contains($"'{origin}'"));
    }

    [Theory]
    [InlineData("https://app.example.com")]
    [InlineData("https://localhost:5001")]
    [InlineData("http://localhost")]
    [InlineData("http://localhost:44303")]
    public void Cors_AllowedOrigins_Valid_Schemes_Allowed(string origin)
    {
        // R5.6 — https or http+localhost are valid.
        var options = Defaults();
        options.Cors.AllowedOrigins.Add(origin);

        var result = NewValidator().Validate(name: null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(86_401)]
    public void Cors_PreflightMaxAge_Out_Of_Range_Fails(int seconds)
    {
        // R5.7 — PreflightMaxAgeSeconds ∈ [0, 86400].
        var options = Defaults();
        options.Cors.PreflightMaxAgeSeconds = seconds;

        var result = NewValidator().Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures!.Single().Should().Contain($"{Section}:Cors:PreflightMaxAgeSeconds");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3601)]
    public void ResponseCache_MaxAge_Out_Of_Range_Fails(int seconds)
    {
        // R6.2 — ResponseCache:MaxAgeSeconds ∈ [0, 3600].
        var options = Defaults();
        options.ResponseCache.MaxAgeSeconds = seconds;

        var result = NewValidator().Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures!.Single().Should().Contain($"{Section}:ResponseCache:MaxAgeSeconds");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Audit_RemoteIpSalt_Empty_In_Production_Fails(string salt)
    {
        // R9.6 — RemoteIpSalt MUST be non-empty in Production.
        var options = Defaults();
        options.Audit.RemoteIpSalt = salt;

        var result = new TenantClientCachePublicReadOptionsValidator(
            Environment(Environments.Production)).Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures!.Single().Should().Contain($"{Section}:Audit:RemoteIpSalt");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Audit_RemoteIpSalt_Empty_In_Development_Allowed(string salt)
    {
        // R9.6 — Production-only fail-fast. Dev / Staging tolerate empty salt.
        var options = Defaults();
        options.Audit.RemoteIpSalt = salt;

        var result = new TenantClientCachePublicReadOptionsValidator(
            Environment(Environments.Development)).Validate(name: null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void RateLimit_TokenLimit_Boundaries_Allowed()
    {
        // R4.3 boundaries: 1 and 10000 are inclusive.
        var validator = NewValidator();

        var lower = Defaults();
        lower.RateLimit.TokenLimit = 1;
        validator.Validate(null, lower).Succeeded.Should().BeTrue();

        var upper = Defaults();
        upper.RateLimit.TokenLimit = 10_000;
        validator.Validate(null, upper).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void RateLimit_ReplenishmentPeriod_Boundaries_Allowed()
    {
        // R4.4 boundaries: 1 second and 1 hour are inclusive.
        var validator = NewValidator();

        var lower = Defaults();
        lower.RateLimit.ReplenishmentPeriod = TimeSpan.FromSeconds(1);
        validator.Validate(null, lower).Succeeded.Should().BeTrue();

        var upper = Defaults();
        upper.RateLimit.ReplenishmentPeriod = TimeSpan.FromHours(1);
        validator.Validate(null, upper).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Multiple_Failures_Aggregated_Into_Single_ValidateOptionsResult()
    {
        // R17.1 — fail-fast aggregates all errors at startup.
        var options = Defaults();
        options.RateLimit.TokenLimit = 0;
        options.Cors.PreflightMaxAgeSeconds = -1;
        options.ResponseCache.MaxAgeSeconds = 4000;

        var result = NewValidator().Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().NotBeNull();
        result.Failures!.Count().Should().BeGreaterThanOrEqualTo(3);
    }

    // ===== Property 01 — ValidatorRejects_Without_Leaking_Values =====

    /// <summary>
    /// Property 1 (Validates: Requirements 1.4, 1.5, 4.3, 4.4, 5.6, 5.7, 6.2,
    /// 9.6). For any malformed <see cref="TenantClientCachePublicReadOptions"/>
    /// (random uppercase tenant key, random non-hex hash, out-of-range
    /// numerics, non-https origin, prod env empty salt) the validator returns
    /// Fail AND no failure message contains the raw hash value substring.
    /// </summary>
    [Property(MaxTest = 25, Arbitrary = new[] { typeof(MalformedArbs) })]
    public void Property01_ValidatorRejects_Without_Leaking_Values(MalformedOptions sample)
    {
        // Feature: tenant-client-cache-public-read, Property 1: Options validator
        // rejects malformed entries without leaking values.
        var validator = new TenantClientCachePublicReadOptionsValidator(
            Environment(sample.Production ? Environments.Production : Environments.Development));

        var result = validator.Validate(null, sample.Options);

        result.Failed.Should().BeTrue("malformed options must always fail validation");
        result.Failures.Should().NotBeNull();

        // Anti-leak: no failure message may include the raw API-key hash
        // values that were planted into ApiKeys.
        foreach (var hash in sample.PlantedHashes.Where(h => h.Length >= 16))
        {
            foreach (var failure in result.Failures!)
            {
                failure.Should().NotContain(hash,
                    "R1.4 + P1: validator MUST never echo API-key hash values, even partially");
            }
        }
    }

    // ===== Generators ==============================================

    public sealed record MalformedOptions(
        TenantClientCachePublicReadOptions Options,
        IReadOnlyList<string> PlantedHashes,
        bool Production);

    public static class MalformedArbs
    {
        public static Arbitrary<MalformedOptions> MalformedOptions()
            => Generator().ToArbitrary();

        private static Gen<MalformedOptions> Generator()
            => from tenantKey in TenantKeyGen()
               from badHash in BadHashGen()
               from extraHash in BadHashGen()
               from tokenLimit in OutOfRangeIntGen(min: 1, max: 10_000)
               from periodSeconds in OutOfRangePeriodSecondsGen()
               from origin in BadOriginGen()
               from preflight in OutOfRangeIntGen(min: 0, max: 86_400)
               from maxAge in OutOfRangeIntGen(min: 0, max: 3600)
               from saltMode in Gen.Choose(0, 3)
               select Build(tenantKey, badHash, extraHash, tokenLimit, periodSeconds, origin, preflight, maxAge, saltMode);

        private static MalformedOptions Build(
            string tenantKey,
            string badHash,
            string extraHash,
            int tokenLimit,
            int periodSeconds,
            string origin,
            int preflight,
            int maxAge,
            int saltMode)
        {
            var options = new TenantClientCachePublicReadOptions
            {
                ApiKeys = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [tenantKey] = badHash,
                    ["acme"] = extraHash, // second entry to exercise iteration
                },
            };
            options.RateLimit.TokenLimit = tokenLimit;
            options.RateLimit.ReplenishmentPeriod = TimeSpan.FromSeconds(periodSeconds);
            options.Cors.AllowedOrigins.Add(origin);
            options.Cors.PreflightMaxAgeSeconds = preflight;
            options.ResponseCache.MaxAgeSeconds = maxAge;

            // Vary salt + production so R9.6 is exercised in some — but not all — runs.
            var production = saltMode == 0;
            options.Audit.RemoteIpSalt = production ? "" : "dev-salt";

            return new MalformedOptions(options, new[] { badHash, extraHash }, production);
        }

        private static Gen<string> TenantKeyGen()
            => Gen.Elements(" acme", "acme ", "ACME", "Acme", "TENANT-1");

        private static Gen<string> BadHashGen()
            // Generates 16+ char strings that are guaranteed NOT to match the
            // 64-char lowercased hex regex (uppercase, wrong length, non-hex).
            => Gen.Elements(
                "DEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEF", // uppercase
                "deadbeef0123456789",                                                // too short (18)
                "deadbeef0123456789deadbeef0123456789deadbeef0123456789deadbeef0123456789", // too long (72)
                "zzzzzzzzzzzzzzzzdeadbeef0123456789deadbeef0123456789deadbeef0123", // non-hex chars
                "0123_456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"); // underscore disqualifies

        private static Gen<int> OutOfRangeIntGen(int min, int max)
            => Gen.OneOf(
                Gen.Choose(min - 1000, min - 1),
                Gen.Choose(max + 1, max + 1000));

        private static Gen<int> OutOfRangePeriodSecondsGen()
            // ReplenishmentPeriod ∈ [1s, 3600s] → out-of-range covers 0 (below)
            // or > 3600 (above).
            => Gen.OneOf(
                Gen.Constant(0),
                Gen.Choose(3601, 7200));

        private static Gen<string> BadOriginGen()
            => Gen.Elements(
                "http://attacker.example",
                "ftp://example.com",
                "not-a-url",
                "//missing-scheme",
                "javascript:alert(1)");
    }
}
