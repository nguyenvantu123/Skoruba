// Feature: tenant-client-cache-public-read, Task 10
//
// Test-only helper that generates deterministic <plaintext, sha256-hex>
// pairs for populating TenantClientCachePublicRead:ApiKeys configuration
// in integration tests. The plaintext is a synthetic test value (not a
// real secret) — see runbook PR review checks (Task 12) which forbid
// committing plaintext secrets to the repository.

#nullable enable

using System;
using System.Security.Cryptography;
using System.Text;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Tests.PublicTenantClients.Helpers;

internal static class TestApiKeys
{
    /// <summary>
    /// The canonical valid plaintext API key used by every happy-path
    /// integration test. Synthetic and carries no real secret value —
    /// the prefix advertises that fact to PR reviewers.
    /// </summary>
    public const string ValidPlaintext = "test-key-deadbeef-acme-public-read";

    /// <summary>Pre-computed lowercase SHA-256 hex of <see cref="ValidPlaintext"/>.</summary>
    public static string ValidHashAcme { get; } = Sha256HexLower(ValidPlaintext);

    /// <summary>Synthetic alternate key used for negative tests.</summary>
    public const string OtherPlaintext = "test-key-cafebabe-globex-public-read";

    public static string OtherHashGlobex { get; } = Sha256HexLower(OtherPlaintext);

    /// <summary>Compute the lowercase hex SHA-256 of a UTF-8 string.</summary>
    public static string Sha256HexLower(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
