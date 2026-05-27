// Feature: tenant-client-cache-public-read, Task 10
//
// In-process WebApplicationFactory-style host for the public-read endpoint
// (tenant-client-cache-public-read). Mounts only PublicTenantClientsController
// and the public-read pipeline DI extension AddTenantClientCachePublicRead.
//
// Goals:
//   * Drive every terminal outcome (200, 304, 400, 401, 404, 405, 429, 503,
//     CORS preflight) without live Redis or a real tenant database.
//   * Replace ITenantClientCacheService with FakeTenantClientCacheService so
//     tests can stage canned envelopes / exceptions per (tenantKey, clientId).
//   * Capture structured log entries via CapturingLoggerProvider so audit-
//     event assertions can run without a Serilog sink.
//   * Configure overlay so different tests can pin RateLimit:TokenLimit,
//     CORS allowlist, ApiKeys and Audit options.
//
// Validates: Requirements 1.6, 2.9, 3.1, 3.2, 3.3, 3.5, 3.8, 4.5, 4.7,
//            5.1, 5.2, 5.4, 6.x, 7.x, 9.7, 12.9, 12.10

#nullable enable

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Skoruba.Duende.IdentityServer.Admin.UI.Api.Controllers;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Helpers;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Common;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.PublicTenantClients;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Tests.PublicTenantClients.Helpers;

/// <summary>
/// Integration test host scoped to the public-read endpoint. The host
/// composes the production DI extension verbatim, then overrides
/// <see cref="ITenantClientCacheService"/> with
/// <see cref="FakeTenantClientCacheService"/> so the tests do not exercise
/// the production cache writer or Redis.
/// </summary>
internal sealed class PublicTenantClientsTestHost : IDisposable
{
    /// <summary>
    /// Builder that lets each test overlay <c>TenantClientCachePublicRead:*</c>
    /// configuration and toggle the simulated remote-IP loopback bypass
    /// (R9.7 plain-HTTP gate).
    /// </summary>
    public sealed class Builder
    {
        public Dictionary<string, string?> ConfigOverrides { get; }
            = new(StringComparer.Ordinal);

        public Action<FakeTenantClientCacheService>? ConfigureFake { get; set; }

        /// <summary>
        /// Override the request scheme reported to the framework (defaults
        /// to <c>https</c> so the HttpsRequiredFilter does not short-circuit).
        /// Set to <c>http</c> to drive the R9.7 plain-HTTP path; combine with
        /// <see cref="ForceNonLoopbackRemoteIp"/> to actually trip the filter
        /// (otherwise the loopback bypass kicks in).
        /// </summary>
        public string Scheme { get; set; } = "https";

        /// <summary>
        /// Force the connection's remote IP to a non-loopback address so
        /// HttpsRequiredFilter (R9.7) and IpHashHelper (R9.6) see a value.
        /// </summary>
        public bool ForceNonLoopbackRemoteIp { get; set; }

        /// <summary>
        /// Override <c>HostString.Host</c> reported to the framework. Use a
        /// non-localhost host name combined with <see cref="Scheme"/>=http
        /// to exercise R9.7 (plain-HTTP gate over a non-loopback host).
        /// </summary>
        public string HostName { get; set; } = "api.example.com";

        public Builder WithApiKey(string tenantKey, string sha256HexLower)
        {
            ConfigOverrides[$"TenantClientCachePublicRead:ApiKeys:{tenantKey}"] = sha256HexLower;
            return this;
        }

        public Builder WithRateLimit(int tokenLimit, int tokensPerPeriod, TimeSpan replenishmentPeriod, bool autoReplenishment = false)
        {
            ConfigOverrides["TenantClientCachePublicRead:RateLimit:TokenLimit"] =
                tokenLimit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ConfigOverrides["TenantClientCachePublicRead:RateLimit:TokensPerPeriod"] =
                tokensPerPeriod.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ConfigOverrides["TenantClientCachePublicRead:RateLimit:ReplenishmentPeriod"] =
                replenishmentPeriod.ToString();
            ConfigOverrides["TenantClientCachePublicRead:RateLimit:QueueLimit"] = "0";
            ConfigOverrides["TenantClientCachePublicRead:RateLimit:AutoReplenishment"] =
                autoReplenishment ? "true" : "false";
            return this;
        }

        public Builder WithCorsOrigin(string origin)
        {
            // Indexes are 0..N — overlay "TenantClientCachePublicRead:Cors:AllowedOrigins:0=...".
            var existingCount = 0;
            foreach (var key in ConfigOverrides.Keys)
            {
                if (key.StartsWith("TenantClientCachePublicRead:Cors:AllowedOrigins:", StringComparison.Ordinal))
                {
                    existingCount++;
                }
            }
            ConfigOverrides[$"TenantClientCachePublicRead:Cors:AllowedOrigins:{existingCount}"] = origin;
            return this;
        }

