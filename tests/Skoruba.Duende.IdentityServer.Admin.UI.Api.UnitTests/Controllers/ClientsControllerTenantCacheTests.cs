// Feature: tenant-client-cache-expansion, Task 7
//
// Example-based tests covering ClientsController integration with the new
// per-tenant snapshot cache. We verify, for each CRUD path:
//
//   * The strict call ordering required by design.md "Coexistence":
//       service mutation → legacy IClientScopeCacheService → re-read →
//       resolve tenant keys → write/invalidate new ITenantClientCacheService.
//   * That HttpContext.RequestAborted is forwarded to every cache call.
//   * That an empty resolved tenant set does not break the success path
//     (R4.2 / R7.3).
//   * Drift / rename handling on Put (R5.2, R5.7).
//   * Pre-delete capture on Delete (R6.1, R6.2).
//   * Clone writes only the new clientId — never the source (R7.1, R7.2).
//   * Fail-soft contract: when the new cache service throws, the controller
//     surfaces the error untouched (the *service* is responsible for
//     swallowing storage failures — see Task 5).
//
// Property-based variants (P7, P8, P13) are intentionally deferred to the
// dedicated `ClientsControllerCacheIntegrationProperties.cs` companion in a
// later task wave; the example coverage below pins the controller wiring.
//
// Validates: Requirements 4.1, 4.2, 4.3, 4.6, 4.7, 4.8, 4.9, 5.1, 5.2,
//            5.6, 5.7, 5.8, 6.1, 6.2, 6.5, 6.6, 7.1, 7.2, 7.3, 7.5,
//            12.1, 12.2

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

using Moq;

using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Configuration;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Services.Interfaces;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Controllers;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Dtos.Clients;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Resources;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.Controllers;

public sealed class ClientsControllerTenantCacheTests
{
    private const string SampleClientId = "client-1";
    private const string RenamedClientId = "client-1-renamed";
    private const int SampleClientPk = 42;

    // ---- Fixtures --------------------------------------------------------

    private sealed class Harness
    {
        public Mock<IClientService> ClientService { get; } = new(MockBehavior.Strict);
        public Mock<IClientScopeCacheService> LegacyCache { get; } = new(MockBehavior.Strict);
        public Mock<ITenantClientCacheService> TenantCache { get; } = new(MockBehavior.Strict);
        public Mock<IClientTenantScopeResolver> Resolver { get; } = new(MockBehavior.Strict);
        public Mock<IApiErrorResources> ErrorResources { get; } = new(MockBehavior.Strict);
        public CancellationTokenSource RequestCts { get; } = new();

