using System;
using System.Collections.Generic;

using FluentAssertions;

using Microsoft.AspNetCore.DataProtection;

using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Services;

using Xunit;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.UnitTests.Services;

/// <summary>
/// Unit tests cho <see cref="PhoneOtpAccountSelectCookieCodec"/> (Section 4.3 design).
/// Validates Requirements 6.1, 6.2, 6.3.
/// </summary>
public class PhoneOtpAccountSelectCookieCodecTests
{
    private static AccountSelectContext SamplePayload() => new(
        TenantKey: "tenant-a",
        PhoneE164Hash: "f1d2c3b4a5968778695a4b3c2d1e0f00",
        CandidateUserIds: new List<string> { "u-1", "u-7", "u-42" },
        IssuedAtUtc: new DateTimeOffset(2025, 1, 5, 8, 5, 0, TimeSpan.Zero),
        ExpiresAtUtc: new DateTimeOffset(2025, 1, 5, 8, 6, 0, TimeSpan.Zero),
        OtpRecordKey: "tenant-a:f1d2c3b4a5968778695a4b3c2d1e0f00",
        Version: 1);

    [Fact]
    public void Protect_Unprotect_RoundTrip()
    {
        var provider = new EphemeralDataProtectionProvider();
        var codec = new PhoneOtpAccountSelectCookieCodec(provider);
        var payload = SamplePayload();

        var raw = codec.Protect(payload);

        codec.TryUnprotect(raw, out var roundTripped).Should().BeTrue();
        roundTripped.Should().NotBeNull();
        roundTripped.TenantKey.Should().Be(payload.TenantKey);
        roundTripped.PhoneE164Hash.Should().Be(payload.PhoneE164Hash);
        roundTripped.CandidateUserIds.Should().Equal(payload.CandidateUserIds);
        roundTripped.IssuedAtUtc.Should().Be(payload.IssuedAtUtc);
        roundTripped.ExpiresAtUtc.Should().Be(payload.ExpiresAtUtc);
        roundTripped.OtpRecordKey.Should().Be(payload.OtpRecordKey);
        roundTripped.Version.Should().Be(payload.Version);
    }

    [Fact]
    public void Tampered_Returns_False()
    {
        var provider = new EphemeralDataProtectionProvider();
        var codec = new PhoneOtpAccountSelectCookieCodec(provider);
        var raw = codec.Protect(SamplePayload());

        // Mutate first character (deterministically lands inside ciphertext, never on a delimiter
        // since DataProtection output is base64url-ish or base64 and never starts with a control char).
        var mutated = (raw[0] == 'A' ? 'B' : 'A') + raw[1..];
        mutated.Should().NotBe(raw);

        codec.TryUnprotect(mutated, out var payload).Should().BeFalse();
        payload.Should().BeNull();
    }

    [Fact]
    public void WrongPurpose_Returns_False()
    {
        var provider = new EphemeralDataProtectionProvider();
        var codec = new PhoneOtpAccountSelectCookieCodec(provider);

        // Protect bằng 1 protector có purpose khác (vd "PhoneOtp.SessionCookie")
        // và pass raw đó vào codec để chứng minh purpose isolation.
        var foreignProtector = provider.CreateProtector("PhoneOtp.SessionCookie");
        var foreignRaw = foreignProtector.Protect("not-an-account-select-payload");

        codec.TryUnprotect(foreignRaw, out var payload).Should().BeFalse();
        payload.Should().BeNull();
    }

    [Fact]
    public void Empty_String_Returns_False()
    {
        var provider = new EphemeralDataProtectionProvider();
        var codec = new PhoneOtpAccountSelectCookieCodec(provider);

        codec.TryUnprotect(string.Empty, out var payloadFromEmpty).Should().BeFalse();
        payloadFromEmpty.Should().BeNull();

        codec.TryUnprotect(null!, out var payloadFromNull).Should().BeFalse();
        payloadFromNull.Should().BeNull();
    }
}
