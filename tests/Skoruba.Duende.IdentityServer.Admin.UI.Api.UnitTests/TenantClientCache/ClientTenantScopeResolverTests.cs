// Feature: tenant-client-cache-expansion, Task 4
//
// Example-based unit tests for ClientTenantScopeResolver. Property-based
// determinism / normalization invariants live next to these in
// ClientTenantScopeResolverProperties.cs (Property06).
//
// Validates: Requirements 11.1, 11.2, 11.3, 11.4, 11.5

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using Moq;

using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Configuration;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Helpers;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Services.Interfaces;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Shared.ExceptionHandling;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache;

public sealed class ClientTenantScopeResolverTests
{
    private static IClientTenantScopeResolver CreateResolver(
        Mock<IClientService>? clientServiceMock = null)
    {
        clientServiceMock ??= new Mock<IClientService>(MockBehavior.Strict);
        return new ClientTenantScopeResolver(clientServiceMock.Object);
    }

    private static ClientPropertyDto LegacyPairsProperty(string json)
        => new ClientPropertyDto
        {
            Key = ClientTenantRedirectPairsHelper.PropertyKey,
            Value = json,
        };

    [Fact]
    public async Task Priority1_DbRows_TakesPriority_Over_PropertyJson()
    {
        // R11.2: when DB-backed pairs exist, the legacy Properties JSON is
        // ignored entirely — even when the JSON would have produced a
        // disjoint tenant set.
        var json = """[{ "tenantKey": "TenantFromJson" }]""";
        var client = new ClientDto
        {
            ClientId = "demo",
            TenantRedirectPairs = new List<ClientTenantRedirectPairDto>
            {
                new() { TenantKey = "TenantFromDb1" },
                new() { TenantKey = "TenantFromDb2" },
            },
            Properties = new List<ClientPropertyDto> { LegacyPairsProperty(json) },
        };
        var resolver = CreateResolver();

        var keys = await resolver.ResolveTenantKeysAsync(client, CancellationToken.None);

        keys.Should().Equal("tenantfromdb1", "tenantfromdb2");
    }

    [Fact]
    public async Task Priority2_PropertyJson_UsedWhen_DbRows_Empty()
    {
        // R11.2: priority 2 fires only when priority 1 returned zero rows.
        var json = """
[
  { "tenantKey": "AcmeTenant",   "signInCallbackUrl": "https://acme/cb" },
  { "tenantKey": "ContosoTenant", "signInCallbackUrl": "https://contoso/cb" }
]
""";
        var client = new ClientDto
        {
            ClientId = "demo",
            TenantRedirectPairs = new List<ClientTenantRedirectPairDto>(),
            Properties = new List<ClientPropertyDto> { LegacyPairsProperty(json) },
        };
        var resolver = CreateResolver();

        var keys = await resolver.ResolveTenantKeysAsync(client, CancellationToken.None);

        keys.Should().Equal("acmetenant", "contosotenant");
    }

