// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

#nullable enable

using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.PublicTenantClients;

/// <summary>
/// Computes a salted SHA-256 hash of a remote IP address for inclusion in
/// audit log entries of the public-read endpoint
/// (<c>GET /api/public/tenants/{tenantKey}/clients/{clientId}</c>). Used by
/// <see cref="TenantApiKeyAuthorizationFilter"/> + the controller so the raw
/// IP is never persisted in structured logs (R9.6, R9.7).
/// </summary>
/// <remarks>
/// <para>
/// Lifetime: <c>Singleton</c>. The implementation re-reads
/// <see cref="IOptionsMonitor{TOptions}.CurrentValue"/> on every call so that
/// hot-reload of <c>TenantClientCachePublicRead:Audit:LogIpHash</c> /
/// <c>:RemoteIpSalt</c> is observable on the very next request without
/// restarting the host.
/// </para>
/// <para>
/// The hash format is <c>sha256-hex-lowercase(ip + ":" + salt)</c>. The
/// remote-IP string representation comes from
/// <see cref="IPAddress.ToString"/> which is deterministic for both IPv4
/// and IPv6 (canonical, lowercase hex for IPv6, no zone suffix unless the
/// address actually carries one). When
/// <c>TenantClientCachePublicRead:Audit:LogIpHash = false</c> (R3.6 opt-out)
/// or when the caller passes a <c>null</c> remote IP, this helper returns
/// <c>null</c> so audit emitters can omit the field entirely.
/// </para>
/// </remarks>
public sealed class IpHashHelper
{
    /// <summary>
    /// Stack-allocation budget for the UTF-8 encoding of <c>ip:salt</c>.
    /// Largest reasonable input is an IPv6 address (45 chars) + ":" +
    /// a salt of practical size (≤ 128 chars) which fits comfortably.
    /// </summary>
    private const int StackAllocByteThreshold = 256;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IOptionsMonitor<TenantClientCachePublicReadOptions> _options;

    public IpHashHelper(IOptionsMonitor<TenantClientCachePublicReadOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Returns a salted SHA-256 hex hash of <paramref name="remoteIp"/>, or
    /// <c>null</c> when audit IP hashing is disabled or the address is
    /// unknown. The same <c>(ip, salt)</c> pair always produces the same
    /// hash within a single <see cref="IOptionsMonitor{TOptions}"/> snapshot.
    /// </summary>
    /// <param name="remoteIp">
    /// Remote IP address from <c>HttpContext.Connection.RemoteIpAddress</c>.
    /// May be <c>null</c> when the connection lacks one (e.g. in-process
    /// test host) — the helper returns <c>null</c> in that case.
    /// </param>
    /// <returns>
    /// 64-char lowercase hex SHA-256 of <c>ip + ":" + salt</c>, or
    /// <c>null</c> when <c>Audit.LogIpHash</c> is <c>false</c> or
    /// <paramref name="remoteIp"/> is <c>null</c>.
    /// </returns>
    public string? Hash(IPAddress? remoteIp)
    {
        var audit = _options.CurrentValue.Audit;
        if (!audit.LogIpHash || remoteIp is null)
        {
            return null;
        }

        var salt = audit.RemoteIpSalt ?? string.Empty;
        var ip = remoteIp.ToString();

        // Compose the deterministic preimage "ip:salt" without intermediate
        // string allocation when possible.
        var totalChars = ip.Length + 1 + salt.Length;
        var byteCount = Utf8NoBom.GetMaxByteCount(totalChars);

        Span<byte> hash = stackalloc byte[32];
        if (byteCount <= StackAllocByteThreshold)
        {
            Span<byte> buffer = stackalloc byte[StackAllocByteThreshold];
            var written = WritePreimage(ip, salt, buffer);
            SHA256.HashData(buffer[..written], hash);
        }
        else
        {
            var heap = new byte[byteCount];
            var written = WritePreimage(ip, salt, heap);
            SHA256.HashData(heap.AsSpan(0, written), hash);
        }

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static int WritePreimage(string ip, string salt, Span<byte> destination)
    {
        var written = Utf8NoBom.GetBytes(ip.AsSpan(), destination);
        destination[written++] = (byte)':';
        written += Utf8NoBom.GetBytes(salt.AsSpan(), destination[written..]);
        return written;
    }
}
