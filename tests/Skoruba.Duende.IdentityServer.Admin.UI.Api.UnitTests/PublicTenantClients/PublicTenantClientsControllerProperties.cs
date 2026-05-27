// Feature: tenant-client-cache-public-read, Task 5
//
// Property-based tests for PublicTenantClientsController.
//
// Property 05 — PathInputsOnly      (Validates: Requirements 2.2, 2.3, 3.7).
// Property 09 — PathValidation      (Validates: Requirements 7.1, 7.2).
// Property 10 — Serialization+ETag  (Validates: Requirements 2.4, 2.5, 6.1, 6.8).
// Property 11 — IfNoneMatch         (Validates: Requirements 6.4, 6.5).
// Property 12 — ResponseHeader      (Validates: Requirements 2.6, 6.2, 6.3, 6.6, 6.7, 9.8).
// Property 13 — FailureBodySchema   (Validates: Requirements 7.5, 7.6, 7.7, 7.8).

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

using Moq;

using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Controllers;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.PublicTenantClients;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache.Helpers;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.PublicTenantClients;

[Collection(PublicReadMetricCollection.Name)]
public sealed class PublicTenantClientsControllerProperties
{
    // ===== Generators =========================================================

    public sealed record PathSample(string PathTenant, string PathClient, string ForeignTenant, string ForeignClient);
    public sealed record EnvelopeSample(string ClientId, string ClientName, int AccessTokenLifetime, bool Enabled);
    public sealed record IfNoneMatchSample(string Variant, bool ShouldMatch);
    public sealed record HeaderSample(int MaxAgeSeconds, int Version, DateTime LastWrite);

    public static class Arbs
    {
        private static readonly char[] TenantAlphabet =
            "abcdefghijklmnopqrstuvwxyz0123456789_-".ToCharArray();
        private static readonly char[] ClientAlphabet =
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.:".ToCharArray();

        private static Gen<string> TenantGen()
            => from len in Gen.Choose(2, 16)
               from chars in Gen.Elements(TenantAlphabet).ListOf(len)
               select new string(chars.ToArray());

        private static Gen<string> ClientGen()
            => from len in Gen.Choose(1, 16)
               from chars in Gen.Elements(ClientAlphabet).ListOf(len)
               select new string(chars.ToArray());

        public static Arbitrary<PathSample> Path()
            => (from t in TenantGen()
                from c in ClientGen()
                from ft in TenantGen()
                from fc in ClientGen()
                select new PathSample(t, c, ft, fc))
               .ToArbitrary();

        public static Arbitrary<EnvelopeSample> Envelope()
            => (from c in ClientGen()
                from name in Gen.Elements("Sample", "Web", "Mobile", "Internal", "")
                from atl in Gen.Choose(60, 86400)
                from enabled in Gen.Elements(true, false)
                select new EnvelopeSample(c, name, atl, enabled))
               .ToArbitrary();

        public static Arbitrary<HeaderSample> Headers()
            => (from m in Gen.Choose(0, 3600)
                from v in Gen.Choose(1, 1000)
                from ticks in Gen.Choose(0, 100_000_000)
                select new HeaderSample(
                    m, v,
                    new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(ticks)))
               .ToArbitrary();
    }

    // ===== Builders ===========================================================

    private sealed record Harness(
        PublicTenantClientsController Controller,
        Mock<ITenantClientCacheService> Snapshots,
        DefaultHttpContext HttpContext,
        StubOptionsMonitor<TenantClientCachePublicReadOptions> Monitor);

    private static Harness Build(int maxAgeSeconds = 60)
    {
        var options = new TenantClientCachePublicReadOptions();
        options.ResponseCache.MaxAgeSeconds = maxAgeSeconds;
        options.Audit.LogIpHash = false;
        options.Audit.RemoteIpSalt = "salt";

        var monitor = new StubOptionsMonitor<TenantClientCachePublicReadOptions>(options);
        var ipHash = new IpHashHelper(monitor);
        var metrics = new TenantClientCacheMetrics();
        var logger = new CapturingLogger<PublicTenantClientsController>();
        var snapshots = new Mock<ITenantClientCacheService>(MockBehavior.Loose);

        var controller = new PublicTenantClientsController(
            snapshots.Object, monitor, metrics, logger, ipHash);

        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Get;
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("api.example.com");
        http.Response.Body = new MemoryStream();

        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return new Harness(controller, snapshots, http, monitor);
    }

