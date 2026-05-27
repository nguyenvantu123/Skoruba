// Feature: tenant-client-cache-public-read, Task 5
//
// Example-based tests for PublicTenantClientsController covering:
//   * 200 happy path with the canonical response header set (R2.4, R2.6,
//     R6.1, R6.2, R6.3, R6.6, R6.7, R9.8).
//   * HEAD parity — same headers, empty body, Content-Length set (R2.9).
//   * If-None-Match negotiation: exact match, with/without W/, with
//     surrounding whitespace, list, wildcard "*" (R6.4, R6.5).
//   * 404 snapshot_not_found when ReadSnapshotAsync returns null (R7.3).
//   * 503 snapshot_pipeline_disabled when envelope.Version <= 0 (R7.4).
//   * 400 invalid_tenant_key / invalid_client_id with NO call to the
//     cache service (R7.1, R7.2).
//   * Path inputs only — query / body / non-key headers ignored (R2.2,
//     R3.7).
//   * RequestAborted propagation (R2.8).

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

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
public class PublicTenantClientsControllerTests
{
    private const string Tenant = "acme";
    private const string Client = "web";

    // ===== Builders ============================================================

    private sealed record Harness(
        PublicTenantClientsController Controller,
        Mock<ITenantClientCacheService> Snapshots,
        StubOptionsMonitor<TenantClientCachePublicReadOptions> Monitor,
        TenantClientCacheMetrics Metrics,
        CapturingLogger<PublicTenantClientsController> Logger,
        DefaultHttpContext HttpContext);

    private static Harness Build(int maxAgeSeconds = 60)
    {
        var options = new TenantClientCachePublicReadOptions();
        options.ResponseCache.MaxAgeSeconds = maxAgeSeconds;
        options.Audit.LogIpHash = false;          // keep tests isolated from IP-hash noise
        options.Audit.RemoteIpSalt = "test-salt";
        var monitor = new StubOptionsMonitor<TenantClientCachePublicReadOptions>(options);
        var ipHash = new IpHashHelper(monitor);
        var metrics = new TenantClientCacheMetrics();
        var logger = new CapturingLogger<PublicTenantClientsController>();
        var snapshots = new Mock<ITenantClientCacheService>(MockBehavior.Strict);

        var controller = new PublicTenantClientsController(
            snapshots.Object, monitor, metrics, logger, ipHash);

        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Get;
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("api.example.com");
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        // A response body that is a real MemoryStream so we can read it back.
        http.Response.Body = new MemoryStream();

        return new Harness(controller, snapshots, monitor, metrics, logger, http);
    }