        public Builder WithResponseCacheMaxAge(int maxAgeSeconds)
        {
            ConfigOverrides["TenantClientCachePublicRead:ResponseCache:MaxAgeSeconds"] =
                maxAgeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return this;
        }

        public Builder WithRemoteIpSalt(string salt, bool logIpHash = true)
        {
            ConfigOverrides["TenantClientCachePublicRead:Audit:RemoteIpSalt"] = salt;
            ConfigOverrides["TenantClientCachePublicRead:Audit:LogIpHash"] =
                logIpHash ? "true" : "false";
            return this;
        }

        public PublicTenantClientsTestHost Build() => new(this);
    }

    public IHost Host { get; }
    public TestServer TestServer { get; }
    public HttpClient Client { get; }
    public FakeTenantClientCacheService FakeCache { get; }
    public CapturingLoggerProvider Logger { get; }
    public IConfigurationRoot ConfigurationRoot { get; }
    public Builder Settings { get; }

    private PublicTenantClientsTestHost(Builder settings)
    {
        Settings = settings;
        var fake = new FakeTenantClientCacheService();
        settings.ConfigureFake?.Invoke(fake);
        FakeCache = fake;

        var loggerProvider = new CapturingLoggerProvider();
        Logger = loggerProvider;

        // Build a defaults overlay first so callers do not have to
        // re-state the standard rate-limit / CORS settings every test.
        var defaults = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["TenantClientCachePublicRead:RateLimit:TokenLimit"] = "30",
            ["TenantClientCachePublicRead:RateLimit:TokensPerPeriod"] = "30",
            ["TenantClientCachePublicRead:RateLimit:ReplenishmentPeriod"] = "00:01:00",
            ["TenantClientCachePublicRead:RateLimit:QueueLimit"] = "0",
            ["TenantClientCachePublicRead:RateLimit:AutoReplenishment"] = "false",
            ["TenantClientCachePublicRead:Cors:PreflightMaxAgeSeconds"] = "600",
            ["TenantClientCachePublicRead:ResponseCache:MaxAgeSeconds"] = "60",
            ["TenantClientCachePublicRead:Audit:LogIpHash"] = "true",
            // RemoteIpSalt non-empty is required by the validator only in
            // Production. The integration host sets EnvironmentName=
            // "Development" so the empty default is accepted.
            ["TenantClientCachePublicRead:Audit:RemoteIpSalt"] = string.Empty,
        };
        foreach (var kv in settings.ConfigOverrides)
        {
            defaults[kv.Key] = kv.Value;
        }

        var configBuilder = new ConfigurationBuilder().AddInMemoryCollection(defaults);
        ConfigurationRoot = configBuilder.Build();

