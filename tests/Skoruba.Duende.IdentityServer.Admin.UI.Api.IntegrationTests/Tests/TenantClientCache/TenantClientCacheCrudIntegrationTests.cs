// Feature: tenant-client-cache-expansion, Task 10
//
// End-to-end CRUD integration tests for the tenant-client-cache feature.
// Each test drives the live ClientsController via the in-process
// WebApplicationFactory (TenantClientCacheTestHost) wired with the real
// TenantClientCacheService + IClientTenantScopeResolver and a
// MemoryDistributedCache (no live Redis).
//
// Validates: Requirements 4.1, 4.2, 5.1, 5.4, 5.5, 6.1, 6.2, 7.1, 7.2,
//            10.1, 10.2, 12.1, 12.2, 12.3, 17.1, 17.4

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Dtos.Clients;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Common;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;
using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Tests.TenantClientCache;

public sealed class TenantClientCacheCrudIntegrationTests
{
    // ----- Helpers --------------------------------------------------------

    private static ClientApiDto NewClient(string clientId, params string[] tenants)
    {
        var dto = new ClientApiDto
        {
            ClientId = clientId,
            ClientName = clientId,
            ProtocolType = "oidc",
            AllowedScopes = new List<string> { "openid" },
            RedirectUris = new List<string> { $"https://{clientId}/callback" },
        };
        foreach (var t in tenants)
        {
            dto.TenantRedirectPairs.Add(new ClientTenantRedirectPairApiDto
            {
                TenantKey = t,
                SignInCallbackUrl = $"https://{t}.example/callback",
                SignOutCallbackUrl = $"https://{t}.example/signout",
            });
        }
        return dto;
    }