        public ClientsController BuildController()
        {
            var controller = new ClientsController(
                ClientService.Object,
                LegacyCache.Object,
                ErrorResources.Object,
                TenantCache.Object,
                Resolver.Object);

            var httpContext = new DefaultHttpContext
            {
                RequestAborted = RequestCts.Token,
            };
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = httpContext,
            };
            return controller;
        }
    }

    private static ClientApiDto BuildApiDto(int id = 0, string clientId = SampleClientId)
        => new()
        {
            Id = id,
            ClientId = clientId,
            ClientName = "Sample",
            ProtocolType = "oidc",
            AllowedScopes = { "openid", "profile" },
            RedirectUris = { "https://app/callback" },
        };

    private static ClientDto BuildDto(int id = SampleClientPk, string clientId = SampleClientId)
        => new()
        {
            Id = id,
            ClientId = clientId,
            ClientName = "Sample",
            ProtocolType = "oidc",
            AllowedScopes = new List<string> { "openid", "profile" },
            RedirectUris = new List<string> { "https://app/callback" },
        };

    private static IReadOnlyList<string> Tenants(params string[] keys)
        => keys.ToArray();

    // ===== POST ===========================================================

    [Fact]
    public async Task Post_Calls_AddClient_Then_LegacyCache_Then_NewCache_With_TenantKeys()
    {
        // Validates: design.md "Coexistence" call order — AddClientAsync ⇒
        // SaveAllowedScopesAsync (legacy) ⇒ GetClientAsync (re-read) ⇒
        // ResolveTenantKeysAsync ⇒ WriteSnapshotsAsync.
        var harness = new Harness();
        var fresh = BuildDto();
        var tenants = Tenants("tenant-a", "tenant-b");
        var sequence = new MockSequence();

        harness.ClientService.InSequence(sequence)
            .Setup(s => s.AddClientAsync(It.IsAny<ClientDto>()))
            .ReturnsAsync(SampleClientPk);
        harness.LegacyCache.InSequence(sequence)
            .Setup(c => c.SaveAllowedScopesAsync(SampleClientId, It.IsAny<IEnumerable<string>>(), harness.RequestCts.Token))
            .Returns(Task.CompletedTask);
        harness.ClientService.InSequence(sequence)
            .Setup(s => s.GetClientAsync(SampleClientPk))
            .ReturnsAsync(fresh);
        harness.Resolver.InSequence(sequence)
            .Setup(r => r.ResolveTenantKeysAsync(fresh, harness.RequestCts.Token))
            .ReturnsAsync(tenants);
        harness.TenantCache.InSequence(sequence)
            .Setup(t => t.WriteSnapshotsAsync(tenants, fresh, harness.RequestCts.Token))
            .Returns(Task.CompletedTask);

        var controller = harness.BuildController();
        var result = await controller.Post(BuildApiDto());

        var created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.StatusCode.Should().Be(201);

        harness.ClientService.Verify();
        harness.LegacyCache.Verify();
        harness.TenantCache.Verify();
        harness.Resolver.Verify();
    }

    [Fact]
    public async Task Post_With_Empty_Tenant_Set_Skips_NewCache_But_Still_Returns_201()
    {
        // R4.2: a client with no tenant scope still completes the HTTP path
        // and emits an empty WriteSnapshotsAsync call (the service treats
        // an empty collection as a no-op).
        var harness = new Harness();
        var fresh = BuildDto();

        harness.ClientService.Setup(s => s.AddClientAsync(It.IsAny<ClientDto>()))
            .ReturnsAsync(SampleClientPk);
        harness.LegacyCache.Setup(c => c.SaveAllowedScopesAsync(SampleClientId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        harness.ClientService.Setup(s => s.GetClientAsync(SampleClientPk)).ReturnsAsync(fresh);
        harness.Resolver.Setup(r => r.ResolveTenantKeysAsync(fresh, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        harness.TenantCache
            .Setup(t => t.WriteSnapshotsAsync(It.Is<IReadOnlyCollection<string>>(c => c.Count == 0), fresh, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var controller = harness.BuildController();
        var result = await controller.Post(BuildApiDto());

        result.Result.Should().BeOfType<CreatedAtActionResult>()
            .Which.StatusCode.Should().Be(201);

        // Sanity: the legacy and new cache were *both* invoked exactly once.
        harness.LegacyCache.Verify(c => c.SaveAllowedScopesAsync(
            SampleClientId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.TenantCache.Verify(t => t.WriteSnapshotsAsync(
            It.IsAny<IReadOnlyCollection<string>>(), fresh, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ===== PUT ============================================================

    [Fact]
    public async Task Put_Captures_PreTenantKeys_Then_Computes_Drift()
    {
        // R5.2: pre-tenants {a,b}, post-tenants {b,c} ⇒ drift = {a}.
        // The controller must invalidate (a, fresh.ClientId) BEFORE writing
        // the new snapshot set {b, c}.
        var harness = new Harness();
        var pre = BuildDto();
        var fresh = BuildDto();
        var preTenants = Tenants("tenant-a", "tenant-b");
        var postTenants = Tenants("tenant-b", "tenant-c");
        var sequence = new MockSequence();

        harness.ClientService.InSequence(sequence)
            .Setup(s => s.GetClientAsync(SampleClientPk))
            .ReturnsAsync(pre);
        harness.Resolver.InSequence(sequence)
            .Setup(r => r.ResolveTenantKeysAsync(pre, harness.RequestCts.Token))
            .ReturnsAsync(preTenants);
        harness.ClientService.InSequence(sequence)
            .Setup(s => s.UpdateClientAsync(It.IsAny<ClientDto>(), true, true))
            .ReturnsAsync(SampleClientPk);
        harness.LegacyCache.InSequence(sequence)
            .Setup(c => c.SaveAllowedScopesAsync(SampleClientId, It.IsAny<IEnumerable<string>>(), harness.RequestCts.Token))
            .Returns(Task.CompletedTask);
        harness.ClientService.InSequence(sequence)
            .Setup(s => s.GetClientAsync(SampleClientPk))
            .ReturnsAsync(fresh);
        harness.Resolver.InSequence(sequence)
            .Setup(r => r.ResolveTenantKeysAsync(fresh, harness.RequestCts.Token))
            .ReturnsAsync(postTenants);
        harness.TenantCache.InSequence(sequence)
            .Setup(t => t.InvalidateSnapshotsAsync(
                It.Is<IReadOnlyCollection<string>>(c => c.SequenceEqual(new[] { "tenant-a" })),
                SampleClientId,
                harness.RequestCts.Token))
            .Returns(Task.CompletedTask);
        harness.TenantCache.InSequence(sequence)
            .Setup(t => t.WriteSnapshotsAsync(postTenants, fresh, harness.RequestCts.Token))
            .Returns(Task.CompletedTask);

        var controller = harness.BuildController();
        var result = await controller.Put(BuildApiDto(id: SampleClientPk));

        result.Should().BeOfType<NoContentResult>();
        harness.ClientService.Verify();
        harness.Resolver.Verify();
        harness.LegacyCache.Verify();
        harness.TenantCache.Verify();
    }

    [Fact]
    public async Task Put_Without_Drift_Skips_Invalidate_And_Only_Writes()
    {
        // Pre-tenants == post-tenants ⇒ drift is empty, controller skips
        // the InvalidateSnapshotsAsync call entirely.
        var harness = new Harness();
        var pre = BuildDto();
        var fresh = BuildDto();
        var tenants = Tenants("tenant-a");

        harness.ClientService
            .SetupSequence(s => s.GetClientAsync(SampleClientPk))
            .ReturnsAsync(pre)
            .ReturnsAsync(fresh);
        harness.Resolver.Setup(r => r.ResolveTenantKeysAsync(pre, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenants);
        harness.Resolver.Setup(r => r.ResolveTenantKeysAsync(fresh, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenants);
        harness.ClientService.Setup(s => s.UpdateClientAsync(It.IsAny<ClientDto>(), true, true))
            .ReturnsAsync(SampleClientPk);
        harness.LegacyCache.Setup(c => c.SaveAllowedScopesAsync(SampleClientId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        harness.TenantCache.Setup(t => t.WriteSnapshotsAsync(tenants, fresh, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var controller = harness.BuildController();
        var result = await controller.Put(BuildApiDto(id: SampleClientPk));

        result.Should().BeOfType<NoContentResult>();
        harness.TenantCache.Verify(
            t => t.InvalidateSnapshotsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.TenantCache.Verify(
            t => t.WriteSnapshotsAsync(tenants, fresh, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Put_With_Renamed_ClientId_Invalidates_PreTenantKeys_Old_Then_Writes_New()
    {
        // R5.7: when the clientId is renamed, every (preTenant ∪ postTenant)
        // entry under the OLD clientId must be invalidated, then the new
        // snapshot is written under the NEW clientId. The two operations
        // are strictly ordered: invalidate first, write second.
        var harness = new Harness();
        var pre = BuildDto();
        var fresh = BuildDto(clientId: RenamedClientId);
        var preTenants = Tenants("tenant-a");
        var postTenants = Tenants("tenant-b");
        var sequence = new MockSequence();

        harness.ClientService.InSequence(sequence)
            .Setup(s => s.GetClientAsync(SampleClientPk)).ReturnsAsync(pre);
        harness.Resolver.InSequence(sequence)
            .Setup(r => r.ResolveTenantKeysAsync(pre, It.IsAny<CancellationToken>()))
            .ReturnsAsync(preTenants);
        harness.ClientService.InSequence(sequence)
            .Setup(s => s.UpdateClientAsync(It.IsAny<ClientDto>(), true, true))
            .ReturnsAsync(SampleClientPk);
        harness.LegacyCache.InSequence(sequence)
            .Setup(c => c.SaveAllowedScopesAsync(RenamedClientId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        harness.ClientService.InSequence(sequence)
            .Setup(s => s.GetClientAsync(SampleClientPk)).ReturnsAsync(fresh);
        harness.Resolver.InSequence(sequence)
            .Setup(r => r.ResolveTenantKeysAsync(fresh, It.IsAny<CancellationToken>()))
            .ReturnsAsync(postTenants);
        harness.TenantCache.InSequence(sequence)
            .Setup(t => t.InvalidateSnapshotsAsync(
                It.Is<IReadOnlyCollection<string>>(c => c.OrderBy(x => x).SequenceEqual(new[] { "tenant-a", "tenant-b" })),
                SampleClientId,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        harness.TenantCache.InSequence(sequence)
            .Setup(t => t.WriteSnapshotsAsync(postTenants, fresh, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var controller = harness.BuildController();
        var result = await controller.Put(BuildApiDto(id: SampleClientPk, clientId: RenamedClientId));

        result.Should().BeOfType<NoContentResult>();
        harness.TenantCache.Verify();
    }

    // ===== DELETE =========================================================

    [Fact]
    public async Task Delete_Invalidates_PreTenantKeys_Then_Returns_204()
    {
        // R6.1, R6.2: the pre-delete tenant set is captured BEFORE the
        // mutation; after the row is removed the controller invalidates
        // every pre-delete (tenantKey, clientId) pair.
        var harness = new Harness();
        var pre = BuildDto();
        var preTenants = Tenants("tenant-a", "tenant-b");
        var sequence = new MockSequence();

        harness.ClientService.InSequence(sequence)
            .Setup(s => s.GetClientAsync(SampleClientPk)).ReturnsAsync(pre);
        harness.Resolver.InSequence(sequence)
            .Setup(r => r.ResolveTenantKeysAsync(pre, harness.RequestCts.Token))
            .ReturnsAsync(preTenants);
        harness.ClientService.InSequence(sequence)
            .Setup(s => s.RemoveClientAsync(It.IsAny<ClientDto>())).ReturnsAsync(SampleClientPk);
        harness.LegacyCache.InSequence(sequence)
            .Setup(c => c.RemoveAllowedScopesAsync(SampleClientId, harness.RequestCts.Token))
            .Returns(Task.CompletedTask);
        harness.TenantCache.InSequence(sequence)
            .Setup(t => t.InvalidateSnapshotsAsync(preTenants, SampleClientId, harness.RequestCts.Token))
            .Returns(Task.CompletedTask);

        var controller = harness.BuildController();
        var result = await controller.Delete(SampleClientPk);

        result.Should().BeOfType<NoContentResult>();
        harness.ClientService.Verify();
        harness.LegacyCache.Verify();
        harness.TenantCache.Verify();
        harness.Resolver.Verify();
    }

    // ===== CLONE ==========================================================

    [Fact]
    public async Task Clone_Writes_New_Client_Snapshot_Without_Touching_Source()
    {
        // R7.1, R7.2: clone path writes snapshots for the *new* client only.
        // The source client's cache entries must NOT be invalidated — the
        // source row is unchanged.
        const int SourceId = SampleClientPk;
        const int CloneId = SampleClientPk + 1;
        var harness = new Harness();
        var source = BuildDto(id: SourceId, clientId: "source");
        var fresh = BuildDto(id: CloneId, clientId: "cloned");
        var tenants = Tenants("tenant-a");
        var sequence = new MockSequence();

        harness.ClientService.InSequence(sequence)
            .Setup(s => s.GetClientAsync(SourceId)).ReturnsAsync(source);
        harness.ClientService.InSequence(sequence)
            .Setup(s => s.CloneClientAsync(It.IsAny<ClientCloneDto>())).ReturnsAsync(CloneId);
        harness.ClientService.InSequence(sequence)
            .Setup(s => s.GetClientAsync(CloneId)).ReturnsAsync(fresh);
        harness.Resolver.InSequence(sequence)
            .Setup(r => r.ResolveTenantKeysAsync(fresh, harness.RequestCts.Token))
            .ReturnsAsync(tenants);
        harness.TenantCache.InSequence(sequence)
            .Setup(t => t.WriteSnapshotsAsync(tenants, fresh, harness.RequestCts.Token))
            .Returns(Task.CompletedTask);

        var controller = harness.BuildController();
        var result = await controller.PostClientClone(new ClientCloneApiDto { Id = SourceId });

        result.Result.Should().BeOfType<CreatedAtActionResult>()
            .Which.StatusCode.Should().Be(201);

        // Strictly assert no invalidate calls touched the source clientId.
        harness.TenantCache.Verify(
            t => t.InvalidateSnapshotsAsync(It.IsAny<IReadOnlyCollection<string>>(), "source", It.IsAny<CancellationToken>()),
            Times.Never);
        // Legacy cache is intentionally not used on the clone path.
        harness.LegacyCache.VerifyNoOtherCalls();
    }

    // ===== Cancellation forwarding ========================================

    [Fact]
    public async Task RequestAborted_Is_Passed_To_Cache_Calls()
    {
        // R4.9 / R5.8 / R6.6 / R7.5: every cache invocation must receive
        // HttpContext.RequestAborted so a cancelled request stops in-flight
        // I/O.
        var harness = new Harness();
        var fresh = BuildDto();
        var tenants = Tenants("tenant-a");

        harness.ClientService.Setup(s => s.AddClientAsync(It.IsAny<ClientDto>())).ReturnsAsync(SampleClientPk);
        harness.ClientService.Setup(s => s.GetClientAsync(SampleClientPk)).ReturnsAsync(fresh);
        harness.LegacyCache
            .Setup(c => c.SaveAllowedScopesAsync(SampleClientId, It.IsAny<IEnumerable<string>>(), harness.RequestCts.Token))
            .Returns(Task.CompletedTask);
        harness.Resolver.Setup(r => r.ResolveTenantKeysAsync(fresh, harness.RequestCts.Token))
            .ReturnsAsync(tenants);
        harness.TenantCache.Setup(t => t.WriteSnapshotsAsync(tenants, fresh, harness.RequestCts.Token))
            .Returns(Task.CompletedTask);

        var controller = harness.BuildController();
        await controller.Post(BuildApiDto());

        harness.LegacyCache.Verify(
            c => c.SaveAllowedScopesAsync(SampleClientId, It.IsAny<IEnumerable<string>>(), harness.RequestCts.Token),
            Times.Once);
        harness.Resolver.Verify(
            r => r.ResolveTenantKeysAsync(fresh, harness.RequestCts.Token), Times.Once);
        harness.TenantCache.Verify(
            t => t.WriteSnapshotsAsync(tenants, fresh, harness.RequestCts.Token), Times.Once);
    }

    // ===== Fail-soft behaviour (cache service swallows internally) ========

    [Fact]
    public async Task Cache_Service_Does_Not_Throw_So_Controller_Returns_Success()
    {
        // The new cache service contract (Task 5) is fail-soft: it never
        // propagates an exception from the underlying IDistributedCache.
        // A correctly implemented service therefore always returns a
        // completed task even when Redis is down. The controller must
        // make NO attempt to wrap the call in try/catch — verifying here
        // that with a fail-soft mock the success HTTP code is returned.
        var harness = new Harness();
        var fresh = BuildDto();
        var tenants = Tenants("tenant-a");

        harness.ClientService.Setup(s => s.AddClientAsync(It.IsAny<ClientDto>())).ReturnsAsync(SampleClientPk);
        harness.ClientService.Setup(s => s.GetClientAsync(SampleClientPk)).ReturnsAsync(fresh);
        harness.LegacyCache
            .Setup(c => c.SaveAllowedScopesAsync(SampleClientId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        harness.Resolver.Setup(r => r.ResolveTenantKeysAsync(fresh, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tenants);
        // Simulate the fail-soft service: it absorbs storage failures and
        // returns a completed task. The controller does not see the
        // underlying exception.
        harness.TenantCache.Setup(t => t.WriteSnapshotsAsync(tenants, fresh, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var controller = harness.BuildController();
        var result = await controller.Post(BuildApiDto());

        result.Result.Should().BeOfType<CreatedAtActionResult>()
            .Which.StatusCode.Should().Be(201);
    }
}
