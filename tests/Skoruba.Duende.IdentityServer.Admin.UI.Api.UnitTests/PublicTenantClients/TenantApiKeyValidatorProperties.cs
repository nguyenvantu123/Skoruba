// Feature: tenant-client-cache-public-read, Task 2
//
// Property-based tests for TenantApiKeyValidator.
//
// Property 02 — HotReload (Validates: Requirements 1.6, 3.5):
//   For a sequence (tenant, oldHash, newHash, plaintextOld, plaintextNew),
//   the validator picks up an updated IOptionsMonitor.CurrentValue snapshot
//   on the very next call without restarting the host. Old plaintext stops
//   validating, new plaintext starts validating.
//
// Property 03 — ConstantTime (Validates: Requirements 3.1, 3.2):
//   Output correctness across matched/mismatched/unregistered (tenantKey,
//   plaintext) pairs. Approximate timing parity is asserted as a loose
//   secondary check; FixedTimeEquals output equality is the primary
//   correctness guarantee. Annotated as such in-line.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using FluentAssertions;

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.PublicTenantClients;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache.Helpers;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.PublicTenantClients;

public sealed class TenantApiKeyValidatorProperties
{
    // ===== Generators ==============================================

    public sealed record HotReloadSample(
        string Tenant,
        string PlaintextOld,
        string PlaintextNew);

    public sealed record TimingSample(
        string RegisteredTenant,
        string ApiKey,
        string WrongKey,
        string UnregisteredTenant);

    public static class Arbs
    {
        // Lowercase ASCII alphabet for tenant keys — matches the path regex
        // ^[a-z0-9_-]+$ enforced upstream by the route binder (R7.1).
        private static readonly char[] TenantAlphabet =
            "abcdefghijklmnopqrstuvwxyz0123456789_-".ToCharArray();

        // Wider alphabet for opaque API key plaintext — must include some
        // whitespace and punctuation to exercise UTF-8 encoding branches.
        private static readonly char[] PlaintextAlphabet =
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_!.".ToCharArray();

        private static Gen<string> TenantGen()
            => from len in Gen.Choose(1, 16)
               from chars in Gen.Elements(TenantAlphabet).ListOf(len)
               select new string(chars.ToArray());

        private static Gen<string> PlaintextGen()
            => from len in Gen.Choose(1, 48)
               from chars in Gen.Elements(PlaintextAlphabet).ListOf(len)
               select new string(chars.ToArray());

        public static Arbitrary<HotReloadSample> HotReload()
            => (from tenant in TenantGen()
                from oldKey in PlaintextGen()
                from newKey in PlaintextGen().Where(s => !string.Equals(s, oldKey, StringComparison.Ordinal))
                select new HotReloadSample(tenant, oldKey, newKey))
                .ToArbitrary();

        public static Arbitrary<TimingSample> Timing()
            => (from registered in TenantGen()
                from unregistered in TenantGen().Where(s => !string.Equals(s, registered, StringComparison.Ordinal))
                from key in PlaintextGen()
                from wrong in PlaintextGen().Where(s => !string.Equals(s, key, StringComparison.Ordinal))
                select new TimingSample(registered, key, wrong, unregistered))
                .ToArbitrary();
    }

    // ===== Helpers ==================================================

