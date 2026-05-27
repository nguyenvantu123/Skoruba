// Feature: phone-otp-multi-account-select, Property 8: SelectionToken security invariants
//
// Property 8 (Section 4.4 + Requirements 5.9, 6.8):
//   For all non-empty userId values:
//     (a) Issue(uid) MUST NOT contain uid as plaintext substring.
//     (b) TryResolve(Issue(uid)) MUST return uid (round-trip).
//     (c) Issue(uid) called twice MUST produce two distinct tokens (random IV).
//     (d) Tampering 1 char in the token MUST cause TryResolve to return false.
//     (e) Token protected under a different DataProtection purpose MUST NOT resolve.
//
// Validates: Requirements 5.9, 6.8
using System.Linq;
using System.Text;

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Services;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.UnitTests.Properties;

public class Property08_SelectionTokenInvariants
{
    /// <summary>
    /// userId generator: GUID-ish strings (16 hex chars) — non-empty, alphanumeric, deterministic
    /// shape of <see cref="Microsoft.AspNetCore.Identity.IdentityUser.Id"/>.
    /// </summary>
    private static Gen<string> UserIdGen() =>
        Gen.Choose(0x30, 0x7a) // ASCII '0'..'z'
            .Where(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
            .ListOf(16)
            .Select(chars => new string(chars.Select(c => (char)c).ToArray()))
            .Where(s => s.Length > 0);

    [Property(MaxTest = 100)]
    public Property Issue_DoesNotEmbed_UserId_AsPlaintextSubstring()
    {
        return Prop.ForAll(UserIdGen().ToArbitrary(), userId =>
        {
            var protector = new SelectionTokenProtector(new EphemeralDataProtectionProvider());
            var token = protector.Issue(userId);
            return (!token.Contains(userId)).Label($"userId='{userId}' token='{token}'");
        });
    }

    [Property(MaxTest = 100)]
    public Property Issue_TryResolve_RoundTrip()
    {
        return Prop.ForAll(UserIdGen().ToArbitrary(), userId =>
        {
            var protector = new SelectionTokenProtector(new EphemeralDataProtectionProvider());
            var token = protector.Issue(userId);
            var ok = protector.TryResolve(token, out var resolved);
            return (ok && resolved == userId).Label($"ok={ok} resolved='{resolved}' userId='{userId}'");
        });
    }

    [Property(MaxTest = 100)]
    public Property Issue_Twice_Produces_DistinctTokens()
    {
        return Prop.ForAll(UserIdGen().ToArbitrary(), userId =>
        {
            var protector = new SelectionTokenProtector(new EphemeralDataProtectionProvider());
            var t1 = protector.Issue(userId);
            var t2 = protector.Issue(userId);
            return (t1 != t2).Label($"t1='{t1}' t2='{t2}'");
        });
    }

    [Property(MaxTest = 100)]
    public Property Tampered_Token_FailsToResolve()
    {
        return Prop.ForAll(UserIdGen().ToArbitrary(), userId =>
        {
            var protector = new SelectionTokenProtector(new EphemeralDataProtectionProvider());
            var token = protector.Issue(userId);

            // Mutate first char deterministically — guarantees a different string.
            var swapped = token[0] == 'A' ? 'B' : 'A';
            var mutated = swapped + token[1..];

            if (mutated == token)
            {
                return true.Label("no-op mutation skipped");
            }

            var ok = protector.TryResolve(mutated, out var resolved);
            return (!ok && resolved == string.Empty).Label($"ok={ok} resolved='{resolved}'");
        });
    }

    [Property(MaxTest = 100)]
    public Property WrongPurpose_Token_FailsToResolve()
    {
        return Prop.ForAll(UserIdGen().ToArbitrary(), userId =>
        {
            var provider = new EphemeralDataProtectionProvider();
            var protector = new SelectionTokenProtector(provider);

            // Encrypt under a foreign purpose, base64url-encode to look like a real token.
            var foreign = provider.CreateProtector("PhoneOtp.NotTheTokenPurpose");
            var encoded = Base64UrlTextEncoder.Encode(foreign.Protect(Encoding.UTF8.GetBytes(userId)));

            var ok = protector.TryResolve(encoded, out var resolved);
            return (!ok && resolved == string.Empty).Label($"ok={ok} resolved='{resolved}'");
        });
    }
}
