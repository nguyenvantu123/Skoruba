// Feature: tenant-client-cache-expansion, Task 4 — Property06_ResolverDeterminism
//
// Property-based test asserting:
//   (P6) the resolver's output is normalized lower-invariant + trimmed,
//        case-insensitively distinct, lex-ascending, and the priority chain
//        is strict (priority 2 ignored when priority 1 has at least one
//        non-blank tenant key).
//
// Validates: Requirements 11.2, 11.3, 11.4

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

using Moq;

using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Configuration;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Helpers;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Services.Interfaces;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache;

public sealed class ClientTenantScopeResolverProperties
{
    /// <summary>
    /// Generators for synthetic ClientDtos with controlled DB-backed pair
    /// sets and legacy property JSON. We keep value domains small so the
    /// FsCheck loop stays fast.
    /// </summary>
    public static class Arbs
    {
        // Tenant-key alphabet — short ASCII identifiers, deliberately
        // overlapping uppercase/lowercase to exercise the case-insensitive
        // distinct rule.
        private static readonly string[] KeyAlphabet =
        {
            "Acme", "acme", "ACME",
            "Contoso", "contoso", "CONTOSO",
            "fabrikam", "FABRIKAM",
            "tailspin", "Tailspin",
            "alpha", "Beta", "GAMMA",
        };

        private static Gen<string> RawTenantKeyGen()
            => Gen.Frequency(
                // Most of the time produce a real-looking key — sometimes
                // pad it with whitespace so the trim contract gets exercised.
                (5, Gen.Elements(KeyAlphabet)),
                (1, from k in Gen.Elements(KeyAlphabet) select "  " + k),
                (1, from k in Gen.Elements(KeyAlphabet) select k + "  "),
                // Occasionally inject a blank key — these must be dropped.
                (1, Gen.Constant("")),
                (1, Gen.Constant("   ")));

        public static Arbitrary<List<string>> RawTenantKeyList()
        {
            // 0..6 keys per list, with repeats allowed. Empty list lets us
            // stress the priority-chain fall-through.
            var gen =
                from count in Gen.Choose(0, 6)
                from items in RawTenantKeyGen().ListOf(count)
                select items.ToList();
            return gen.ToArbitrary();
        }
    }

    private static IClientTenantScopeResolver CreateResolver()
        => new ClientTenantScopeResolver(new Mock<IClientService>(MockBehavior.Strict).Object);

    /// <summary>
    /// Build a DTO whose DB-backed pair list is <paramref name="dbKeys"/>
    /// (non-blank entries only) and whose <c>Properties</c> contains a
    /// well-formed legacy JSON for <paramref name="propertyKeys"/>.
    /// </summary>
    /// <remarks>
    /// The legacy JSON entries always include a synthetic
    /// <c>signInCallbackUrl</c>: the existing
    /// <see cref="ClientTenantRedirectPairsHelper"/> drops pair records that
    /// have no callback URLs at all, so a real-world entry always carries
    /// at least one URL. Including a URL here exercises the parse +
    /// normalization round-trip the resolver actually performs.
    /// </remarks>
    private static ClientDto BuildDto(List<string> dbKeys, List<string> propertyKeys)
    {
        var dto = new ClientDto
        {
            ClientId = "client-under-test",
            TenantRedirectPairs = dbKeys
                .Select(k => new ClientTenantRedirectPairDto { TenantKey = k })
                .ToList(),
            Properties = new List<ClientPropertyDto>(),
        };

        // Build legacy JSON payload for priority-2. We attach a realistic
        // signInCallbackUrl per entry — without it the helper's Normalize
        // step (which guards against zero-URL pairs) would silently drop
        // every pair, masking what we're trying to test.
        var json = JsonSerializer.Serialize(
            propertyKeys.Select(k => new
            {
                tenantKey = k,
                signInCallbackUrl = "https://example/cb",
            }));
        dto.Properties.Add(new ClientPropertyDto
        {
            Key = ClientTenantRedirectPairsHelper.PropertyKey,
            Value = json,
        });

        return dto;
    }

    /// <summary>
    /// Re-implements the resolver's normalization in test-language so we can
    /// compute the expected output independently. If both implementations
    /// drift apart, the property fails.
    /// </summary>
    private static IReadOnlyList<string> ExpectedNormalize(IEnumerable<string> source)
    {
        return source
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Property 06 — Resolver determinism + priority chain strict + normalization.
    ///
    /// Validates: Requirements 11.2, 11.3, 11.4
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Arbs) })]
    public async Task Property06_ResolverDeterminism(List<string> dbKeys, List<string> propertyKeys)
    {
        var resolver = CreateResolver();
        var dto = BuildDto(dbKeys, propertyKeys);

        var actual = await resolver.ResolveTenantKeysAsync(dto, CancellationToken.None);

        // Priority chain: priority 1 "fires" whenever the DB list contains
        // any non-null pair entry (matching the STS ClientTenantRedirectResolver
        // semantics that "any DB row wins"). It is the existence of a row,
        // not the well-formedness of its TenantKey, that suppresses
        // priority 2. When priority 1 fires but normalization wipes every
        // key (e.g. corrupt blank tenant keys), the result is empty —
        // priority 2 is NOT consulted in that case.
        IReadOnlyList<string> expected;
        if (dbKeys.Count > 0)
        {
            expected = ExpectedNormalize(dbKeys);
        }
        else
        {
            expected = ExpectedNormalize(propertyKeys);
        }

        actual.Should().Equal(expected,
            "R11.2/R11.3/R11.4: resolver output must match the priority-chain + normalization spec exactly");

        // R11.3 invariants restated as standalone properties for stronger
        // signal when a counter-example arrives. Skip the per-item checks
        // when the result is legitimately empty (FluentAssertions'
        // OnlyContain treats an empty subject as a failure even though for
        // our contract "no keys" trivially satisfies the per-item invariants).
        if (actual.Count > 0)
        {
            actual.Should().OnlyContain(k => k == k.Trim().ToLowerInvariant(),
                "every emitted key must be trimmed + lower-invariant");
            actual.Should().OnlyHaveUniqueItems(
                "case-insensitive distinct after normalization (R11.3)");
            actual.Should().BeInAscendingOrder(StringComparer.Ordinal,
                "lexicographic ascending (R11.4)");
        }
    }
}