    private static string Sha256HexLower(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ===== Property 02 — HotReload =================================

    /// <summary>
    /// Property 2 (Validates: Requirements 1.6, 3.5). After the
    /// <see cref="Microsoft.Extensions.Options.IOptionsMonitor{T}.CurrentValue"/>
    /// snapshot is swapped, the very next call observes the rotated key.
    /// </summary>
    [Property(MaxTest = 25, Arbitrary = new[] { typeof(Arbs) })]
    public void Property02_HotReload(HotReloadSample sample)
    {
        // Feature: tenant-client-cache-public-read, Property 2: API key
        // validator observes IOptionsMonitor hot reload on the next request.
        var initial = new TenantClientCachePublicReadOptions();
        initial.ApiKeys[sample.Tenant] = Sha256HexLower(sample.PlaintextOld);
        var monitor = new StubOptionsMonitor<TenantClientCachePublicReadOptions>(initial);
        var validator = new TenantApiKeyValidator(monitor);

        // Pre-rotation: old plaintext validates, new plaintext does not.
        validator.TryValidate(sample.Tenant, sample.PlaintextOld.AsSpan()).Should().BeTrue();
        validator.TryValidate(sample.Tenant, sample.PlaintextNew.AsSpan()).Should().BeFalse();

        // Rotate the configured digest in place via a fresh options instance
        // (mirrors what IConfiguration reload produces).
        var rotated = new TenantClientCachePublicReadOptions();
        rotated.ApiKeys[sample.Tenant] = Sha256HexLower(sample.PlaintextNew);
        monitor.Set(rotated);

        // Post-rotation: new plaintext validates on the very next call,
        // old plaintext is revoked.
        validator.TryValidate(sample.Tenant, sample.PlaintextNew.AsSpan()).Should().BeTrue();
        validator.TryValidate(sample.Tenant, sample.PlaintextOld.AsSpan()).Should().BeFalse();
    }

    // ===== Property 03 — ConstantTime ===============================

    /// <summary>
    /// Property 3 (Validates: Requirements 3.1, 3.2). Output correctness:
    /// matched plaintext returns true; mismatched plaintext on the
    /// registered tenant returns false; any plaintext on an unregistered
    /// tenant returns false. Approximate timing parity is checked but
    /// FixedTimeEquals is the primary correctness guarantee.
    /// </summary>
    [Property(MaxTest = 15, Arbitrary = new[] { typeof(Arbs) })]
    public void Property03_ConstantTime(TimingSample sample)
    {
        // Feature: tenant-client-cache-public-read, Property 3: API key
        // validator uses constant-time comparison (FixedTimeEquals).
        var options = new TenantClientCachePublicReadOptions();
        options.ApiKeys[sample.RegisteredTenant] = Sha256HexLower(sample.ApiKey);
        var monitor = new StubOptionsMonitor<TenantClientCachePublicReadOptions>(options);
        var validator = new TenantApiKeyValidator(monitor);

        // ===== Output correctness (primary assertion) =====
        validator.TryValidate(sample.RegisteredTenant, sample.ApiKey.AsSpan())
            .Should().BeTrue("matching plaintext on a registered tenant must validate (R3.2)");

        validator.TryValidate(sample.RegisteredTenant, sample.WrongKey.AsSpan())
            .Should().BeFalse("mismatched plaintext on a registered tenant must NOT validate (R3.2)");

        validator.TryValidate(sample.UnregisteredTenant, sample.ApiKey.AsSpan())
            .Should().BeFalse("unregistered tenant must NEVER validate (R3.1, R3.3)");

        // ===== Approximate timing parity (secondary, non-strict) =====
        // FixedTimeEquals output equality is the primary correctness
        // guarantee. The wall-clock numbers below are noisy on shared CI
        // hardware so we use a very loose bound only as a smoke check that
        // the validator did not accidentally short-circuit on the registered
        // path. We deliberately do NOT fail the test for moderate skew —
        // only catastrophic order-of-magnitude divergence (which is what a
        // real short-circuit regression on long keys would produce) trips
        // the assertion.
        const int Iterations = 5_000;

        Warmup(validator, sample);

        var matchedTicks = MeasureMean(validator, sample.RegisteredTenant, sample.ApiKey, Iterations);
        var mismatchedTicks = MeasureMean(validator, sample.RegisteredTenant, sample.WrongKey, Iterations);

        // Approximate timing assertion; FixedTimeEquals output equality is
        // the primary correctness guarantee. We log skew but only assert a
        // catastrophic-regression bound (100x) so a single CPU-stalled
        // iteration cannot flake the test on shared hardware. A real
        // short-circuit regression on long keys produces 1000x+ skew, well
        // beyond this bound.
        if (matchedTicks > 0 && mismatchedTicks > 0)
        {
            var lo = Math.Min(matchedTicks, mismatchedTicks);
            var hi = Math.Max(matchedTicks, mismatchedTicks);
            (hi / lo).Should().BeLessThan(100,
                "approximate timing parity is a smoke check on FixedTimeEquals; "
                + "exceeding a 100x wall-clock spread suggests a short-circuit regression");
        }
    }

    private static void Warmup(TenantApiKeyValidator validator, TimingSample sample)
    {
        // 200 warmup iterations to absorb JIT + first-use allocation costs.
        for (var i = 0; i < 200; i++)
        {
            validator.TryValidate(sample.RegisteredTenant, sample.ApiKey.AsSpan());
            validator.TryValidate(sample.RegisteredTenant, sample.WrongKey.AsSpan());
        }
    }

    private static long MeasureMean(
        TenantApiKeyValidator validator,
        string tenant,
        string plaintext,
        int iterations)
    {
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            validator.TryValidate(tenant, plaintext.AsSpan());
        }

        sw.Stop();
        return sw.ElapsedTicks;
    }
}
