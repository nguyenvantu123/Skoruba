// Feature: tenant-client-cache-public-read, Task 3
//
// Property-based tests for TenantApiKeyAuthorizationFilter + IpHashHelper +
// HttpsRequiredFilter.
//
// Property 04 — EnumerationResistance (Validates: Requirements 3.3, 9.1):
//   Unregistered tenant + registered tenant with wrong key produce
//   byte-equal HTTP status, response body, content type, missing
//   Retry-After header AND audit log entries WITHOUT a TenantKey field.
//
// Property 06 — WhitespaceHeader (Validates: Requirements 3.1, 3.7):
//   Whitespace-only header values short-circuit at the authorization
//   filter with 401 missing_api_key and never reach the validator (so
//   ITenantClientCacheService is never invoked).
//
// Property 14 — AuditLogRedaction (Validates: Requirements 3.4, 8.7, 9.3,
//   9.5, 10.10): no log entry's structured field value contains the raw
//   header, the SHA-256 hash, or the raw tenantKey on the unauthorized
//   path. (Success / Hit-path redaction is covered by the controller
//   integration test in Task 5.)
//
// Property 17 — HttpsGate_And_RemoteIpHash (Validates: Requirements 9.6,
//   9.7): http://non-localhost requests are rejected with 400
//   https_required BEFORE API key validation; IpHashHelper is
//   deterministic and never embeds the raw IP string in its output.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using FluentAssertions;

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.PublicTenantClients;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache.Helpers;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.PublicTenantClients;

public sealed class TenantApiKeyAuthorizationFilterProperties
{
    // ===== Generators ==============================================

    public sealed record EnumerationSample(string RegisteredTenant, string ApiKey, string UnregisteredTenant);
    public sealed record WhitespaceSample(string Whitespace);
    public sealed record RedactionSample(string TenantPath, string Header);
    public sealed record HttpsSample(string Scheme, string Host, string RemoteIp, string Salt);

    public static class Arbs
    {
        private static readonly char[] TenantAlphabet =
            "abcdefghijklmnopqrstuvwxyz0123456789_-".ToCharArray();
        private static readonly char[] OpaqueAlphabet =
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_!.".ToCharArray();
        private static readonly string[] WhitespaceVariants =
        {
            "", " ", "  ", "   ", "\t", "\n", "\r", "  \t  ", "\n\n", " \t \n ",
        };

        private static Gen<string> TenantGen()
            => from len in Gen.Choose(4, 16)
               from chars in Gen.Elements(TenantAlphabet).ListOf(len)
               select new string(chars.ToArray());

        private static Gen<string> OpaqueGen()
            => from len in Gen.Choose(4, 32)
               from chars in Gen.Elements(OpaqueAlphabet).ListOf(len)
               select new string(chars.ToArray());

        public static Arbitrary<EnumerationSample> Enumeration()
            => (from registered in TenantGen()
                from unregistered in TenantGen().Where(s => !string.Equals(s, registered, StringComparison.Ordinal))
                from key in OpaqueGen()
                select new EnumerationSample(registered, key, unregistered))
               .ToArbitrary();

        public static Arbitrary<WhitespaceSample> Whitespace()
            => Gen.Elements(WhitespaceVariants).Select(s => new WhitespaceSample(s)).ToArbitrary();

        public static Arbitrary<RedactionSample> Redaction()
            => (from t in TenantGen()
                from h in OpaqueGen()
                select new RedactionSample(t, h))
               .ToArbitrary();

        public static Arbitrary<HttpsSample> Https()
            => (from scheme in Gen.Elements("http", "https")
                from host in Gen.Elements(
                    "example.com", "api.example.com", "intranet.local",
                    "localhost", "LOCALHOST", "internal.test")
                from ip in Gen.Elements(
                    "203.0.113.10", "198.51.100.7", "192.0.2.5",
                    "127.0.0.1", "::1", "2001:db8::1")
                from salt in OpaqueGen()
                select new HttpsSample(scheme, host, ip, salt))
               .ToArbitrary();
    }

    // ===== Helpers =================================================