    [Fact]
    public async Task Priority3_BothEmpty_Returns_Empty()
    {
        // R11.2: when both DB rows AND property JSON are absent, the result
        // is an empty list — the client is shared/global and not scoped.
        var client = new ClientDto
        {
            ClientId = "demo",
            TenantRedirectPairs = new List<ClientTenantRedirectPairDto>(),
            Properties = new List<ClientPropertyDto>(),
        };
        var resolver = CreateResolver();

        var keys = await resolver.ResolveTenantKeysAsync(client, CancellationToken.None);

        keys.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns_Lowercased_Trimmed_Distinct_LexAsc()
    {
        // R11.3, R11.4: case-insensitively distinct, trimmed, lower-invariant,
        // sorted lexicographic ascending.
        var client = new ClientDto
        {
            ClientId = "demo",
            TenantRedirectPairs = new List<ClientTenantRedirectPairDto>
            {
                new() { TenantKey = "  Zeta " },
                new() { TenantKey = "alpha" },
                new() { TenantKey = "ALPHA" },     // duplicate (case)
                new() { TenantKey = "  Mu" },
                new() { TenantKey = "" },           // dropped (blank)
                new() { TenantKey = "   " },        // dropped (whitespace)
                new() { TenantKey = null! },        // dropped (null)
            },
        };
        var resolver = CreateResolver();

        var keys = await resolver.ResolveTenantKeysAsync(client, CancellationToken.None);

        keys.Should().Equal("alpha", "mu", "zeta");
    }

    [Fact]
    public async Task Malformed_Property_Json_Falls_To_Empty_Not_Throws()
    {
        // R11.5: malformed legacy JSON must NOT throw — mirrors STS
        // ClientTenantRedirectResolver behaviour. The TryParsePairs façade
        // returns false and an empty list.
        var client = new ClientDto
        {
            ClientId = "demo",
            TenantRedirectPairs = new List<ClientTenantRedirectPairDto>(),
            Properties = new List<ClientPropertyDto>
            {
                LegacyPairsProperty("{ this is not valid json"),
            },
        };
        var resolver = CreateResolver();

        var keys = await resolver.ResolveTenantKeysAsync(client, CancellationToken.None);

        keys.Should().BeEmpty();
    }

    [Fact]
    public async Task Empty_ClientId_Returns_Empty_Not_Throws()
    {
        // R11.5: degenerate input (empty ClientId, empty pairs, empty
        // properties) must return an empty list rather than throwing.
        var client = new ClientDto
        {
            ClientId = string.Empty,
            TenantRedirectPairs = new List<ClientTenantRedirectPairDto>(),
            Properties = new List<ClientPropertyDto>(),
        };
        var resolver = CreateResolver();

        var act = async () => await resolver.ResolveTenantKeysAsync(client, CancellationToken.None);

        var keys = await act.Should().NotThrowAsync();
        keys.Subject.Should().BeEmpty();
    }

    [Fact]
    public async Task Null_Client_Overload_Returns_Empty_Not_Throws()
    {
        // R11.5 sibling case: a null DTO surfaced by edge paths must collapse
        // to an empty result. We pin this to keep the controller hot-path
        // forgiving (clone before re-read, etc.).
        var resolver = CreateResolver();

        var keys = await resolver.ResolveTenantKeysAsync((ClientDto)null!, CancellationToken.None);

        keys.Should().BeEmpty();
    }

    [Fact]
    public async Task IntOverload_NonPositive_PrimaryKey_Returns_Empty_NoServiceCall()
    {
        // R11.5: invalid id ⇒ empty result, no DB call. We use Strict mock
        // to assert the resolver does NOT touch IClientService.
        var clientServiceMock = new Mock<IClientService>(MockBehavior.Strict);
        var resolver = CreateResolver(clientServiceMock);

        var keys = await resolver.ResolveTenantKeysAsync(0, CancellationToken.None);

        keys.Should().BeEmpty();
        clientServiceMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task IntOverload_LoadsViaClientService_AndResolves()
    {
        var dto = new ClientDto
        {
            ClientId = "demo",
            TenantRedirectPairs = new List<ClientTenantRedirectPairDto>
            {
                new() { TenantKey = "TENANT-One" },
                new() { TenantKey = "tenant-two" },
            },
        };
        var clientServiceMock = new Mock<IClientService>(MockBehavior.Strict);
        clientServiceMock.Setup(s => s.GetClientAsync(42)).ReturnsAsync(dto);
        var resolver = CreateResolver(clientServiceMock);

        var keys = await resolver.ResolveTenantKeysAsync(42, CancellationToken.None);

        keys.Should().Equal("tenant-one", "tenant-two");
        clientServiceMock.Verify(s => s.GetClientAsync(42), Times.Once);
    }

    [Fact]
    public async Task IntOverload_ClientNotFound_Returns_Empty_NotThrows()
    {
        // The BusinessLogic ClientService throws UserFriendlyErrorPageException
        // when the row is missing — the background sweep may race with a
        // delete. R11.5 says the resolver must NOT propagate, so we expect
        // an empty result.
        var clientServiceMock = new Mock<IClientService>(MockBehavior.Strict);
        clientServiceMock.Setup(s => s.GetClientAsync(99))
            .ThrowsAsync(new UserFriendlyErrorPageException("not found"));
        var resolver = CreateResolver(clientServiceMock);

        var keys = await resolver.ResolveTenantKeysAsync(99, CancellationToken.None);

        keys.Should().BeEmpty();
    }
}
