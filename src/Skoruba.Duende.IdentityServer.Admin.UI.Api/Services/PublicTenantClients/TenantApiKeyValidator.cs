// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

#nullable enable

using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.PublicTenantClients;

/// <summary>
/// SHA-256 + constant-time implementation of <see cref="ITenantApiKeyValidator"/>.
/// </summary>
/// <remarks>
/// <para>
/// Lifetime: <c>Singleton</c>. Re-reads
/// <see cref="IOptionsMonitor{TOptions}.CurrentValue"/> of
/// <see cref="TenantClientCachePublicReadOptions"/> on every call so that
/// hot-reload (R1.6) and per-request revocation (R3.5) both take effect on
/// the very next request without restarting the host.
/// </para>
/// <para>
/// The implementation never persists or logs the plaintext header, the
/// derived SHA-256 hash, or the configured hex digest (R3.4 / R8.7). Stack-
/// allocated buffers are used for the &lt;=256-byte common path to avoid
/// touching the GC.
/// </para>
/// </remarks>
internal sealed class TenantApiKeyValidator : ITenantApiKeyValidator
{
    /// <summary>
    /// Stack-allocation budget for the UTF-8 encoding of the API-key plaintext.
    /// Anything larger falls back to the heap. 256 bytes comfortably covers
    /// any reasonable opaque token (typically &lt;= 64 chars).
    /// </summary>
    private const int StackAllocByteThreshold = 256;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IOptionsMonitor<TenantClientCachePublicReadOptions> _options;

    public TenantApiKeyValidator(IOptionsMonitor<TenantClientCachePublicReadOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public bool TryValidate(string normalizedTenantKey, ReadOnlySpan<char> apiKeyPlaintext)
    {
        // R3.5 + R1.6: re-read every call so hot-reload-driven revocation
        // takes effect on the very next request without restarting.
        // The dictionary itself is not mutated in place; IOptionsMonitor
        // hands back a fresh instance after a configuration reload.
        var snapshot = _options.CurrentValue.ApiKeys;

        var hasEntry = snapshot is not null
            && snapshot.TryGetValue(normalizedTenantKey, out var expectedHexLowerLocal)
            && expectedHexLowerLocal is not null;
        var expectedHexLower = hasEntry ? snapshot![normalizedTenantKey] : string.Empty;

        // Compute SHA-256 of the plaintext header value regardless of whether
        // an entry exists — best-effort timing parity per R3.3 anti-enumeration.
        Span<byte> computed = stackalloc byte[32];
        ComputeSha256(apiKeyPlaintext, computed);

        // Parse the configured hex digest into a 32-byte buffer. If the entry
        // is absent (or somehow malformed at runtime — validator should have
        // caught this at startup, but defend in depth), produce a deterministic
        // zero buffer and STILL run FixedTimeEquals. The end result is "false"
        // because computed will not match a zero buffer for any plaintext that
        // happens to hash to all-zero (cryptographically negligible probability).
        Span<byte> expected = stackalloc byte[32];
        var parsed = TryParseHexLower(expectedHexLower, expected);

        // R3.2: constant-time comparison. We deliberately return false when
        // the lookup missed OR the hex parse failed, rather than short-
        // circuiting before the FixedTimeEquals call, so that the wall-clock
        // shape of registered-vs-unregistered tenants stays close.
        var equal = CryptographicOperations.FixedTimeEquals(computed, expected);
        return hasEntry && parsed && equal;
    }

    /// <summary>
    /// UTF-8 encode the plaintext with no BOM and SHA-256 hash it into
    /// <paramref name="destination"/>. Uses stack allocation when the
    /// encoded byte count fits within <see cref="StackAllocByteThreshold"/>.
    /// </summary>
    private static void ComputeSha256(ReadOnlySpan<char> plaintext, Span<byte> destination)
    {
        var byteCount = Utf8NoBom.GetByteCount(plaintext);
        if (byteCount <= StackAllocByteThreshold)
        {
            Span<byte> stackBuffer = stackalloc byte[StackAllocByteThreshold];
            var encoded = stackBuffer[..byteCount];
            Utf8NoBom.GetBytes(plaintext, encoded);
            SHA256.HashData(encoded, destination);
        }
        else
        {
            // Heap fallback for the unusually large header — still hashes
            // identically because UTF-8 encoding is deterministic.
            var heapBuffer = new byte[byteCount];
            Utf8NoBom.GetBytes(plaintext, heapBuffer);
            SHA256.HashData(heapBuffer, destination);
        }
    }

    /// <summary>
    /// Strict 64-character lowercase hex parser. Returns <c>true</c> on
    /// success and writes the 32 bytes into <paramref name="destination"/>.
    /// Mixed-case, malformed, or wrong-length input returns <c>false</c>.
    /// </summary>
    /// <remarks>
    /// We do not call <see cref="Convert.FromHexString(string)"/> because
    /// that helper accepts uppercase hex too — the spec (R1.4) requires the
    /// configured digest to be lowercase. Defending here keeps the validator
    /// robust even if the options validator is bypassed in tests or
    /// programmatic wiring.
    /// </remarks>
    private static bool TryParseHexLower(string? hex, Span<byte> destination)
    {
        if (hex is null || hex.Length != 64 || destination.Length != 32)
        {
            return false;
        }

        for (var i = 0; i < 32; i++)
        {
            var hi = FromHexLower(hex[2 * i]);
            var lo = FromHexLower(hex[(2 * i) + 1]);
            if ((hi | lo) < 0)
            {
                return false;
            }

            destination[i] = (byte)((hi << 4) | lo);
        }

        return true;
    }

    /// <summary>
    /// Map a lowercase hex character to its nibble value, or -1 for any
    /// non-lowercase-hex character (including uppercase A-F).
    /// </summary>
    private static int FromHexLower(char c)
    {
        if (c >= '0' && c <= '9')
        {
            return c - '0';
        }

        if (c >= 'a' && c <= 'f')
        {
            return c - 'a' + 10;
        }

        return -1;
    }
}