    private static ClientCacheSnapshotEnvelope MakeEnvelope(
        string tenant = Tenant,
        string client = Client,
        int version = 1,
        DateTime? lastWrite = null)
    {
        return new ClientCacheSnapshotEnvelope
        {
            Version = version,
            TenantKey = tenant,
            ClientId = client,
            LastWriteUtc = lastWrite ?? new DateTime(2024, 5, 1, 12, 30, 45, DateTimeKind.Utc),
            Data = new ClientCacheSnapshotDto
            {
                ClientId = client,
                ClientName = "Sample",
                ProtocolType = "oidc",
                Enabled = true,
                AccessTokenLifetime = 3600,
                AllowedScopes = new[] { "openid", "profile" },
                RedirectUris = new[] { "https://app/callback" },
                LastWriteUtc = lastWrite ?? new DateTime(2024, 5, 1, 12, 30, 45, DateTimeKind.Utc),
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

    // ===== 200 happy path =====================================================

    [Fact]
    public async Task Get_HappyPath_Returns_200_With_Headers_And_Body()
    {
        var h = Build(maxAgeSeconds: 120);
        var envelope = MakeEnvelope();
        h.Snapshots
            .Setup(s => s.ReadSnapshotAsync(Tenant, Client, It.IsAny<CancellationToken>()))
            .ReturnsAsync(envelope);

        var result = await h.Controller.GetAsync(Tenant, Client, default);

        result.Should().BeOfType<EmptyResult>();
        h.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        h.HttpContext.Response.ContentType.Should().Be("application/json; charset=utf-8");

        var expectedEtag = ComputeExpectedEtag(envelope.Data);
        h.HttpContext.Response.Headers["ETag"].ToString().Should().Be(expectedEtag);
        h.HttpContext.Response.Headers["Cache-Control"].ToString()
            .Should().Be("public, max-age=120, no-transform");
        h.HttpContext.Response.Headers["Vary"].ToString().Should().Be("X-Tenant-Api-Key");
        h.HttpContext.Response.Headers["X-Snapshot-Last-Write-Utc"].ToString()
            .Should().Be(envelope.LastWriteUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        h.HttpContext.Response.Headers["X-Snapshot-Version"].ToString().Should().Be("1");
        h.HttpContext.Response.Headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");

        // Body equals JSON of envelope.Data, NOT the envelope.
        var body = ((MemoryStream)h.HttpContext.Response.Body).ToArray();
        var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        root.TryGetProperty("clientId", out var cid).Should().BeTrue();
        cid.GetString().Should().Be(Client);
        // The envelope's outer fields MUST NOT appear at the body root (R2.5).
        root.TryGetProperty("version", out _).Should().BeFalse();
        root.TryGetProperty("tenantKey", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Head_Same_Headers_Empty_Body_ContentLength_Set()
    {
        var h = Build();
        h.HttpContext.Request.Method = HttpMethods.Head;
        var envelope = MakeEnvelope();
        h.Snapshots
            .Setup(s => s.ReadSnapshotAsync(Tenant, Client, It.IsAny<CancellationToken>()))
            .ReturnsAsync(envelope);

        var result = await h.Controller.GetAsync(Tenant, Client, default);

        result.Should().BeOfType<EmptyResult>();
        h.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        h.HttpContext.Response.Headers["ETag"].ToString()
            .Should().Be(ComputeExpectedEtag(envelope.Data));
        h.HttpContext.Response.Headers["X-Snapshot-Version"].ToString().Should().Be("1");
        h.HttpContext.Response.ContentLength.Should().NotBeNull().And.NotBe(0);

        var body = ((MemoryStream)h.HttpContext.Response.Body).ToArray();
        body.Length.Should().Be(0, "HEAD must not write a response body");
    }

    // ===== If-None-Match negotiation =========================================

    [Fact]
    public async Task IfNoneMatch_Matching_Returns_304_Same_Headers_Empty_Body()
    {
        var h = Build();
        var envelope = MakeEnvelope();
        var etag = ComputeExpectedEtag(envelope.Data);
        h.HttpContext.Request.Headers["If-None-Match"] = etag;
        h.Snapshots
            .Setup(s => s.ReadSnapshotAsync(Tenant, Client, It.IsAny<CancellationToken>()))
            .ReturnsAsync(envelope);

        var result = await h.Controller.GetAsync(Tenant, Client, default);

        var status = result.Should().BeOfType<StatusCodeResult>().Which.StatusCode;
        status.Should().Be(StatusCodes.Status304NotModified);
        h.HttpContext.Response.Headers["ETag"].ToString().Should().Be(etag);
        h.HttpContext.Response.Headers["Cache-Control"].ToString()
            .Should().Be("public, max-age=60, no-transform");
        h.HttpContext.Response.Headers["X-Snapshot-Version"].ToString().Should().Be("1");
        var body = ((MemoryStream)h.HttpContext.Response.Body).ToArray();
        body.Length.Should().Be(0);
    }

    [Fact]
    public async Task IfNoneMatch_Wildcard_Returns_304()
    {
        var h = Build();
        h.HttpContext.Request.Headers["If-None-Match"] = "*";
        h.Snapshots
            .Setup(s => s.ReadSnapshotAsync(Tenant, Client, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeEnvelope());

        var result = await h.Controller.GetAsync(Tenant, Client, default);

        result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status304NotModified);
    }

    [Theory]
    [InlineData("\"abc\"", "W/\"abc\"")]               // strong vs weak
    [InlineData(" W/\"abc\" ", "W/\"abc\"")]           // whitespace
    [InlineData("W/\"x\", W/\"abc\"", "W/\"abc\"")]    // list with match second
    public async Task IfNoneMatch_With_W_Prefix_Or_Whitespace_Matches(string ifNoneMatch, string serverEtag)
    {
        // Verifies the static matcher directly so the controller body
        // does not need a snapshot envelope here.
        PublicTenantClientsController.Matches(ifNoneMatch, serverEtag).Should().BeTrue();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task IfNoneMatch_Mismatch_Returns_200()
    {
        var h = Build();
        h.HttpContext.Request.Headers["If-None-Match"] = "W/\"non-matching-hash\"";
        h.Snapshots
            .Setup(s => s.ReadSnapshotAsync(Tenant, Client, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeEnvelope());

        var result = await h.Controller.GetAsync(Tenant, Client, default);

        result.Should().BeOfType<EmptyResult>();
        h.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        ((MemoryStream)h.HttpContext.Response.Body).Length.Should().BeGreaterThan(0);
    }

    // ===== Failure paths ======================================================

    [Fact]
    public async Task Snapshot_Null_Returns_404_SnapshotNotFound()
    {
        var h = Build();
        h.Snapshots
            .Setup(s => s.ReadSnapshotAsync(Tenant, Client, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ClientCacheSnapshotEnvelope?)null);

        var result = await h.Controller.GetAsync(Tenant, Client, default);

        var oResult = result.Should().BeOfType<ObjectResult>().Which;
        oResult.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        oResult.Value.Should().BeEquivalentTo(new { error = "snapshot_not_found" });
    }

    [Fact]
    public async Task Envelope_Version_LE_Zero_Returns_503_PipelineDisabled()
    {
        var h = Build();
        h.Snapshots
            .Setup(s => s.ReadSnapshotAsync(Tenant, Client, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeEnvelope(version: 0));

        var result = await h.Controller.GetAsync(Tenant, Client, default);

        var oResult = result.Should().BeOfType<ObjectResult>().Which;
        oResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        oResult.Value.Should().BeEquivalentTo(new { error = "snapshot_pipeline_disabled" });
        h.HttpContext.Response.Headers["Retry-After"].ToString().Should().Be("60");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad space")]            // whitespace inside (after trim) is not allowed
    [InlineData("dot.invalid")]          // dot is outside [a-z0-9_-]
    [InlineData("path/invalid")]         // slash is outside [a-z0-9_-]
    [InlineData("tenant!")]              // punctuation rejected
    public async Task InvalidTenantKey_Path_Returns_400_NotInvokesService(string tenant)
    {
        var h = Build();

        var result = await h.Controller.GetAsync(tenant, Client, default);

        var oResult = result.Should().BeOfType<ObjectResult>().Which;
        oResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        oResult.Value.Should().BeEquivalentTo(new { error = "invalid_tenant_key" });
        h.Snapshots.Verify(
            s => s.ReadSnapshotAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task InvalidTenantKey_TooLong_Returns_400()
    {
        var h = Build();
        var tooLong = new string('a', 129);

        var result = await h.Controller.GetAsync(tooLong, Client, default);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        h.Snapshots.Verify(
            s => s.ReadSnapshotAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InvalidClientId_Path_Returns_400_NotInvokesService(string client)
    {
        var h = Build();

        var result = await h.Controller.GetAsync(Tenant, client, default);

        var oResult = result.Should().BeOfType<ObjectResult>().Which;
        oResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        oResult.Value.Should().BeEquivalentTo(new { error = "invalid_client_id" });
        h.Snapshots.Verify(
            s => s.ReadSnapshotAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task InvalidClientId_TooLong_Returns_400()
    {
        var h = Build();
        var tooLong = new string('c', 201);

        var result = await h.Controller.GetAsync(Tenant, tooLong, default);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        h.Snapshots.Verify(
            s => s.ReadSnapshotAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ===== Cancellation + path-only =========================================

    [Fact]
    public async Task RequestAborted_Propagates_To_ReadSnapshotAsync()
    {
        var h = Build();
        var cts = new CancellationTokenSource();
        h.HttpContext.RequestAborted = cts.Token;

        CancellationToken captured = default;
        h.Snapshots
            .Setup(s => s.ReadSnapshotAsync(Tenant, Client, It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, _, ct) => captured = ct)
            .ReturnsAsync(MakeEnvelope());

        await h.Controller.GetAsync(Tenant, Client, default);

        captured.Should().Be(cts.Token, "controller must forward HttpContext.RequestAborted");
    }

    [Fact]
    public async Task Foreign_Values_In_Query_Or_Header_Ignored_PathOnly_Used()
    {
        var h = Build();
        h.HttpContext.Request.QueryString = new QueryString("?tenantKey=other&clientId=other-client");
        h.HttpContext.Request.Headers["X-Other-Header"] = "another-value";
        h.Snapshots
            .Setup(s => s.ReadSnapshotAsync(Tenant, Client, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeEnvelope());

        var result = await h.Controller.GetAsync(Tenant, Client, default);

        result.Should().BeOfType<EmptyResult>();
        h.Snapshots.Verify(
            s => s.ReadSnapshotAsync(Tenant, Client, It.IsAny<CancellationToken>()),
            Times.Once,
            "tenantKey and clientId must come exclusively from the URL path (R2.2, R3.7)");
    }

    [Fact]
    public async Task TenantKey_And_ClientId_Are_Normalized_Before_Service_Call()
    {
        var h = Build();
        h.Snapshots
            .Setup(s => s.ReadSnapshotAsync(Tenant, Client, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeEnvelope());

        var result = await h.Controller.GetAsync("  ACME  ", "  web  ", default);

        result.Should().BeOfType<EmptyResult>();
        h.Snapshots.Verify(
            s => s.ReadSnapshotAsync(Tenant, Client, It.IsAny<CancellationToken>()),
            Times.Once,
            "tenantKey is Trim().ToLowerInvariant() and clientId is Trim() before the call");
    }

    // ===== Constructor guards ===============================================

    [Fact]
    public void Constructor_Throws_On_Null_Dependencies()
    {
        var monitor = new StubOptionsMonitor<TenantClientCachePublicReadOptions>(
            new TenantClientCachePublicReadOptions());
        var ipHash = new IpHashHelper(monitor);
        var metrics = new TenantClientCacheMetrics();
        var logger = new CapturingLogger<PublicTenantClientsController>();
        var snapshots = new Mock<ITenantClientCacheService>().Object;

        Action a1 = () => _ = new PublicTenantClientsController(null!, monitor, metrics, logger, ipHash);
        Action a2 = () => _ = new PublicTenantClientsController(snapshots, null!, metrics, logger, ipHash);
        Action a3 = () => _ = new PublicTenantClientsController(snapshots, monitor, null!, logger, ipHash);
        Action a4 = () => _ = new PublicTenantClientsController(snapshots, monitor, metrics, null!, ipHash);
        Action a5 = () => _ = new PublicTenantClientsController(snapshots, monitor, metrics, logger, null!);

        a1.Should().Throw<ArgumentNullException>().WithParameterName("snapshots");
        a2.Should().Throw<ArgumentNullException>().WithParameterName("options");
        a3.Should().Throw<ArgumentNullException>().WithParameterName("metrics");
        a4.Should().Throw<ArgumentNullException>().WithParameterName("logger");
        a5.Should().Throw<ArgumentNullException>().WithParameterName("ipHash");
    }

    // ===== Layer-boundary guard (R2.7, R12.10) ===============================

    [Fact]
    public void Controller_Does_Not_Inject_Forbidden_Service_Tier_Types()
    {
        // R2.7 / R12.10: the controller must NEVER depend on services that
        // grant access to secret-bearing fields. We assert the constructor
        // signature directly so any future regression that adds a forbidden
        // dependency fails this test.
        var ctor = typeof(PublicTenantClientsController).GetConstructors().Single();
        var paramTypeNames = ctor.GetParameters()
            .Select(p => p.ParameterType.FullName ?? p.ParameterType.Name)
            .ToArray();

        foreach (var forbidden in new[]
                 {
                     "IClientService",
                     "IClientRepository",
                     "IAdminConfigurationDbContext",
                     "DbContext",
                 })
        {
            paramTypeNames.Should().NotContain(t => t!.Contains(forbidden, StringComparison.Ordinal),
                $"controller must not depend on {forbidden} (R2.7, R12.10)");
        }
    }

    // ===== Audit / metric tag policy on terminal outcomes ====================

    [Fact]
    public async Task BadRequest_Counter_Has_No_TenantKey_Tag()
    {
        var h = Build();
        using var listener = new RecordingMeterListener(TenantClientCacheMetrics.MeterName);

        await h.Controller.GetAsync("BAD!", Client, default);

        // The shared Meter "TenantClientCache" can also fire from parallel
        // test classes, so we narrow the assertion to "at least one
        // bad_request increment exists AND none of them carries a
        // tenantKey tag".
        var measurements = listener.ForInstrument(TenantClientCacheMetrics.PublicReadBadRequestCounterName);
        measurements.Should().NotBeEmpty();
        foreach (var m in measurements)
        {
            m.Tags.Should().NotContainKey("tenantKey");
        }
    }

    [Fact]
    public async Task Hit_Counter_And_Histogram_Tagged_With_TenantKey()
    {
        // Use a per-test-unique tenant key so the listener can isolate this
        // test's measurement from any concurrently-running test class.
        const string UniqueTenant = "hit-tag-test-tenant";

        var h = Build();
        h.Snapshots
            .Setup(s => s.ReadSnapshotAsync(UniqueTenant, Client, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeEnvelope(tenant: UniqueTenant));
        using var listener = new RecordingMeterListener(TenantClientCacheMetrics.MeterName);

        await h.Controller.GetAsync(UniqueTenant, Client, default);

        var hit = listener.ForInstrument(TenantClientCacheMetrics.PublicReadHitCounterName)
            .Where(m => m.Tags.TryGetValue("tenantKey", out var v) && Equals(v, UniqueTenant))
            .ToArray();
        hit.Should().ContainSingle();
        hit.Single().Tags.Should().NotContainKey("clientId");

        var histogram = listener.ForInstrument(TenantClientCacheMetrics.PublicReadDurationHistogramName)
            .Where(m => m.Tags.TryGetValue("tenantKey", out var v) && Equals(v, UniqueTenant))
            .ToArray();
        histogram.Should().ContainSingle();
        histogram.Single().Tags.Should().ContainKey("outcome").WhoseValue.Should().Be("Hit");
    }
}
