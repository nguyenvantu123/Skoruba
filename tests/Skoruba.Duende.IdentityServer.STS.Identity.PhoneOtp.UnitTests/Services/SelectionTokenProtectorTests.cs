using System.Text;

using FluentAssertions;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Services;

using Xunit;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.UnitTests.Services;

/// <summary>
/// Unit tests cho <see cref="SelectionTokenProtector"/> (Section 4.4 design).
/// Validates Requirements 5.9, 6.8.
/// </summary>
public class SelectionTokenProtectorTests
{
    [Fact]
    public void Issue_DoesNotContain_UserId_AsPlaintextSubstring()
    {
        var protector = new SelectionTokenProtector(new EphemeralDataProtectionProvider());
        const string userId = "u-12345-DISTINCT";

        var token = protector.Issue(userId);

        token.Should().NotBeNullOrEmpty();
        token.Should().NotContain(userId, "token MUST NOT carry userId in plaintext (R5.9)");
    }

    [Fact]
    public void TryResolve_Valid_RoundTrip()
    {
        var protector = new SelectionTokenProtector(new EphemeralDataProtectionProvider());
        const string userId = "u-1";

        var token = protector.Issue(userId);

        protector.TryResolve(token, out var resolved).Should().BeTrue();
        resolved.Should().Be(userId);
    }

    [Fact]
    public void Issue_Twice_ProducesDistinctTokens()
    {
        var protector = new SelectionTokenProtector(new EphemeralDataProtectionProvider());
        const string userId = "u-1";

        var first = protector.Issue(userId);
        var second = protector.Issue(userId);

        first.Should().NotBe(second, "DataProtection adds a random IV — two protect calls must differ (R6.8)");

        protector.TryResolve(first, out var resolvedFirst).Should().BeTrue();
        protector.TryResolve(second, out var resolvedSecond).Should().BeTrue();
        resolvedFirst.Should().Be(userId);
        resolvedSecond.Should().Be(userId);
    }

    [Fact]
    public void TryResolve_Tampered_ReturnsFalse()
    {
        var protector = new SelectionTokenProtector(new EphemeralDataProtectionProvider());
        var token = protector.Issue("u-1");

        // Flip first base64url character to mutate ciphertext deterministically.
        var mutated = (token[0] == 'A' ? 'B' : 'A') + token[1..];
        mutated.Should().NotBe(token);

        protector.TryResolve(mutated, out var resolved).Should().BeFalse();
        resolved.Should().BeEmpty();
    }

    [Fact]
    public void TryResolve_WrongPurpose_ReturnsFalse()
    {
        var provider = new EphemeralDataProtectionProvider();
        var protector = new SelectionTokenProtector(provider);

        // Protect bằng 1 protector khác purpose, sau đó base64url-encode để format giống token.
        var foreign = provider.CreateProtector("PhoneOtp.SomethingElse");
        var encoded = Base64UrlTextEncoder.Encode(foreign.Protect(Encoding.UTF8.GetBytes("u-1")));

        protector.TryResolve(encoded, out var resolved).Should().BeFalse();
        resolved.Should().BeEmpty();
    }
}
