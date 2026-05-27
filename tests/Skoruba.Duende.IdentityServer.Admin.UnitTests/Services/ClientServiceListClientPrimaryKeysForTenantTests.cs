// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Duende.IdentityServer.EntityFramework.Entities;
using Duende.IdentityServer.EntityFramework.Options;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Skoruba.AuditLogging.Services;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Resources;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Services;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Services.Interfaces;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Admin.Storage.Entities.Configuration;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Repositories;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Repositories.Interfaces;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.DbContexts;
using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UnitTests.Services
{
    public class ClientServiceListClientPrimaryKeysForTenantTests
    {
        private const string TenantRedirectPairsPropertyKey = "skoruba_tenant_redirect_pairs";

        private static IdentityServerConfigurationDbContext GetDbContext()
        {
            var serviceCollection = new ServiceCollection();

            serviceCollection.AddSingleton(new ConfigurationStoreOptions());
            serviceCollection.AddSingleton(new OperationalStoreOptions());

            serviceCollection.AddDbContext<IdentityServerConfigurationDbContext>(builder =>
                builder.UseInMemoryDatabase(Guid.NewGuid().ToString()));

            var serviceProvider = serviceCollection.BuildServiceProvider();
            return serviceProvider.GetRequiredService<IdentityServerConfigurationDbContext>();
        }

        private static IClientService GetClientService(IdentityServerConfigurationDbContext context)
        {
            IClientRepository clientRepository = new ClientRepository<IdentityServerConfigurationDbContext>(context);

            var localizer = new Mock<IClientServiceResources>().Object;
            var auditLogger = new Mock<IAuditEventLogger>().Object;

            return new ClientService(clientRepository, localizer, auditLogger);
        }

        private static Client SeedClient(IdentityServerConfigurationDbContext context, string clientId)
        {
            var client = new Client
            {
                ClientId = clientId,
                ClientName = clientId
            };
            context.Clients.Add(client);
            return client;
        }

        private static void SeedTenantMapping(IdentityServerConfigurationDbContext context, Client client, string tenantKey)
        {
            context.ClientTenantRedirectUris.Add(new ClientTenantRedirectUri
            {
                Client = client,
                ClientId = client.Id,
                TenantKey = tenantKey,
                SignInCallbackUrl = $"https://{tenantKey}.example/callback",
                SignOutCallbackUrl = $"https://{tenantKey}.example/signout",
                CorsOrigin = $"https://{tenantKey}.example"
            });
        }

        private static void SeedTenantPairsProperty(IdentityServerConfigurationDbContext context, Client client, params string[] tenantKeys)
        {
            var json = "[" + string.Join(",", tenantKeys.Select(t =>
                $"{{\"tenantKey\":\"{t}\",\"signInCallbackUrl\":\"https://{t}.example/callback\"}}")) + "]";

            client.Properties ??= new List<ClientProperty>();
            client.Properties.Add(new ClientProperty
            {
                Key = TenantRedirectPairsPropertyKey,
                Value = json
            });
        }

        [Fact]
        public async Task EmptyTenantKey_Throws_ArgumentException()
        {
            using var context = GetDbContext();
            var service = GetClientService(context);

            Func<Task> act = () => service.ListClientPrimaryKeysForTenantAsync("   ", 10, CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>();
        }

        [Fact]
        public async Task NullTenantKey_Throws_ArgumentException()
        {
            using var context = GetDbContext();
            var service = GetClientService(context);

            Func<Task> act = () => service.ListClientPrimaryKeysForTenantAsync(null, 10, CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>();
        }

        [Fact]
        public async Task MaxLessOrEqualZero_Throws_ArgumentOutOfRangeException()
        {
            using var context = GetDbContext();
            var service = GetClientService(context);

            Func<Task> actZero = () => service.ListClientPrimaryKeysForTenantAsync("tenant-a", 0, CancellationToken.None);
            Func<Task> actNegative = () => service.ListClientPrimaryKeysForTenantAsync("tenant-a", -1, CancellationToken.None);

            await actZero.Should().ThrowAsync<ArgumentOutOfRangeException>();
            await actNegative.Should().ThrowAsync<ArgumentOutOfRangeException>();
        }

        [Fact]
        public async Task Priority1_Mapping_Returns_DistinctIds_OrderedAsc_CappedAtMax()
        {
            using var context = GetDbContext();

            var clientA = SeedClient(context, "client-a");
            var clientB = SeedClient(context, "client-b");
            var clientC = SeedClient(context, "client-c");
            await context.SaveChangesAsync();

            // Mapping rows: tenant-a maps to A, B, C; tenant-b maps to B only
            SeedTenantMapping(context, clientA, "tenant-a");
            SeedTenantMapping(context, clientB, "tenant-a");
            // Duplicate for clientB to confirm Distinct
            SeedTenantMapping(context, clientB, "tenant-a-extra");
            SeedTenantMapping(context, clientC, "tenant-a");
            SeedTenantMapping(context, clientB, "tenant-b");
            await context.SaveChangesAsync();

            var service = GetClientService(context);

            var result = await service.ListClientPrimaryKeysForTenantAsync("tenant-a", max: 10, CancellationToken.None);

            result.Should().BeEquivalentTo(new[] { clientA.Id, clientB.Id, clientC.Id }, options => options.WithStrictOrdering());

            var capped = await service.ListClientPrimaryKeysForTenantAsync("tenant-a", max: 2, CancellationToken.None);

            // Returns max + 1 to allow the caller to detect overflow per R8.4.
            capped.Should().HaveCount(3);
            capped.Take(2).Should().BeEquivalentTo(new[] { clientA.Id, clientB.Id }, options => options.WithStrictOrdering());
        }

        [Fact]
        public async Task Priority1_TrimsTenantKey_BeforeQuery()
        {
            using var context = GetDbContext();

            var clientA = SeedClient(context, "client-a");
            await context.SaveChangesAsync();

            SeedTenantMapping(context, clientA, "tenant-a");
            await context.SaveChangesAsync();

            var service = GetClientService(context);

            var result = await service.ListClientPrimaryKeysForTenantAsync("  tenant-a  ", max: 10, CancellationToken.None);

            result.Should().ContainSingle().Which.Should().Be(clientA.Id);
        }

        [Fact]
        public async Task Priority2_Property_Fallback_Used_When_Mapping_Empty()
        {
            using var context = GetDbContext();

            var clientWithProp = SeedClient(context, "client-with-prop");
            SeedTenantPairsProperty(context, clientWithProp, "tenant-legacy");

            var clientWithOtherProp = SeedClient(context, "client-with-other-prop");
            SeedTenantPairsProperty(context, clientWithOtherProp, "tenant-other");

            var clientNoMatch = SeedClient(context, "client-no-match");

            await context.SaveChangesAsync();

            var service = GetClientService(context);

            var result = await service.ListClientPrimaryKeysForTenantAsync("tenant-legacy", max: 10, CancellationToken.None);

            result.Should().ContainSingle().Which.Should().Be(clientWithProp.Id);
        }

        [Fact]
        public async Task Priority1_Wins_Over_Priority2_When_Both_Match()
        {
            using var context = GetDbContext();

            var clientMapping = SeedClient(context, "client-mapping");
            var clientLegacy = SeedClient(context, "client-legacy");
            SeedTenantPairsProperty(context, clientLegacy, "tenant-shared");
            await context.SaveChangesAsync();

            SeedTenantMapping(context, clientMapping, "tenant-shared");
            await context.SaveChangesAsync();

            var service = GetClientService(context);

            var result = await service.ListClientPrimaryKeysForTenantAsync("tenant-shared", max: 10, CancellationToken.None);

            // Priority 1 wins; legacy property fallback is ignored because mapping returned >= 1 row.
            result.Should().ContainSingle().Which.Should().Be(clientMapping.Id);
        }

        [Fact]
        public async Task Priority2_TenantKey_Match_Is_CaseInsensitive_And_Trimmed()
        {
            using var context = GetDbContext();

            var clientLegacy = SeedClient(context, "client-legacy");
            SeedTenantPairsProperty(context, clientLegacy, "Tenant-LEGACY");
            await context.SaveChangesAsync();

            var service = GetClientService(context);

            var result = await service.ListClientPrimaryKeysForTenantAsync("  tenant-legacy  ", max: 10, CancellationToken.None);

            result.Should().ContainSingle().Which.Should().Be(clientLegacy.Id);
        }

        [Fact]
        public async Task Priority2_Malformed_Json_Is_Skipped_Without_Throwing()
        {
            using var context = GetDbContext();

            var clientBadJson = SeedClient(context, "client-bad-json");
            clientBadJson.Properties ??= new List<ClientProperty>();
            clientBadJson.Properties.Add(new ClientProperty
            {
                Key = TenantRedirectPairsPropertyKey,
                Value = "{not-valid-json"
            });

            var clientGoodJson = SeedClient(context, "client-good-json");
            SeedTenantPairsProperty(context, clientGoodJson, "tenant-legacy");

            await context.SaveChangesAsync();

            var service = GetClientService(context);

            var result = await service.ListClientPrimaryKeysForTenantAsync("tenant-legacy", max: 10, CancellationToken.None);

            result.Should().ContainSingle().Which.Should().Be(clientGoodJson.Id);
        }

        [Fact]
        public async Task Both_Empty_Returns_EmptyList()
        {
            using var context = GetDbContext();

            SeedClient(context, "client-x");
            await context.SaveChangesAsync();

            var service = GetClientService(context);

            var result = await service.ListClientPrimaryKeysForTenantAsync("tenant-missing", max: 10, CancellationToken.None);

            result.Should().NotBeNull();
            result.Should().BeEmpty();
        }

        [Fact]
        public async Task Mapping_Has_Plus_One_Element_Caller_Detects_Overflow()
        {
            using var context = GetDbContext();

            for (var i = 0; i < 7; i++)
            {
                var c = SeedClient(context, $"client-{i:D2}");
                await context.SaveChangesAsync();
                SeedTenantMapping(context, c, "tenant-overflow");
            }
            await context.SaveChangesAsync();

            var service = GetClientService(context);

            const int max = 5;
            var result = await service.ListClientPrimaryKeysForTenantAsync("tenant-overflow", max, CancellationToken.None);

            // max + 1 returned so caller can detect overflow per R8.4.
            result.Should().HaveCount(max + 1);
            result.Should().BeInAscendingOrder();
        }

        [Fact]
        public async Task Priority2_Caps_Result_At_Max_Plus_One()
        {
            using var context = GetDbContext();

            for (var i = 0; i < 6; i++)
            {
                var c = SeedClient(context, $"legacy-{i:D2}");
                SeedTenantPairsProperty(context, c, "tenant-legacy");
            }
            await context.SaveChangesAsync();

            var service = GetClientService(context);

            const int max = 4;
            var result = await service.ListClientPrimaryKeysForTenantAsync("tenant-legacy", max, CancellationToken.None);

            result.Should().HaveCount(max + 1);
            result.Should().BeInAscendingOrder();
        }
    }
}