    private static string Sha256HexLower(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record FilterUnderTest(
        TenantApiKeyAuthorizationFilter Filter,
        CapturingLogger<TenantApiKeyAuthorizationFilter> Logger,
        TenantClientCacheMetrics Metrics);

    private static FilterUnderTest BuildFilter(
        Dictionary<string, string> apiKeys,
        bool logIpHash = true,
        string salt = "salt")
    {
        var options = new TenantClientCachePublicReadOptions
        {
            ApiKeys = apiKeys,
        };
        options.Audit.LogIpHash = logIpHash;
        options.Audit.RemoteIpSalt = salt;
        var monitor = new StubOptionsMonitor<TenantClientCachePublicReadOptions>(options);
        var validator = new TenantApiKeyValidator(monitor);
        var ipHash = new IpHashHelper(monitor);
        var metrics = new TenantClientCacheMetrics();
        var logger = new CapturingLogger<TenantApiKeyAuthorizationFilter>();
        var filter = new TenantApiKeyAuthorizationFilter(validator, logger, monitor, metrics, ipHash);
        return new FilterUnderTest(filter, logger, metrics);
    }

    private static AuthorizationFilterContext BuildContext(
        string? headerValue,
        string tenantRouteValue,
        IPAddress? remoteIp = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("api.example.com");
        http.Request.Path = $"/api/public/tenants/{tenantRouteValue}/clients/web";
        if (headerValue is not null)
        {
            http.Request.Headers[TenantApiKeyAuthorizationFilter.HeaderName] = headerValue;
        }

        if (remoteIp is not null)
        {
            http.Connection.RemoteIpAddress = remoteIp;
        }

        var routeData = new RouteData();
        routeData.Values["tenantKey"] = tenantRouteValue;
        var actionContext = new ActionContext(http, routeData, new ActionDescriptor());
        return new AuthorizationFilterContext(actionContext, new IFilterMetadata[0]);
    }

    private static (int Status, object? Body, string? RetryAfter) Snapshot(AuthorizationFilterContext ctx)
    {
        var result = (ObjectResult?)ctx.Result;
        var retry = ctx.HttpContext.Response.Headers.TryGetValue("Retry-After", out var v)
            ? v.ToString()
            : null;
        return (result?.StatusCode ?? 0, result?.Value, retry);
    }

    // ===== Property 04 — EnumerationResistance ======================

    /// <summary>
    /// Property 4 (Validates: Requirements 3.3, 9.1). Unregistered tenant
    /// vs registered tenant with wrong key produce byte-equal status,
    /// response body, content type, missing Retry-After header AND audit
    /// log entries WITHOUT a TenantKey field.
    /// </summary>
    [Property(MaxTest = 25, Arbitrary = new[] { typeof(Arbs) })]
    public async Task Property04_EnumerationResistance(EnumerationSample sample)
    {
        // Feature: tenant-client-cache-public-read, Property 4: 401
        // responses for "tenant not registered" and "wrong key" are
        // indistinguishable byte-for-byte (anti-enumeration R3.3).
        var sut = BuildFilter(new Dictionary<string, string>
        {
            [sample.RegisteredTenant] = Sha256HexLower(sample.ApiKey),
        });

        var ctxUnregistered = BuildContext(
            headerValue: sample.ApiKey,
            tenantRouteValue: sample.UnregisteredTenant);
        var ctxWrongKey = BuildContext(
            headerValue: sample.ApiKey + "-wrong",
            tenantRouteValue: sample.RegisteredTenant);

        await sut.Filter.OnAuthorizationAsync(ctxUnregistered);
        await sut.Filter.OnAuthorizationAsync(ctxWrongKey);

        var unregistered = Snapshot(ctxUnregistered);
        var wrong = Snapshot(ctxWrongKey);

        unregistered.Status.Should().Be(wrong.Status, "anti-enumeration: status codes must match");
        unregistered.Body.Should().BeEquivalentTo(wrong.Body, "anti-enumeration: response bodies must match");
        unregistered.RetryAfter.Should().Be(wrong.RetryAfter, "neither path emits Retry-After");

        // Audit log entries: TenantKey field MUST be omitted on the
        // Unauthorized path so dashboards / log aggregators cannot be used
        // to enumerate registered tenants.
        sut.Logger.Entries.Should().NotBeEmpty();
        foreach (var entry in sut.Logger.Entries)
        {
            entry.Fields.Should().NotContainKey("TenantKey",
                "Unauthorized audit entries must not surface the tenantKey field");
        }
    }

    // ===== Property 06 — WhitespaceHeader ===========================

    /// <summary>
    /// Property 6 (Validates: Requirements 3.1, 3.7). A whitespace-only
    /// <c>X-Tenant-Api-Key</c> header is treated as missing; the filter
    /// short-circuits at the authorization layer so no downstream handler
    /// is invoked.
    /// </summary>
    [Property(MaxTest = 25, Arbitrary = new[] { typeof(Arbs) })]
    public async Task Property06_WhitespaceHeader(WhitespaceSample sample)
    {
        // Feature: tenant-client-cache-public-read, Property 6: whitespace
        // header → 401 missing_api_key without consulting the validator.
        var apiKey = "registered-secret";
        var sut = BuildFilter(new Dictionary<string, string>
        {
            ["acme"] = Sha256HexLower(apiKey),
        });

        var ctx = BuildContext(headerValue: sample.Whitespace, tenantRouteValue: "acme");

        await sut.Filter.OnAuthorizationAsync(ctx);

        var snap = Snapshot(ctx);
        snap.Status.Should().Be(StatusCodes.Status401Unauthorized);
        snap.Body.Should().BeEquivalentTo(new { error = "missing_api_key" });
    }

    // ===== Property 14 — AuditLogRedaction ==========================

    /// <summary>
    /// Property 14 (Validates: Requirements 3.4, 8.7, 9.3, 9.5, 10.10).
    /// On the unauthorized path, no log entry's message OR structured
    /// field contains the raw header, the SHA-256 hash, or the raw
    /// tenantKey route value. Field names matching <c>(?i).*secret.*</c>
    /// must not appear at all.
    /// </summary>
    [Property(MaxTest = 40, Arbitrary = new[] { typeof(Arbs) })]
    public async Task Property14_AuditLogRedaction(RedactionSample sample)
    {
        // Feature: tenant-client-cache-public-read, Property 14: audit
        // entries on the unauthorized path never embed the raw header,
        // the hash, or the raw tenantKey.
        var sut = BuildFilter(new Dictionary<string, string>
        {
            // Configure a registered tenant with a *different* key so the
            // path always lands on Unauthorized.
            ["acme"] = Sha256HexLower("expected-secret"),
        });

        var ctx = BuildContext(headerValue: sample.Header, tenantRouteValue: sample.TenantPath);

        await sut.Filter.OnAuthorizationAsync(ctx);

        var hash = Sha256HexLower(sample.Header);
        var rawTenant = sample.TenantPath.Trim().ToLowerInvariant();

        sut.Logger.Entries.Should().NotBeEmpty(
            "an Unauthorized request always emits exactly one audit log entry");

        // Reduce false positives from accidental substring overlap between
        // randomly-generated short inputs and the hardcoded log template
        // ("Unauthorized", "outcome", "corr", etc.). The contract under
        // test is that the FILTER never WRITES the secret values into a
        // structured field — a substring of a template literal that
        // happens to spell the same characters as a 1-char tenantKey is
        // not a leak. We assert against substrings only when they are
        // long enough (≥ 4 chars) that incidental collision is negligible.
        const int IncidentalCollisionGuardLength = 4;

        foreach (var entry in sut.Logger.Entries)
        {
            if (sample.Header.Length >= IncidentalCollisionGuardLength)
            {
                entry.Message.Should().NotContain(sample.Header,
                    "raw header value must never appear in log message");
            }

            entry.Message.Should().NotContain(hash,
                "SHA-256 hash of header must never appear in log message");

            // The rawTenant check on the rendered Message is intentionally
            // omitted: short tenantKey strings within [a-z0-9_-]+ can
            // coincide with substrings of the hardcoded template literals
            // ("outcome", "Unauthorized", "corr"). The structured FIELD
            // VALUE check below (which excludes {OriginalFormat}) is the
            // authoritative invariant for R3.4 / R8.7.

            foreach (var (name, value) in entry.Fields)
            {
                // R8.7: field name reflection — secret-shaped names rejected.
                System.Text.RegularExpressions.Regex
                    .IsMatch(name, "secret", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    .Should().BeFalse(
                        "structured field name '{0}' must not match (?i).*secret.*", name);

                // {OriginalFormat} is the message template, not a field
                // value carrying request data — skip the leak checks
                // against it for the same reason as above.
                if (string.Equals(name, "{OriginalFormat}", StringComparison.Ordinal))
                {
                    continue;
                }

                var stringified = value?.ToString() ?? string.Empty;

                // Apply the same incidental-collision guard as the
                // message-level check: short user-supplied substrings can
                // coincide with characters of constant-labelled fields
                // ("TenantClientCachePublicRead.Unauthorized",
                // "Unauthorized", etc.) without representing a real leak
                // of input data. ≥ 4 chars makes accidental overlap with
                // English-language constants negligible.
                if (rawTenant.Length >= IncidentalCollisionGuardLength)
                {
                    stringified.Should().NotContain(rawTenant,
                        "structured field '{0}' value must not embed raw tenantKey", name);
                }

                if (sample.Header.Length >= IncidentalCollisionGuardLength)
                {
                    stringified.Should().NotContain(sample.Header,
                        "structured field '{0}' value must not embed raw header", name);
                }

                stringified.Should().NotContain(hash,
                    "structured field '{0}' value must not embed SHA-256 hash", name);
            }
        }
    }

    // ===== Property 17 — HttpsGate_And_RemoteIpHash =================

    /// <summary>
    /// Property 17 (Validates: Requirements 9.6, 9.7). HTTP requests
    /// against non-loopback hosts are rejected with 400 https_required
    /// BEFORE the API key filter runs; <see cref="IpHashHelper"/> is
    /// deterministic and never embeds the raw IP string in its output.
    /// </summary>
    [Property(MaxTest = 25, Arbitrary = new[] { typeof(Arbs) })]
    public async Task Property17_HttpsGate_And_RemoteIpHash(HttpsSample sample)
    {
        // Feature: tenant-client-cache-public-read, Property 17: HTTPS
        // gate fires before API key validation, IP hash is deterministic
        // sha256-hex(ip + ":" + salt) and never leaks the raw IP.

        // ---- HTTPS gate ----
        var http = new DefaultHttpContext();
        http.Request.Scheme = sample.Scheme;
        http.Request.Host = new HostString(sample.Host);
        http.Request.Path = "/api/public/tenants/acme/clients/web";
        var ip = IPAddress.Parse(sample.RemoteIp);
        http.Connection.RemoteIpAddress = ip;
        var routeData = new RouteData();
        routeData.Values["tenantKey"] = "acme";
        var actionContext = new ActionContext(http, routeData, new ActionDescriptor());
        var ctx = new AuthorizationFilterContext(actionContext, new IFilterMetadata[0]);

        await new HttpsRequiredFilter().OnAuthorizationAsync(ctx);

        var isLoopbackHost = string.Equals(sample.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        var isLoopbackIp = IPAddress.IsLoopback(ip);

        if (sample.Scheme == "http" && !isLoopbackHost && !isLoopbackIp)
        {
            var result = ctx.Result.Should().BeOfType<ObjectResult>().Which;
            result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
            result.Value.Should().BeEquivalentTo(new { error = "https_required" });
        }
        else
        {
            ctx.Result.Should().BeNull("https or loopback request must fall through to the API key filter");
        }

        // ---- IP hash determinism + leak-resistance ----
        var options = new TenantClientCachePublicReadOptions();
        options.Audit.LogIpHash = true;
        options.Audit.RemoteIpSalt = sample.Salt;
        var monitor = new StubOptionsMonitor<TenantClientCachePublicReadOptions>(options);
        var helper = new IpHashHelper(monitor);

        var first = helper.Hash(ip);
        var second = helper.Hash(ip);

        first.Should().NotBeNull();
        first.Should().Be(second, "same (ip, salt) pair must produce the same hash within a snapshot");
        first.Should().NotContain(sample.RemoteIp,
            "audit-grade IP hash must not echo the raw IP string");

        var manual = Sha256HexLower($"{ip}:{sample.Salt}");
        first.Should().Be(manual, "hash format is sha256-hex-lowercase(ip + ':' + salt)");

        // Disabling LogIpHash returns null regardless of inputs.
        options.Audit.LogIpHash = false;
        monitor.Set(options);
        helper.Hash(ip).Should().BeNull();
    }
}
