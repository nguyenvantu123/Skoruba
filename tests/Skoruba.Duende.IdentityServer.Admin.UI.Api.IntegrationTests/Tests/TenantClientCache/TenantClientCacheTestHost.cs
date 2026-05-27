// Feature: tenant-client-cache-expansion, Task 10
//
// In-process WebApplicationFactory used by the tenant-client-cache E2E
// integration tests. Mirrors Tests/Base/TestFixture.cs (the existing
// fixture used by TenantsControllerTests) but mounts ClientsController
// instead of TenantsController. The reason for a sibling fixture rather
// than reusing the public-tenant fixture: ClientsController carries a
// completely different DI graph (IClientService, IClientScopeCacheService,
// ITenantClientCacheService, IClientTenantScopeResolver) and has the
// AdministrationPolicy + TestAuth header requirement.
//
// The fixture is parameterized via a builder so each test can:
//   * tweak the TenantClientCacheOptions (Enabled = false case);
//   * override the IDistributedCache registration to inject a
//     ThrowingDistributedCache for Redis-down scenarios;
//   * pre-seed the in-memory IClientService / ITenantRepository.
//
// The host is built with Microsoft.AspNetCore.TestHost so no actual TCP
// socket / SQL connection is needed — satisfies the AGENTS.md hard rule
// "integration tests run against the in-process WebApplicationFactory".

#nullable enable

using System;
using System.Collections.Generic;
using System.Net.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Services.Interfaces;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration.Constants;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Controllers;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.ExceptionHandling;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Common;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Resources;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;
using TenantInfrastructure.MasterDb;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Tests.TenantClientCache;

internal sealed class TenantClientCacheTestHost : IDisposable
{
    public IHost Host { get; }
    public TestServer TestServer { get; }
    public HttpClient Client { get; }

    public InMemoryClientStore ClientStore { get; }
    public InMemoryTenantRepository TenantRepository { get; }
    public IDistributedCache DistributedCache { get; }
    public ThrowingDistributedCache? ThrowingCache { get; }
    public CapturingLoggerProvider LoggerProvider { get; }
    public TenantClientCacheOptions BoundOptions { get; }

    private TenantClientCacheTestHost(
        IHost host,
        InMemoryClientStore clientStore,
        InMemoryTenantRepository tenantRepository,
        IDistributedCache distributedCache,
        ThrowingDistributedCache? throwingCache,
        CapturingLoggerProvider loggerProvider,
        TenantClientCacheOptions boundOptions)
    {
        Host = host;
        TestServer = host.GetTestServer();
        Client = host.GetTestClient();
        Client.DefaultRequestHeaders.Add(TestAuthenticationHandler.HeaderName, "admin");

        ClientStore = clientStore;
        TenantRepository = tenantRepository;
        DistributedCache = distributedCache;
        ThrowingCache = throwingCache;
        LoggerProvider = loggerProvider;
        BoundOptions = boundOptions;
    }

    public ITenantClientCacheService TenantClientCache =>
        Host.Services.GetRequiredService<ITenantClientCacheService>();

    public IClientScopeCacheService LegacyClientScopeCache =>
        Host.Services.GetRequiredService<IClientScopeCacheService>();

    public IClientTenantScopeResolver ScopeResolver =>
        Host.Services.GetRequiredService<IClientTenantScopeResolver>();

    public TenantClientCacheRefreshService BackgroundRefreshService =>
        Host.Services.GetRequiredService<TenantClientCacheRefreshService>();

    public void Dispose()
    {
        Client.Dispose();
        Host.Dispose();
    }

    public static TenantClientCacheTestHost Create(Action<Builder>? configure = null)
    {
        var builder = new Builder();
        configure?.Invoke(builder);
        return builder.Build();
    }

    internal sealed class Builder
    {
        public TenantClientCacheOptions Options { get; } = new()
        {
            Enabled = true,
            AbsoluteTtl = TimeSpan.FromHours(1),
            SlidingTtl = null,
            RefreshInterval = TimeSpan.FromHours(1),
            WriteTimeoutMs = 2000,
            MaxClientsPerTenant = 5000,
        };

        public bool UseThrowingCache { get; set; }

        /// <summary>Optional initial state for the in-memory IClientService.</summary>
        public Action<InMemoryClientStore>? SeedClients { get; set; }

        /// <summary>Optional initial state for the in-memory ITenantRepository.</summary>
        public Action<InMemoryTenantRepository>? SeedTenants { get; set; }