    private static ClientCacheSnapshotEnvelope? ReadSnapshot(
        IDistributedCache cache, string tenantKey, string clientId)
    {
        var key = $"{tenantKey.Trim().ToLowerInvariant()}:clients:{clientId.Trim()}";
        var bytes = cache.Get(key);
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }
        return ClientCacheSnapshotSerializer.TryDeserialize(bytes, out _);
    }

    private static IEnumerable<CapturedLogEntry> WhereEvent(
        TenantClientCacheTestHost host, string eventType)
        => host.LoggerProvider.Entries
            .Where(e => e.Fields.TryGetValue("EventType", out var v)
                        && v is string s
                        && s == eventType);

    // ===== R17.4 (a) Add =================================================

    [Fact]
    public async Task AddClient_PersistsSnapshotPerTenant()
    {
        using var host = TenantClientCacheTestHost.Create();
        var apiDto = NewClient("acme-app", "acme", "globex");

        var response = await host.Client.PostAsJsonAsync("/api/Clients", apiDto);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        // Each tenant gets its own snapshot, keyed by `{tenant}:clients:{clientId}`.
        foreach (var tenantKey in new[] { "acme", "globex" })
        {
            var envelope = ReadSnapshot(host.DistributedCache, tenantKey, "acme-app");
            envelope.Should().NotBeNull($"snapshot for tenant '{tenantKey}' must be persisted after Add");
            envelope!.TenantKey.Should().Be(tenantKey);
            envelope.ClientId.Should().Be("acme-app");
            envelope.Version.Should().Be(1);
            envelope.Data.ClientId.Should().Be("acme-app");
            envelope.Data.ProtocolType.Should().Be("oidc");
            envelope.Data.RedirectUris.Should().ContainSingle().Which.Should().Be("https://acme-app/callback");
        }
    }

    // ===== R17.4 (b) Update ==============================================

    [Fact]
    public async Task UpdateClient_ReplacesSnapshot_FreshLastWriteUtc()
    {
        using var host = TenantClientCacheTestHost.Create();

        // Seed via POST so the snapshot is initially present.
        var initial = NewClient("upd-client", "acme");
        var postResp = await host.Client.PostAsJsonAsync("/api/Clients", initial);
        postResp.EnsureSuccessStatusCode();

        var firstEnvelope = ReadSnapshot(host.DistributedCache, "acme", "upd-client");
        firstEnvelope.Should().NotBeNull();

        // Sleep past clock resolution so LastWriteUtc strictly increases.
        await Task.Delay(20);

        // Build the put payload from the seeded primary key (1).
        var update = NewClient("upd-client", "acme");
        update.Id = host.ClientStore.Snapshot.Single().Key;
        update.AllowedScopes.Add("profile");
        update.RedirectUris[0] = "https://upd-client/callback-v2";

        var putResp = await host.Client.PutAsJsonAsync("/api/Clients", update);
        putResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var secondEnvelope = ReadSnapshot(host.DistributedCache, "acme", "upd-client");
        secondEnvelope.Should().NotBeNull();
        secondEnvelope!.LastWriteUtc.Should().BeAfter(firstEnvelope!.LastWriteUtc);
        secondEnvelope.Data.RedirectUris.Should().Contain("https://upd-client/callback-v2");
        secondEnvelope.Data.AllowedScopes.Should().Contain("profile");
    }

    // ===== R17.4 (c) Delete ==============================================

    [Fact]
    public async Task DeleteClient_RemovesSnapshot_PerTenant()
    {
        using var host = TenantClientCacheTestHost.Create();

        var seed = NewClient("del-client", "acme", "globex");
        var postResp = await host.Client.PostAsJsonAsync("/api/Clients", seed);
        postResp.EnsureSuccessStatusCode();
        var clientPk = host.ClientStore.Snapshot.Single().Key;

        ReadSnapshot(host.DistributedCache, "acme", "del-client").Should().NotBeNull();
        ReadSnapshot(host.DistributedCache, "globex", "del-client").Should().NotBeNull();

        var deleteResp = await host.Client.DeleteAsync($"/api/Clients/{clientPk}");
        deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        ReadSnapshot(host.DistributedCache, "acme", "del-client").Should().BeNull();
        ReadSnapshot(host.DistributedCache, "globex", "del-client").Should().BeNull();
    }

    // ===== R17.4 (d) Clone ===============================================

    [Fact]
    public async Task CloneClient_NewSnapshot_SourceIntact()
    {
        using var host = TenantClientCacheTestHost.Create();

        // Seed source client through POST so the source snapshot is real.
        var source = NewClient("source-client", "acme");
        var postResp = await host.Client.PostAsJsonAsync("/api/Clients", source);
        postResp.EnsureSuccessStatusCode();
        var sourcePk = host.ClientStore.Snapshot.Single().Key;
        var sourceSnapshotBefore = host.DistributedCache.Get($"acme:clients:source-client");
        sourceSnapshotBefore.Should().NotBeNull();

        var cloneApi = new ClientCloneApiDto
        {
            Id = sourcePk,
            ClientId = "cloned-client",
            ClientName = "cloned-client",
            CloneClientCorsOrigins = true,
            CloneClientRedirectUris = true,
            CloneClientIdPRestrictions = true,
            CloneClientPostLogoutRedirectUris = true,
            CloneClientGrantTypes = true,
            CloneClientScopes = true,
            CloneClientClaims = true,
            CloneClientProperties = true,
        };

        var cloneResp = await host.Client.PostAsJsonAsync("/api/Clients/Clone", cloneApi);
        cloneResp.StatusCode.Should().Be(HttpStatusCode.Created);

        // Clone snapshot present.
        var cloneSnap = ReadSnapshot(host.DistributedCache, "acme", "cloned-client");
        cloneSnap.Should().NotBeNull();
        cloneSnap!.Data.ClientId.Should().Be("cloned-client");

        // Source snapshot byte-for-byte unchanged (R7.2).
        var sourceSnapshotAfter = host.DistributedCache.Get("acme:clients:source-client");
        sourceSnapshotAfter.Should().NotBeNull();
        sourceSnapshotAfter.Should().BeEquivalentTo(sourceSnapshotBefore);
    }

    // ===== R17.4 (e) Redis down (CRUD still succeeds, audit emitted) =====

    [Fact]
    public async Task RedisDown_Crud_ReturnsSuccessHttp_AuditEventEmitted()
    {
        using var host = TenantClientCacheTestHost.Create(b => b.UseThrowingCache = true);
        host.ThrowingCache.Should().NotBeNull();
        host.ThrowingCache!.AlwaysThrow(() =>
            new InvalidOperationException("connstr=cache,password=ignored,auth=ignored"));

        // POST — write fails internally, controller still returns 201.
        var postResp = await host.Client.PostAsJsonAsync(
            "/api/Clients", NewClient("redis-down", "acme"));
        postResp.StatusCode.Should().Be(HttpStatusCode.Created);

        // PUT — internal write fails, NoContent.
        var update = NewClient("redis-down", "acme");
        update.Id = host.ClientStore.Snapshot.Single().Key;
        var putResp = await host.Client.PutAsJsonAsync("/api/Clients", update);
        putResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // DELETE — internal invalidate fails, NoContent.
        var deleteResp = await host.Client.DeleteAsync(
            $"/api/Clients/{host.ClientStore.Snapshot.Single().Key}");
        deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Each op emitted an Audit_Event with a transient outcome. We have
        // two write events (POST + PUT) plus one invalidate event (DELETE).
        var writeFailures = WhereEvent(host, "TenantClientCacheWrite")
            .Where(e => HasOutcome(e, "write_failed_transient"))
            .ToList();
        writeFailures.Should().HaveCountGreaterThanOrEqualTo(2);

        var invalidateFailures = WhereEvent(host, "TenantClientCacheInvalidate")
            .Where(e => HasOutcome(e, "invalidate_failed_transient"))
            .ToList();
        invalidateFailures.Should().HaveCountGreaterThanOrEqualTo(1);

        // The injected secret-bearing exception message must NOT leak verbatim
        // — LogRedaction.SanitizeExceptionMessage replaces password/auth keys.
        host.LoggerProvider.Entries
            .Select(e => e.Message + " " + (e.Fields.TryGetValue("ExceptionMessage", out var v) ? v?.ToString() : null))
            .Where(s => s is not null)
            .Should().NotContain(s => s!.Contains("password=ignored", StringComparison.Ordinal));
    }

    // ===== R17.4 (f) Oversize snapshot ===================================

    [Fact]
    public async Task OversizeSnapshot_Rejected_AuditEmitted_HttpStillSuccess()
    {
        using var host = TenantClientCacheTestHost.Create();

        var huge = NewClient("oversize-client", "acme");
        // Build a redirect URI list large enough to push the serialized
        // envelope past 256 KiB (the size guard in
        // ClientCacheSnapshotSizeGuard.MaxBytes). Each entry is ~400 bytes,
        // so 1500 entries comfortably exceeds the cap.
        for (int i = 0; i < 1500; i++)
        {
            huge.RedirectUris.Add($"https://oversize-{i}.example.com/{new string('a', 350)}");
        }

        var postResp = await host.Client.PostAsJsonAsync("/api/Clients", huge);
        postResp.StatusCode.Should().Be(HttpStatusCode.Created);

        // The snapshot must NOT be persisted.
        ReadSnapshot(host.DistributedCache, "acme", "oversize-client")
            .Should().BeNull("oversize payloads are rejected by the size guard");

        // Audit event with subreason=oversize.
        var oversize = WhereEvent(host, "TenantClientCacheWrite")
            .Where(e => e.Fields.TryGetValue("Subreason", out var s) && (string?)s == "oversize")
            .ToList();
        oversize.Should().NotBeEmpty();
        oversize.First().Level.Should().Be(LogLevel.Warning);
    }

    // ===== R17.4 (g) Disabled feature flag ===============================

    [Fact]
    public async Task Enabled_False_AllOps_NoOp()
    {
        using var host = TenantClientCacheTestHost.Create(b => b.Options.Enabled = false);

        // POST succeeds with no cache I/O.
        var postResp = await host.Client.PostAsJsonAsync(
            "/api/Clients", NewClient("disabled-client", "acme"));
        postResp.StatusCode.Should().Be(HttpStatusCode.Created);

        // PUT succeeds.
        var update = NewClient("disabled-client", "acme");
        update.Id = host.ClientStore.Snapshot.Single().Key;
        var putResp = await host.Client.PutAsJsonAsync("/api/Clients", update);
        putResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // DELETE succeeds.
        var deleteResp = await host.Client.DeleteAsync(
            $"/api/Clients/{host.ClientStore.Snapshot.Single().Key}");
        deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // ZERO snapshots present in the cache after every CRUD op.
        ReadSnapshot(host.DistributedCache, "acme", "disabled-client").Should().BeNull();

        // The audit log shows write_skipped_disabled for each op.
        var skipped = host.LoggerProvider.Entries
            .Where(e => e.Fields.TryGetValue("Outcome", out var v)
                        && (string?)v == "write_skipped_disabled")
            .ToList();
        skipped.Should().HaveCountGreaterThanOrEqualTo(3);
    }

    // ===== Legacy IClientScopeCacheService coexistence ===================

    [Fact]
    public async Task LegacyClientScopeCacheService_StillWorks_Independently()
    {
        // R12.1, R12.2, R12.3: legacy cache writes a bare-clientId key
        // alongside (NOT inside) the new tenant-scoped key namespace.
        using var host = TenantClientCacheTestHost.Create();

        var addResp = await host.Client.PostAsJsonAsync(
            "/api/Clients", NewClient("legacy-coexist", "acme"));
        addResp.StatusCode.Should().Be(HttpStatusCode.Created);

        // Legacy bare-clientId key was set by ClientScopeCacheService.
        host.DistributedCache.Get("legacy-coexist").Should().NotBeNull(
            "the legacy IClientScopeCacheService writes a bare-clientId key for backward compat (R12.2)");

        // The new tenant-scoped key is independent.
        host.DistributedCache.Get("acme:clients:legacy-coexist").Should().NotBeNull(
            "the new tenant-scoped key namespace must be populated alongside the legacy key");

        // Delete clears both.
        var deleteResp = await host.Client.DeleteAsync(
            $"/api/Clients/{host.ClientStore.Snapshot.Single().Key}");
        deleteResp.EnsureSuccessStatusCode();

        host.DistributedCache.Get("legacy-coexist").Should().BeNull();
        host.DistributedCache.Get("acme:clients:legacy-coexist").Should().BeNull();
    }

    private static bool HasOutcome(CapturedLogEntry entry, string expected)
        => entry.Fields.TryGetValue("Outcome", out var v) && (string?)v == expected;
}