    private static ClientCacheSnapshotEnvelope MakeEnvelope(
        string tenant, string client, int version = 1, DateTime? lastWrite = null,
        EnvelopeSample? sample = null)
    {
        sample ??= new EnvelopeSample(client, "Sample", 3600, true);
        var write = lastWrite ?? new DateTime(2024, 5, 1, 12, 30, 45, DateTimeKind.Utc);
        return new ClientCacheSnapshotEnvelope
        {
            Version = version,
            TenantKey = tenant,
            ClientId = client,
            LastWriteUtc = write,
            Data = new ClientCacheSnapshotDto
            {
                ClientId = client,
                ClientName = sample.ClientName,
                ProtocolType = "oidc",
                Enabled = sample.Enabled,
                AccessTokenLifetime = sample.AccessTokenLifetime,
                AllowedScopes = new[] { "openid", "profile" },
                RedirectUris = new[] { "https://app/callback" },
                LastWriteUtc = write,
            },
        };
    }

    private static string ComputeExpectedEtag(ClientCacheSnapshotDto data)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            data,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = false });
        var hash = SHA256.HashData(bytes);
        return "W/\"" + Convert.ToHexString(hash).ToLowerInvariant() + "\"";
    }

    // ===== Property 05 — PathInputsOnly ======================================

    /// <summary>
    /// Property 5 (Validates: Requirements 2.2, 2.3, 3.7). The controller
    /// passes the URL-bound tenantKey/clientId — normalized — to
    /// <c>ITenantClientCacheService.ReadSnapshotAsync</c>, ignoring any
    /// foreign values supplied via query string or other headers.
    /// </summary>
    [Property(MaxTest = 25, Arbitrary = new[] { typeof(Arbs) })]
    public async Task Property05_PathInputsOnly(PathSample sample)
    {
        // Feature: tenant-client-cache-public-read, Property 5: path-only.
        var h = Build();
        h.HttpContext.Request.QueryString = new QueryString(
            $"?tenantKey={Uri.EscapeDataString(sample.ForeignTenant)}"
            + $"&clientId={Uri.EscapeDataString(sample.ForeignClient)}");
        h.HttpContext.Request.Headers["X-Tenant-Other"] = sample.ForeignTenant;

        string capturedT = "";
        string capturedC = "";
        h.Snapshots
            .Setup(s => s.ReadSnapshotAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((t, c, _) =>
            {
                capturedT = t;
                capturedC = c;
            })
            .ReturnsAsync(MakeEnvelope(
                sample.PathTenant.Trim().ToLowerInvariant(),
                sample.PathClient.Trim()));

        await h.Controller.GetAsync(sample.PathTenant, sample.PathClient, default);

        capturedT.Should().Be(sample.PathTenant.Trim().ToLowerInvariant());
        capturedC.Should().Be(sample.PathClient.Trim());
    }

    // ===== Property 09 — PathValidation ======================================

    /// <summary>
    /// Property 9 (Validates: Requirements 7.1, 7.2). Malformed tenantKey
    /// and clientId values are rejected with HTTP 400 and a stable error
    /// code; <c>ReadSnapshotAsync</c> is NEVER invoked.
    /// </summary>
    [Property(MaxTest = 25)]
    public async Task Property09_PathValidation_TenantKey(int seed)
    {
        // Generator-free: pick from a closed set of malformed inputs.
        // After Trim().ToLowerInvariant() these still violate the
        // ^[a-z0-9_-]+$ regex OR exceed the 128-char ceiling.
        var malformedTenants = new[] { "", "   ", "Has Space", "../etc", "x" + new string('y', 200), "dot.bad", "tenant!" };
        var tenant = malformedTenants[Math.Abs(seed) % malformedTenants.Length];

        var h = Build();
        var result = await h.Controller.GetAsync(tenant, "valid-client", default);

        var oResult = result.Should().BeOfType<ObjectResult>().Which;
        oResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        oResult.Value.Should().BeEquivalentTo(new { error = "invalid_tenant_key" });

        h.Snapshots.Verify(
            s => s.ReadSnapshotAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Property(MaxTest = 25)]
    public async Task Property09_PathValidation_ClientId(int seed)
    {
        var malformedClients = new[] { "", "   ", "\t", "\n", new string('c', 201) };
        var client = malformedClients[Math.Abs(seed) % malformedClients.Length];

        var h = Build();
        var result = await h.Controller.GetAsync("acme", client, default);

        var oResult = result.Should().BeOfType<ObjectResult>().Which;
        oResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        oResult.Value.Should().BeEquivalentTo(new { error = "invalid_client_id" });

        h.Snapshots.Verify(
            s => s.ReadSnapshotAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ===== Property 10 — Serialization + ETag determinism ====================

    /// <summary>
    /// Property 10 (Validates: Requirements 2.4, 2.5, 6.1, 6.8). The ETag
    /// is the deterministic SHA-256 hex of the serialized envelope.Data,
    /// and the response body root never includes the envelope's outer
    /// fields (<c>version</c>, <c>tenantKey</c>, <c>clientId</c>,
    /// <c>lastWriteUtc</c>) at the top level.
    /// </summary>
    [Property(MaxTest = 40, Arbitrary = new[] { typeof(Arbs) })]
    public async Task Property10_SerializationAndEtagDeterminism(EnvelopeSample sample)
    {
        var h = Build();
        var envelope = MakeEnvelope("acme", string.IsNullOrEmpty(sample.ClientId) ? "web" : sample.ClientId, sample: sample);
        var clientId = envelope.ClientId;

        h.Snapshots
            .Setup(s => s.ReadSnapshotAsync("acme", clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(envelope);

        await h.Controller.GetAsync("acme", clientId, default);

        var expectedEtag = ComputeExpectedEtag(envelope.Data);
        h.HttpContext.Response.Headers["ETag"].ToString().Should().Be(expectedEtag);

        // Body root must not include envelope's outer metadata fields.
        var body = ((MemoryStream)h.HttpContext.Response.Body).ToArray();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.ValueKind.Should().Be(JsonValueKind.Object);
        var keys = root.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        keys.Should().NotContain("version");
        keys.Should().NotContain("tenantKey");
    }

    // ===== Property 11 — If-None-Match negotiation ===========================

    /// <summary>
    /// Property 11 (Validates: Requirements 6.4, 6.5). For an ETag E:
    ///   * exact, weak, whitespace-padded, and listed forms ALL match → 304
    ///   * "*" wildcard matches → 304
    ///   * an unrelated tag does NOT match → 200 with the body
    /// </summary>
    [Property(MaxTest = 25)]
    public async Task Property11_IfNoneMatchNegotiation(int seed)
    {
        var h = Build();
        var envelope = MakeEnvelope("acme", "web");
        h.Snapshots
            .Setup(s => s.ReadSnapshotAsync("acme", "web", It.IsAny<CancellationToken>()))
            .ReturnsAsync(envelope);

        var etag = ComputeExpectedEtag(envelope.Data);
        var inner = etag.Substring(2); // strip W/

        var matchVariants = new[]
        {
            etag,                                    // exact W/"hex"
            "  " + etag + "  ",                       // surrounding whitespace
            inner,                                    // strong form (no W/)
            "*",                                      // wildcard
            "W/\"unrelated\", " + etag,               // list with match second
        };
        var nonMatchVariants = new[]
        {
            "W/\"completely-different\"",
            "W/\"another\"",
        };

        // Match variant.
        var match = matchVariants[Math.Abs(seed) % matchVariants.Length];
        h.HttpContext.Request.Headers["If-None-Match"] = match;
        var result = await h.Controller.GetAsync("acme", "web", default);
        var status = result.Should().BeOfType<StatusCodeResult>().Which.StatusCode;
        status.Should().Be(StatusCodes.Status304NotModified, $"variant '{match}' should be a match");

        // Non-match variant — fresh harness so previous response state does not leak.
        var h2 = Build();
        h2.Snapshots
            .Setup(s => s.ReadSnapshotAsync("acme", "web", It.IsAny<CancellationToken>()))
            .ReturnsAsync(envelope);
        var noMatch = nonMatchVariants[Math.Abs(seed) % nonMatchVariants.Length];
        h2.HttpContext.Request.Headers["If-None-Match"] = noMatch;
        var result2 = await h2.Controller.GetAsync("acme", "web", default);
        result2.Should().BeOfType<EmptyResult>();
        h2.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    // ===== Property 12 — Response header completeness ========================

    /// <summary>
    /// Property 12 (Validates: Requirements 2.6, 6.2, 6.3, 6.6, 6.7, 9.8).
    /// Every successful response carries the canonical header set —
    /// ETag, Cache-Control, Vary, X-Snapshot-Last-Write-Utc,
    /// X-Snapshot-Version, X-Content-Type-Options — with the configured
    /// max-age value.
    /// </summary>
    [Property(MaxTest = 25, Arbitrary = new[] { typeof(Arbs) })]
    public async Task Property12_ResponseHeaderCompleteness(HeaderSample sample)
    {
        var h = Build(maxAgeSeconds: sample.MaxAgeSeconds);
        var envelope = MakeEnvelope("acme", "web", version: sample.Version, lastWrite: sample.LastWrite);
        h.Snapshots
            .Setup(s => s.ReadSnapshotAsync("acme", "web", It.IsAny<CancellationToken>()))
            .ReturnsAsync(envelope);

        await h.Controller.GetAsync("acme", "web", default);

        h.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        h.HttpContext.Response.ContentType.Should().Be("application/json; charset=utf-8");
        h.HttpContext.Response.Headers["ETag"].ToString().Should().NotBeNullOrEmpty();
        h.HttpContext.Response.Headers["Cache-Control"].ToString()
            .Should().Be($"public, max-age={sample.MaxAgeSeconds}, no-transform");
        h.HttpContext.Response.Headers["Vary"].ToString().Should().Be("X-Tenant-Api-Key");
        h.HttpContext.Response.Headers["X-Snapshot-Last-Write-Utc"].ToString()
            .Should().Be(sample.LastWrite.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        h.HttpContext.Response.Headers["X-Snapshot-Version"].ToString()
            .Should().Be(sample.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        h.HttpContext.Response.Headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");
    }

    // ===== Property 13 — Failure body schema closed ==========================

    /// <summary>
    /// Property 13 (Validates: Requirements 7.5, 7.6, 7.7, 7.8). Every
    /// terminal failure outcome serializes to a JSON object with EXACTLY
    /// one property <c>error</c> (string), and the status code is one of
    /// the documented {400, 404, 503} values returned by the controller
    /// directly. (401/429/503-snapshot_unavailable are produced by other
    /// pipeline filters.)
    /// </summary>
    [Property(MaxTest = 25)]
    public async Task Property13_FailureBodySchemaClosed(int seed)
    {
        var outcomes = new (Func<Harness, Task<IActionResult>> Drive, int Status, string Error)[]
        {
            (async h => await h.Controller.GetAsync("BAD!", "valid", default),
                StatusCodes.Status400BadRequest, "invalid_tenant_key"),
            (async h => await h.Controller.GetAsync("acme", "", default),
                StatusCodes.Status400BadRequest, "invalid_client_id"),
            (async h =>
            {
                h.Snapshots
                    .Setup(s => s.ReadSnapshotAsync("acme", "web", It.IsAny<CancellationToken>()))
                    .ReturnsAsync((ClientCacheSnapshotEnvelope?)null);
                return await h.Controller.GetAsync("acme", "web", default);
            }, StatusCodes.Status404NotFound, "snapshot_not_found"),
            (async h =>
            {
                h.Snapshots
                    .Setup(s => s.ReadSnapshotAsync("acme", "web", It.IsAny<CancellationToken>()))
                    .ReturnsAsync(MakeEnvelope("acme", "web", version: 0));
                return await h.Controller.GetAsync("acme", "web", default);
            }, StatusCodes.Status503ServiceUnavailable, "snapshot_pipeline_disabled"),
        };

        var pick = outcomes[Math.Abs(seed) % outcomes.Length];
        var h = Build();

        var result = await pick.Drive(h);

        var oResult = result.Should().BeOfType<ObjectResult>().Which;
        oResult.StatusCode.Should().Be(pick.Status);
        oResult.Value.Should().BeEquivalentTo(new { error = pick.Error });

        // Closed schema: object with exactly one property "error" of type string.
        var json = JsonSerializer.SerializeToUtf8Bytes(oResult.Value);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.ValueKind.Should().Be(JsonValueKind.Object);
        var keys = root.EnumerateObject().Select(p => p.Name).ToList();
        keys.Should().Equal(new List<string> { "error" });
        root.GetProperty("error").ValueKind.Should().Be(JsonValueKind.String);

        // R7.7: never 3xx; controller never produces 5xx other than 503.
        oResult.StatusCode!.Value.Should().NotBeInRange(300, 399);
        if (oResult.StatusCode!.Value >= 500)
        {
            oResult.StatusCode!.Value.Should().Be(StatusCodes.Status503ServiceUnavailable);
        }
    }
}