        public TenantClientCacheTestHost Build()
        {
            var clientStore = new InMemoryClientStore();
            SeedClients?.Invoke(clientStore);

            var tenantRepository = new InMemoryTenantRepository();
            SeedTenants?.Invoke(tenantRepository);

            var memoryCache = new MemoryDistributedCache(
                Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
            ThrowingDistributedCache? throwingCache = UseThrowingCache
                ? new ThrowingDistributedCache(memoryCache)
                : null;
            IDistributedCache cache = (IDistributedCache?)throwingCache ?? memoryCache;

            var loggerProvider = new CapturingLoggerProvider();

            var options = Options;

            var host = Microsoft.Extensions.Hosting.Host
                .CreateDefaultBuilder()
                .ConfigureAppConfiguration(cfg =>
                {
                    cfg.AddInMemoryCollection(new[]
                    {
                        new KeyValuePair<string, string?>(
                            $"{TenantClientCacheOptions.SectionName}:Enabled", options.Enabled ? "true" : "false"),
                        new KeyValuePair<string, string?>(
                            $"{TenantClientCacheOptions.SectionName}:AbsoluteTtl", options.AbsoluteTtl.ToString()),
                        new KeyValuePair<string, string?>(
                            $"{TenantClientCacheOptions.SectionName}:SlidingTtl",
                            options.SlidingTtl?.ToString() ?? string.Empty),
                        new KeyValuePair<string, string?>(
                            $"{TenantClientCacheOptions.SectionName}:RefreshInterval", options.RefreshInterval.ToString()),
                        new KeyValuePair<string, string?>(
                            $"{TenantClientCacheOptions.SectionName}:WriteTimeoutMs", options.WriteTimeoutMs.ToString()),
                        new KeyValuePair<string, string?>(
                            $"{TenantClientCacheOptions.SectionName}:MaxClientsPerTenant", options.MaxClientsPerTenant.ToString()),
                    });
                })
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.SetMinimumLevel(LogLevel.Debug);
                    logging.AddProvider(loggerProvider);
                })
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseTestServer();

                    webBuilder.ConfigureServices(services =>
                    {
                        // ----- Authentication / authorization (test-only) ------
                        services
                            .AddAuthentication(TestAuthenticationHandler.Scheme)
                            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                                TestAuthenticationHandler.Scheme, _ => { });

                        services.AddAuthorization(authOptions =>
                        {
                            authOptions.DefaultPolicy = new AuthorizationPolicyBuilder(TestAuthenticationHandler.Scheme)
                                .RequireAuthenticatedUser()
                                .Build();

                            authOptions.AddPolicy(AuthorizationConsts.AdministrationPolicy, policy =>
                            {
                                policy.AuthenticationSchemes.Add(TestAuthenticationHandler.Scheme);
                                policy.RequireAuthenticatedUser();
                            });
                        });

                        // ----- Distributed cache + legacy scope cache ----------
                        services.RemoveAll<IDistributedCache>();
                        services.AddSingleton<IDistributedCache>(_ => cache);
                        services.AddSingleton<IClientScopeCacheService, ClientScopeCacheService>();

                        // ----- Tenant-client cache feature ---------------------
                        services.Configure<TenantClientCacheOptions>(BindFromConfig);
                        services.AddSingleton<TenantClientCacheMetrics>();
                        services.AddSingleton<ITenantClientCacheService, TenantClientCacheService>();
                        services.AddScoped<IClientTenantScopeResolver, ClientTenantScopeResolver>();
                        // The BackgroundService is registered as a singleton (NOT
                        // a hosted service) so tests can drive `SweepAsync` on
                        // demand rather than wait for the periodic loop.
                        services.AddSingleton<TenantClientCacheRefreshService>();

                        // ----- In-memory test doubles --------------------------
                        services.AddSingleton<IClientService>(clientStore);
                        services.AddSingleton<ITenantRepository>(tenantRepository);
                        services.AddSingleton<IApiErrorResources, ApiErrorResources>();
                        services.AddScoped<ControllerExceptionFilterAttribute>();

                        // ----- MVC pipeline ------------------------------------
                        services
                            .AddControllers()
                            .AddApplicationPart(typeof(ClientsController).Assembly)
                            .AddJsonOptions(jsonOptions =>
                            {
                                jsonOptions.JsonSerializerOptions.PropertyNamingPolicy = null;
                            });
                    });

                    webBuilder.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseAuthentication();
                        app.UseAuthorization();
                        app.UseEndpoints(endpoints => endpoints.MapControllers());
                    });
                })
                .Start();

            void BindFromConfig(TenantClientCacheOptions o)
            {
                o.Enabled = options.Enabled;
                o.AbsoluteTtl = options.AbsoluteTtl;
                o.SlidingTtl = options.SlidingTtl;
                o.RefreshInterval = options.RefreshInterval;
                o.WriteTimeoutMs = options.WriteTimeoutMs;
                o.MaxClientsPerTenant = options.MaxClientsPerTenant;
            }

            return new TenantClientCacheTestHost(
                host,
                clientStore,
                tenantRepository,
                cache,
                throwingCache,
                loggerProvider,
                options);
        }
    }
}
