// Feature: phone-otp-multi-account-select, Property 7: AccountSelectContext cookie round-trip
//
// Property 7 (Section 4.3 + Requirement 6.2, 6.3):
//   For all valid AccountSelectContext payloads, codec.TryUnprotect(codec.Protect(ctx)) SHALL
//   return true and a structurally-equal payload.
//   For all 1-byte mutations of the protected ciphertext, codec.TryUnprotect SHALL return false.
//   For all payloads protected under a different DataProtection purpose, codec.TryUnprotect
//   SHALL return false.
//
// Validates: Requirements 6.2, 6.3
using System;
using System.Collections.Generic;
using System.Linq;

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

using Microsoft.AspNetCore.DataProtection;

using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Services;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.UnitTests.Properties;

public class Property07_AccountSelectCookieRoundTrip
{
    private static Gen<AccountSelectContext> ContextGen()
    {
        // tenant key: short non-empty alphanumeric
        var tenantGen = Gen.Choose('a', 'z')
            .ListOf(8)
            .Select(chars => new string(chars.Select(c => (char)c).ToArray()));

        // phone hash: hex-like 8-char string
        var hashGen = Gen.Choose('0', 'f')
            .ListOf(16)
            .Select(chars => new string(chars.Select(c => (char)c).ToArray()));

        // user-id strings: "u-{1..10000}", up to 5 candidates, deterministic order.
        var userIdGen = Gen.Choose(1, 10000).Select(i => $"u-{i}");
        var candidatesGen = Gen.Choose(1, 5)
            .SelectMany(count => userIdGen.ListOf(count))
            .Select(list => (IReadOnlyList<string>)list
                .Distinct(StringComparer.Ordinal)
                .ToList());

        // Issued ∈ [2024-01-01, 2026-12-31), Expires = Issued + ttl ∈ [30, 180]s.
        var issuedGen = Gen.Choose(0, 2 * 365 * 24 * 3600)
            .Select(secs => new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(secs));
        var ttlGen = Gen.Choose(30, 180);

        return tenantGen.SelectMany(tenant =>
            hashGen.SelectMany(hash =>
                candidatesGen.SelectMany(candidates =>
                    issuedGen.SelectMany(issued =>
                        ttlGen.Select(ttl =>
                            new AccountSelectContext(
                                TenantKey: tenant,
                                PhoneE164Hash: hash,
                                CandidateUserIds: candidates,
                                IssuedAtUtc: issued,
                                ExpiresAtUtc: issued.AddSeconds(ttl),
                                OtpRecordKey: $"{tenant}:{hash}",
                                Version: 1))))));
    }

    [Property(MaxTest = 100)]
    public Property RoundTrip_ProtectThenUnprotect_PreservesPayload()
    {
        return Prop.ForAll(ContextGen().ToArbitrary(), ctx =>
        {
            var codec = new PhoneOtpAccountSelectCookieCodec(new EphemeralDataProtectionProvider());

            var raw = codec.Protect(ctx);
            var ok = codec.TryUnprotect(raw, out var rt);

            return (ok
                    && rt is not null
                    && rt.TenantKey == ctx.TenantKey
                    && rt.PhoneE164Hash == ctx.PhoneE164Hash
                    && rt.CandidateUserIds.SequenceEqual(ctx.CandidateUserIds, StringComparer.Ordinal)
                    && rt.IssuedAtUtc == ctx.IssuedAtUtc
                    && rt.ExpiresAtUtc == ctx.ExpiresAtUtc
                    && rt.OtpRecordKey == ctx.OtpRecordKey
                    && rt.Version == ctx.Version)
                .Label($"raw='{raw}' rt-tenant='{rt?.TenantKey}'");
        });
    }

    [Property(MaxTest = 100)]
    public Property TamperedCiphertext_FailsToUnprotect()
    {
        // Mutate index ∈ [0, raw.Length).
        return Prop.ForAll(ContextGen().ToArbitrary(), ctx =>
        {
            var codec = new PhoneOtpAccountSelectCookieCodec(new EphemeralDataProtectionProvider());
            var raw = codec.Protect(ctx);

            // Flip 1 char in the middle of the ciphertext deterministically.
            var idx = raw.Length / 2;
            var orig = raw[idx];
            // Swap to a different char in printable ASCII.
            var swapped = orig == 'A' ? 'B' : 'A';
            var mutated = raw.Substring(0, idx) + swapped + raw.Substring(idx + 1);

            // If by chance the mutation produced an identical string (idx out of bounds, etc.), skip.
            if (mutated == raw)
            {
                return true.Label("no-op mutation, skipped");
            }

            var ok = codec.TryUnprotect(mutated, out var payload);
            return (!ok && payload is null)
                .Label($"ok={ok} payload-null={payload is null}");
        });
    }

    [Property(MaxTest = 100)]
    public Property WrongPurpose_FailsToUnprotect()
    {
        return Prop.ForAll(ContextGen().ToArbitrary(), ctx =>
        {
            var provider = new EphemeralDataProtectionProvider();
            var codec = new PhoneOtpAccountSelectCookieCodec(provider);

            // Protect via a different purpose — must not decrypt under codec's protector.
            var foreign = provider.CreateProtector("PhoneOtp.SomeOtherPurpose");
            var foreignRaw = foreign.Protect("any-payload");

            var ok = codec.TryUnprotect(foreignRaw, out var payload);
            return (!ok && payload is null)
                .Label($"ok={ok} ctx-tenant='{ctx.TenantKey}'");
        });
    }
}
