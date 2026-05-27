// Feature: tenant-client-cache-public-read, Task 3
//
// Example-based tests for IpHashHelper covering:
//   R3.6 — Audit:LogIpHash = false → Hash returns null.
//   R3.6 — null IP → Hash returns null.
//   R9.6 — sha256-hex(ip + ":" + salt) format, lowercase, deterministic.
//   R9.7 — different salts produce different hashes for the same IP.

#nullable enable

using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;

using FluentAssertions;

using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.PublicTenantClients;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache.Helpers;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.PublicTenantClients;

public class IpHashHelperTests
{
    private static IpHashHelper Build(bool logIpHash, string salt)
    {
        var options = new TenantClientCachePublicReadOptions();
        options.Audit.LogIpHash = logIpHash;
        options.Audit.RemoteIpSalt = salt;
        var monitor = new StubOptionsMonitor<TenantClientCachePublicReadOptions>(options);
        return new IpHashHelper(monitor);
    }

    private static string ExpectedHash(IPAddress ip, string salt)
    {
        var bytes = Encoding.UTF8.GetBytes($"{ip}:{salt}");
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    [Fact]
    public void LogIpHash_False_Returns_Null()
    {
        var helper = Build(logIpHash: false, salt: "any-salt");

        helper.Hash(IPAddress.Parse("198.51.100.7")).Should().BeNull();
    }

    [Fact]
    public void Null_RemoteIp_Returns_Null()
    {
        var helper = Build(logIpHash: true, salt: "salt");

        helper.Hash(null).Should().BeNull();
    }

    [Fact]
    public void IPv4_With_Salt_Hash_Matches_Spec_Format()
    {
        var helper = Build(logIpHash: true, salt: "production-salt-001");
        var ip = IPAddress.Parse("203.0.113.42");

        var actual = helper.Hash(ip);

        actual.Should().NotBeNullOrEmpty();
        actual.Should().HaveLength(64);
        actual.Should().Be(ExpectedHash(ip, "production-salt-001"));
        actual.Should().MatchRegex("^[0-9a-f]{64}$", "R9.6 mandates lowercase hex");
    }

    [Fact]
    public void IPv6_With_Salt_Hash_Matches_Spec_Format()
    {
        var helper = Build(logIpHash: true, salt: "salt");
        var ip = IPAddress.Parse("2001:db8::1");

        helper.Hash(ip).Should().Be(ExpectedHash(ip, "salt"));
    }

    [Fact]
    public void Empty_Salt_Allowed_OutsideProduction()
    {
        // The validator forbids empty salt in Production but not in
        // Development; the helper itself is environment-agnostic and must
        // hash with the empty salt verbatim.
        var helper = Build(logIpHash: true, salt: string.Empty);
        var ip = IPAddress.Parse("198.51.100.5");

        helper.Hash(ip).Should().Be(ExpectedHash(ip, string.Empty));
    }

    [Fact]
    public void Same_Ip_Same_Salt_Same_Hash_Determinism()
    {
        var helper = Build(logIpHash: true, salt: "salt-x");
        var ip = IPAddress.Parse("198.51.100.5");

        var a = helper.Hash(ip);
        var b = helper.Hash(ip);

        a.Should().Be(b);
    }

    [Fact]
    public void Different_Salt_Different_Hash_For_Same_Ip()
    {
        var helperA = Build(logIpHash: true, salt: "salt-A");
        var helperB = Build(logIpHash: true, salt: "salt-B");
        var ip = IPAddress.Parse("198.51.100.5");

        helperA.Hash(ip).Should().NotBe(helperB.Hash(ip));
    }

    [Fact]
    public void Different_Ip_Same_Salt_Different_Hash()
    {
        var helper = Build(logIpHash: true, salt: "salt");

        var a = helper.Hash(IPAddress.Parse("198.51.100.5"));
        var b = helper.Hash(IPAddress.Parse("198.51.100.6"));

        a.Should().NotBe(b);
    }

    [Fact]
    public void Hash_Never_Contains_Raw_Ip_Substring()
    {
        // R9.6 — raw IP must not appear in any audit log field; verify the
        // hash output cannot accidentally include it.
        var helper = Build(logIpHash: true, salt: "salt");
        var ip = IPAddress.Parse("203.0.113.42");

        var hash = helper.Hash(ip);

        hash.Should().NotContain("203.0.113.42");
    }

    [Fact]
    public void Constructor_Throws_On_Null_Options()
    {
        Action act = () => _ = new IpHashHelper(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("options");
    }
}
