// Feature: phone-otp-multi-account-select, Property 1: Candidate set ordering is deterministic and total
//
// Validates: Requirements 2.3
//
// Sort key (Section 4.1 design, R2.3):
//   (LockoutEnabled ASC, LockoutEnd NULL FIRST then ASC, NormalizedUserName ASC).
//
// Invariants asserted:
//   * Permutation: sort output multiset == input multiset.
//   * Total order: every adjacent pair (a, b) satisfies CompareSortKey(a, b) <= 0.
//   * Idempotent: sort(sort(xs)) == sort(xs) (sort is a fixed point of itself).

using System;
using System.Collections.Generic;
using System.Linq;

using FluentAssertions;

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.UnitTests.Properties;

public sealed class Property01_CandidateOrderDeterminism
{
    /// <summary>
    /// Tuple matching the production sort key. <see cref="LockoutEnd"/> nullable
    /// so the generator covers the "NULL FIRST" case explicitly.
    /// </summary>
    public sealed record CandidateKey(
        bool LockoutEnabled,
        DateTimeOffset? LockoutEnd,
        string NormalizedUserName);

    /// <summary>
    /// Mirror of <c>PhoneOtpService.IssueAsync</c> sort logic (Section 4.1
    /// design). Re-implemented here so the property is exercised in isolation
    /// of EF / DI machinery — Section 10.3 design explicitly recommends this
    /// "implement direct sort in test file" approach.
    /// </summary>
    private static IReadOnlyList<CandidateKey> Sort(IEnumerable<CandidateKey> xs)
        => xs
            .OrderBy(c => c.LockoutEnabled)
            .ThenBy(c => c.LockoutEnd.HasValue ? 1 : 0) // NULL first
            .ThenBy(c => c.LockoutEnd ?? DateTimeOffset.MaxValue)
            .ThenBy(c => c.NormalizedUserName, StringComparer.Ordinal)
            .ToList();

    private static int CompareSortKey(CandidateKey a, CandidateKey b)
    {
        var c = a.LockoutEnabled.CompareTo(b.LockoutEnabled);
        if (c != 0) return c;

        // NULL FIRST then ASC
        var aHas = a.LockoutEnd.HasValue ? 1 : 0;
        var bHas = b.LockoutEnd.HasValue ? 1 : 0;
        c = aHas.CompareTo(bHas);
        if (c != 0) return c;

        if (a.LockoutEnd.HasValue && b.LockoutEnd.HasValue)
        {
            c = a.LockoutEnd.Value.CompareTo(b.LockoutEnd.Value);
            if (c != 0) return c;
        }

        return string.CompareOrdinal(a.NormalizedUserName, b.NormalizedUserName);
    }

    public static class Arbs
    {
        // Curated NormalizedUserName pool. Includes ties so the third sort tier
        // (NormalizedUserName ASC) gets exercised independently of the first two.
        // Ordinal compare semantics — uppercase intentional (NormalizedUserName).
        private static readonly string[] NamePool =
        {
            "ALICE", "BOB", "CAROL", "DAVE", "EVE", "ZZZ",
        };

        // LockoutEnd pool covers: null (NULL FIRST branch), past, near-future,
        // far-future, and a tied value across two candidates so the
        // string-compare tie-break path lights up.
        private static readonly DateTimeOffset?[] LockoutEndPool =
        {
            null,
            DateTimeOffset.UnixEpoch,
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), // tie with previous
            new DateTimeOffset(2030, 6, 15, 12, 30, 0, TimeSpan.Zero),
        };

        private static Gen<CandidateKey> KeyGen()
            => from enabled in Gen.Elements(true, false)
               from end in Gen.Elements(LockoutEndPool)
               from name in Gen.Elements(NamePool)
               select new CandidateKey(enabled, end, name);

        public static Arbitrary<CandidateKey[]> NonEmptyKeyArray()
            => KeyGen()
                // ListOf returns a possibly-empty list; we filter out empty
                // since "non-empty" is the documented input space (the spec
                // sort branch only fires when users.Count >= 1).
                .ListOf()
                .Where(xs => xs is { Count: > 0 })
                .Select(xs => xs.ToArray())
                .ToArbitrary();
    }

    /// <summary>
    /// Property 1 — single FsCheck property covering all three invariants
    /// (permutation + total-order + idempotent). One property per file per
    /// task spec (Section 10.3).
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Arbs) })]
    public void Sort_IsDeterministic_TotalOrdered_AndIdempotent(CandidateKey[] inputs)
    {
        var input = inputs.ToList();

        var once = Sort(input);
        var twice = Sort(once);

        // Permutation: sort doesn't drop or duplicate elements. Compare by
        // multiset (count per equal-key) since duplicate keys are valid.
        once.Should().HaveCount(input.Count);
        once.OrderBy(k => k.LockoutEnabled)
            .ThenBy(k => k.LockoutEnd?.UtcTicks ?? long.MinValue)
            .ThenBy(k => k.NormalizedUserName, StringComparer.Ordinal)
            .Should()
            .Equal(input
                .OrderBy(k => k.LockoutEnabled)
                .ThenBy(k => k.LockoutEnd?.UtcTicks ?? long.MinValue)
                .ThenBy(k => k.NormalizedUserName, StringComparer.Ordinal));

        // Total ordered: every adjacent pair respects CompareSortKey.
        for (var i = 0; i < once.Count - 1; i++)
        {
            CompareSortKey(once[i], once[i + 1])
                .Should()
                .BeLessOrEqualTo(0,
                    because: "candidate at index {0} must precede or tie candidate at index {1}",
                    i, i + 1);
        }

        // Idempotent: sort(sort(xs)) == sort(xs).
        twice.Should().Equal(once);
    }
}