        Host = Microsoft.Extensions.Hosting.Host
            .CreateDefaultBuilder()
            .UseEnvironment(Environments.Development)
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Debug);
                logging.AddProvider(loggerProvider);
            })
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseTestServer();

                webBuilder.ConfigureAppConfiguration(cfg =>
                {
                    cfg.AddConfiguration(ConfigurationRoot);
                });

                webBuilder.ConfigureServices(services =>
                {
                    // Authentication is required for the framework to wire
                    // [AllowAnonymous] correctly even though every public-
                    // read request is anonymous.
                    services
                        .AddAuthentication(TestAuthenticationHandler.Scheme)
                        .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                            TestAuthenticationHandler.Scheme, _ => { });
                    services.AddAuthorization();

                    // Public-read DI extension under test (Task 6).
                    services.AddTenantClientCachePublicRead(ConfigurationRoot);

                    // Replace ITenantClientCacheService with the test fake.
                    // Use TryAddSingleton + RemoveAll combo so we do not
                    // depend on whether the production registration ran or
                    // not (the public-read extension does not register the
                    // cache service itself; the parent spec does).
                    services.RemoveAll<ITenantClientCacheService>();
                    services.AddSingleton<ITenantClientCacheService>(fake);

                    // The metrics class is a singleton; the public-read
                    // extension already registers it via TryAdd, but we
                    // ensure a fresh instance per host so RecordingMeterListener
                    // captures only this host's increments.
                    services.RemoveAll<TenantClientCacheMetrics>();
                    services.AddSingleton<TenantClientCacheMetrics>();

                    // Mount the controller via the application part so
                    // we do not transitively pull in the rest of the
                    // Admin_Api_Host controllers.
                    services
                        .AddControllers(mvc =>
                        {
                            mvc.ReturnHttpNotAcceptable = false;
                            mvc.RespectBrowserAcceptHeader = false;
                        })
                        .AddJsonOptions(_ => { /* default options */ })
                        .ConfigureApplicationPartManager(manager =>
                        {
                            manager.ApplicationParts.Clear();
                            manager.ApplicationParts.Add(new TestApplicationPart(typeof(PublicTenantClientsController)));
                        });

                    // Register an additional output formatter that lists
                    // the explicit "application/json; charset=utf-8" media
                    // type the production filters / controller helpers use
                    // when setting ObjectResult.ContentTypes. The framework's
                    // formatter selector picks this when ContentTypes
                    // includes the charset parameter — without it, formatter
                    // selection would return 406 because the default
                    // SystemTextJsonOutputFormatter declares only
                    // "application/json" (no parameters) and the selection
                    // requires the produced content type to be a subset of
                    // a formatter-supported type. Production hosts hit the
                    // same path successfully because their MVC composition
                    // registers similar fallback formatters via Newtonsoft
                    // wiring or the [ApiController] convention; we mirror
                    // that here so the tests do not depend on Admin_Api_Host
                    // composition order.
                    services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(opts =>
                    {
                        var json = opts.OutputFormatters
                            .OfType<Microsoft.AspNetCore.Mvc.Formatters.SystemTextJsonOutputFormatter>()
                            .FirstOrDefault();
                        if (json is not null
                            && !json.SupportedMediaTypes.Contains("application/json; charset=utf-8"))
                        {
                            json.SupportedMediaTypes.Add("application/json; charset=utf-8");
                        }
                    });

                    // Expose Forwarded-IP simulation by overriding the
                    // remote address on every request via a startup
                    // middleware (see webBuilder.Configure below).
                });

                webBuilder.Configure(app =>
                {
                    if (settings.ForceNonLoopbackRemoteIp)
                    {
                        // Stamp the connection metadata before any other
                        // middleware runs so HttpsRequiredFilter sees the
                        // non-loopback IP and IpHashHelper hashes a stable
                        // value.
                        app.Use(async (context, next) =>
                        {
                            context.Connection.RemoteIpAddress =
                                IPAddress.Parse("198.51.100.42");
                            context.Request.Scheme = settings.Scheme;
                            context.Request.Host = new HostString(settings.HostName);
                            await next();
                        });
                    }
                    else if (!string.Equals(settings.Scheme, "https", StringComparison.OrdinalIgnoreCase)
                             || !string.Equals(settings.HostName, "api.example.com", StringComparison.OrdinalIgnoreCase))
                    {
                        app.Use(async (context, next) =>
                        {
                            context.Request.Scheme = settings.Scheme;
                            context.Request.Host = new HostString(settings.HostName);
                            await next();
                        });
                    }

                    app.UseRouting();
                    app.UseCors(StartupHelpers.PublicReadCorsPolicyName);
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseRateLimiter();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .Start();

        TestServer = Host.GetTestServer();
        Client = Host.GetTestClient();
        // Default Accept header — without it, the framework's content-
        // negotiation step may produce 406 NotAcceptable when filter
        // results emit "application/json; charset=utf-8" against an
        // Accept of "application/json" (parameters mismatch). Sending
        // */* matches both the production charset-bearing media type
        // and the formatter's plain "application/json" which keeps
        // the test pipeline production-representative.
        Client.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("*/*"));
        // The TestServer adds a default Host header of "localhost" — we
        // override that on individual requests when needed via
        // HttpRequestMessage so HttpsRequiredFilter sees api.example.com.
    }

    /// <summary>Build an HttpRequestMessage with the synthetic Host header.</summary>
    public HttpRequestMessage Request(HttpMethod method, string relativeUrl)
        => new(method, new Uri(Client.BaseAddress!, relativeUrl))
        {
            // The TestServer respects the relative URL only — Client.BaseAddress
            // already points to the in-process host. We let HttpClient set the
            // Host header from BaseAddress (default: localhost) and override
            // the request scheme via the synthetic middleware above when
            // HostName is non-localhost.
        };

    public void Dispose()
    {
        Client.Dispose();
        Host.Dispose();
    }

    /// <summary>
    /// Embedded application part that loads a single controller assembly
    /// instead of the full controllers list so the test host pipeline is
    /// constrained to PublicTenantClientsController.
    /// </summary>
    private sealed class TestApplicationPart : ApplicationPart, IApplicationPartTypeProvider
    {
        private readonly Assembly _assembly;
        private readonly Type[] _types;

        public TestApplicationPart(Type controllerType)
        {
            _assembly = controllerType.Assembly;
            _types = new[] { controllerType };
        }

        public override string Name => _assembly.GetName().Name ?? "PublicTenantClients";

        public IEnumerable<TypeInfo> Types
        {
            get
            {
                foreach (var t in _types)
                {
                    yield return t.GetTypeInfo();
                }
            }
        }
    }
}
