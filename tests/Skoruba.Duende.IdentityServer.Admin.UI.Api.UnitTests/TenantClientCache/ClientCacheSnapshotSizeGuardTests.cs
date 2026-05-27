// Feature: tenant-client-cache-expansion, Task 3
//
// Example-based + property-based coverage for ClientCacheSnapshotSizeGuard.
//
// Validates: Requirements 2.6, 14.5
//
// The property test pins the upper-bound by generating ClientDto instances
// constrained to "realistic" value domains (lists ≤ 50 items, strings ≤ 512
// characters) and asserting the serialized envelope stays at or below the
// 256 KiB ceiling.

using System;
using System.Collections.Generic;
using System.Linq;

using FluentAssertions;

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache;

public sealed class ClientCacheSnapshotSizeGuardTests
{
    [Fact]
    public void SizeGuard_Under_Limit_Allowed()
    {
        var payload = new byte[ClientCacheSnapshotSizeGuard.MaxBytes - 1];

        ClientCacheSnapshotSizeGuard.IsWithinLimit(payload).Should().BeTrue();
    }

    [Fact]
    public void SizeGuard_Exact_Limit_Allowed()
    {
        var payload = new byte[ClientCacheSnapshotSizeGuard.MaxBytes];

        ClientCacheSnapshotSizeGuard.IsWithinLimit(payload).Should().BeTrue();
    }

    [Fact]
    public void SizeGuard_Over_Limit_Rejected()
    {
        var payload = new byte[ClientCacheSnapshotSizeGuard.MaxBytes + 1];

        ClientCacheSnapshotSizeGuard.IsWithinLimit(payload).Should().BeFalse();
    }

    public static class Arbs
    {
        // Realistic value domain for the property test:
        //   - ≤ 50 items per list,
        //   - ≤ 512 characters per string.
        private const int MaxStringLen = 512;
        private const int MaxListItems = 50;

        private static readonly char[] AsciiPool =
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_".ToCharArray();

        private static Gen<string> StringGen(int maxLen = MaxStringLen)
            => from len in Gen.Choose(0, maxLen)
               from chars in Gen.Elements(AsciiPool).ListOf(len)
               select new string(chars.ToArray());

        private static Gen<List<string>> ListGen()
            => from count in Gen.Choose(0, MaxListItems)
               from items in StringGen().ListOf(count)
               select items.ToList();

        public static Arbitrary<ClientDto> Dto()
        {
            var gen =
                from clientId in StringGen(64)
                from clientName in StringGen(128)
                from clientUri in StringGen()
                from logoUri in StringGen()
                from description in StringGen()
                from enabled in Gen.Elements(true, false)
                from protocolType in Gen.Elements("oidc", "saml2p", "wsfed")
                from redirectUris in ListGen()
                from postLogoutRedirectUris in ListGen()
                from allowedCorsOrigins in ListGen()
                from allowedGrantTypes in ListGen()
                from allowedScopes in ListGen()
                from allowedAlgs in ListGen()
                from frontChannelLogoutUri in StringGen()
                from backChannelLogoutUri in StringGen()
                from initiateLoginUri in StringGen()
                select new ClientDto
                {
                    ClientId = clientId,
                    ClientName = clientName,
                    ClientUri = clientUri,
                    LogoUri = logoUri,
                    Description = description,
                    Enabled = enabled,
                    ProtocolType = protocolType,
                    RedirectUris = redirectUris,
                    PostLogoutRedirectUris = postLogoutRedirectUris,
                    AllowedCorsOrigins = allowedCorsOrigins,
                    AllowedGrantTypes = allowedGrantTypes,
                    AllowedScopes = allowedScopes,
                    AllowedIdentityTokenSigningAlgorithms = allowedAlgs,
                    FrontChannelLogoutUri = frontChannelLogoutUri,
                    BackChannelLogoutUri = backChannelLogoutUri,
                    InitiateLoginUri = initiateLoginUri,
                };
            return gen.ToArbitrary();
        }
    }

    /// <summary>
    /// Property — for any ClientDto whose Public_Safe_Fields are within the
    /// realistic value domain (lists ≤ 50 items, strings ≤ 512 chars), the
    /// serialized envelope stays at or below the 256 KiB ceiling.
    ///
    /// Validates: Requirements 2.6, 14.5
    /// </summary>
    [Property(MaxTest = 50, Arbitrary = new[] { typeof(Arbs) })]
    public void Property_Snapshot_Within_Default_Domains_Stays_Below_256KiB(ClientDto source)
    {
        var lastWriteUtc = new DateTime(2025, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        var snapshot = ClientCacheSnapshotMapper.Map(source, lastWriteUtc);
        var envelope = new ClientCacheSnapshotEnvelope
        {
            Version = 1,
            TenantKey = "tenant-a",
            ClientId = source.ClientId ?? "",
            LastWriteUtc = lastWriteUtc,
            Data = snapshot,
        };

        var bytes = ClientCacheSnapshotSerializer.Serialize(envelope);

        ClientCacheSnapshotSizeGuard.IsWithinLimit(bytes).Should().BeTrue(
            $"R2.6/R14.5: snapshot size {bytes.Length} bytes must be ≤ {ClientCacheSnapshotSizeGuard.MaxBytes}");
    }
}
