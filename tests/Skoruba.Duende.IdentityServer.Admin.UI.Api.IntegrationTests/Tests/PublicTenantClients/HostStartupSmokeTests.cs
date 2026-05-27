// Feature: tenant-client-cache-public-read, Task 11
//
// Startup smoke tests for the host-side wiring of the public-read
// endpoint. These tests boot a minimal in-process host with the
// production AddTenantClientCachePublicRead extension (mirroring the
// caller in Admin.Api/Startup.cs) and assert that:
//
//   * Empty TenantClientCachePublicRead:ApiKeys is fail-open at host
//     start (R1.7 — host comes up; the endpoint will return 401 to
//     every caller, but startup itself is not blocked).
//   * Each fail-fast validator branch — R1.4 (malformed key hash),
//     R4.3 (TokenLimit out-of-range), R5.6 (non-https / non-localhost
//     CORS origin) — propagates an OptionsValidationException through
//     IHost.StartAsync so the host exits with a configuration error.
//   * The startup logger emits exactly one Information entry summarising
//     the bound options, with no plaintext / hash leakage (R1.8).
//
// Validates: Requirements 1.4, 1.7, 1.8, 4.3, 5.6, 17.1

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Helpers;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Common;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Tests.PublicTenantClients.Helpers;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.PublicTenantClients;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Tests.PublicTenantClients;

public sealed class HostStartupSmokeTests
{
    /// <summary>
    /// Build an <see cref="IHost"/> that mirrors the production caller
    /// site in <c>Skoruba.Duende.IdentityServer.Admin.Api/Startup.cs</c>:
    /// register the parent-spec metrics meter (<see
    /// cref="TenantClientCacheMetrics"/>) so the public-read pipeline can
    /// resolve it, then invoke
    /// <see cref="StartupHelpers.AddTenantClientCachePublicRead"/> which
    /// arms <c>ValidateOnStart()</c> + registers the
    /// <see cref="PublicReadStartupLogger"/> hosted service.
    /// </summary>
    private static IHost BuildHost(
        Dictionary<string, string?> configOverrides,
        CapturingLoggerProvider? loggerProvider = null,
        string? environmentName = null)
    {
        environmentName ??= Environments.Development;
        var defaults = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["TenantClientCachePublicRead:RateLimit:TokenLimit"] = "30",
            ["TenantClientCachePublicRead:RateLimit:TokensPerPeriod"] = "30",
            ["TenantClientCachePublicRead:RateLimit:ReplenishmentPeriod"] = "00:01:00",
            ["TenantClientCachePublicRead:RateLimit:QueueLimit"] = "0",
            ["TenantClientCachePublicRead:RateLimit:AutoReplenishment"] = "true",
            ["TenantClientCachePublicRead:Cors:PreflightMaxAgeSeconds"] = "600",
            ["TenantClientCachePublicRead:ResponseCache:MaxAgeSeconds"] = "60",
            ["TenantClientCachePublicRead:Audit:LogIpHash"] = "true",
            ["TenantClientCachePublicRead:Audit:RemoteIpSalt"] = string.Empty,
        };

        foreach (var kv in configOverrides)
        {
            defaults[kv.Key] = kv.Value;
        }

        var builder = Microsoft.Extensions.Hosting.Host
            .CreateDefaultBuilder()
            .UseEnvironment(environmentName)
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Debug);
                if (loggerProvider is not null)
                {
                    logging.AddProvider(loggerProvider);
                }
            })
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureAppConfiguration(cfg =>
                {
                    cfg.AddInMemoryCollection(defaults);
                });
                webBuilder.ConfigureServices((ctx, services) =>
                {
                    // Mirror the parent-spec metrics meter registration —
                    // RegisterTenantClientCache is the production owner; the
                    // public-read filters depend on it. We register a fresh
                    // singleton so RecordingMeterListener observes only this
                    // host's increments.
                    services.AddSingleton<TenantClientCacheMetrics>();

                    // Production caller — Admin.Api/Startup.cs invokes this
                    // exactly once per host. The smoke harness re-uses the
                    // same extension verbatim so the test exercises the same
                    // ValidateOnStart() + IHostedService registration paths.
                    services.AddTenantClientCachePublicRead(ctx.Configuration);
                });
                webBuilder.Configure(app =>
                {
                    // Empty pipeline — we only test host startup, not
                    // request handling.
                });
            });

        return builder.Build();
    }

    // ===== R1.7 fail-open =====================================================

    [Fact]
    public async Task Host_Starts_Successfully_With_Empty_ApiKeys_Default_Config()
    {
        // R1.7: an empty ApiKeys dictionary MUST NOT block host startup.
        // The endpoint will reject every request with 401, but the host
        // itself comes up cleanly (defaults are valid, ValidateOnStart
        // does not trip).
        using var host = BuildHost(new Dictionary<string, string?>());

        var startTask = host.StartAsync(CancellationToken.None);
        await startTask;
        startTask.IsCompletedSuccessfully.Should().BeTrue();

        // Sanity — the bound options resolve cleanly post-start.
        var snapshot = host.Services
            .GetRequiredService<IOptions<TenantClientCachePublicReadOptions>>().Value;
        snapshot.ApiKeys.Should().BeEmpty();

        await host.StopAsync(CancellationToken.None);
    }

    // ===== R1.4 / R4.3 / R5.6 fail-fast =======================================

    [Fact]
    public async Task Host_Fails_Fast_When_ApiKey_Hash_Malformed()
    {
        // R1.4: a non-64-char hex value in ApiKeys MUST trip
        // ValidateOnStart and propagate as OptionsValidationException
        // through StartAsync.
        using var host = BuildHost(new Dictionary<string, string?>
        {
            ["TenantClientCachePublicRead:ApiKeys:acme"] = "not-a-valid-sha256-hex",
        });

        Func<Task> act = () => host.StartAsync(CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<OptionsValidationException>()).Subject.First();
        ex.Failures.Should().Contain(f => f.Contains("ApiKeys[acme]", StringComparison.Ordinal));
        // Defensive: the offending hash value MUST NOT be echoed in the
        // failure message (R1.4 redaction guarantee).
        ex.Failures.Should().NotContain(f => f.Contains("not-a-valid-sha256-hex", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Host_Fails_Fast_When_RateLimit_TokenLimit_OutOfRange()
    {
        // R4.3: TokenLimit ∈ [1, 10000]. 0 trips the validator.
        using var host = BuildHost(new Dictionary<string, string?>
        {
            ["TenantClientCachePublicRead:RateLimit:TokenLimit"] = "0",
        });

        Func<Task> act = () => host.StartAsync(CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<OptionsValidationException>()).Subject.First();
        ex.Message.Should().Contain("TokenLimit", "the failure message must name the offending key (R4.3)");
    }

    [Fact]
    public async Task Host_Fails_Fast_When_Cors_Origin_NonHttps_NonLocalhost()
    {
        // R5.6: CORS origin must be an absolute URL with scheme=https,
        // or scheme=http only when host=localhost. http://example.com
        // fails both conditions.
        using var host = BuildHost(new Dictionary<string, string?>
        {
            ["TenantClientCachePublicRead:Cors:AllowedOrigins:0"] = "http://example.com",
        });

        Func<Task> act = () => host.StartAsync(CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<OptionsValidationException>()).Subject.First();
        ex.Message.Should().Contain("http://example.com", "the failure message must name the offending origin (R5.6)");
    }

    // ===== R1.8 single-shot Information log entry =============================

    [Fact]
    public async Task Host_Logs_Single_Information_Entry_With_Bound_Options_On_Startup()
    {
        // R1.8: emit ONE Information-level entry on start with tenant
        // count + RateLimit / Cors / ResponseCache values. The entry
        // MUST NOT contain the plaintext API key, the SHA-256 hex hash,
        // or the RemoteIpSalt value.
        var validHash = TestApiKeys.ValidHashAcme;
        var loggerProvider = new CapturingLoggerProvider();

        using var host = BuildHost(
            new Dictionary<string, string?>
            {
                ["TenantClientCachePublicRead:ApiKeys:acme"] = validHash,
                ["TenantClientCachePublicRead:RateLimit:TokenLimit"] = "42",
                ["TenantClientCachePublicRead:Cors:AllowedOrigins:0"] = "https://app.example.com",
                ["TenantClientCachePublicRead:ResponseCache:MaxAgeSeconds"] = "75",
                ["TenantClientCachePublicRead:Audit:RemoteIpSalt"] = "salt-do-not-log",
            },
            loggerProvider);

        await host.StartAsync(CancellationToken.None);

        var startupEntries = loggerProvider.Entries
            .Where(e => e.Category == typeof(PublicReadStartupLogger).FullName)
            .Where(e => e.Level == LogLevel.Information)
            .ToArray();

        // Exactly one entry — never duplicated by re-registration.
        startupEntries.Should().HaveCount(1, "R1.8 — the startup logger emits one Information entry");
        var entry = startupEntries.Single();

        // Structured fields preserved by CapturingLoggerProvider.
        entry.Fields["EventType"].Should().Be(PublicReadStartupLogger.EventType);
        entry.Fields["TenantCount"].Should().Be(1);
        entry.Fields["RateLimitTokenLimit"].Should().Be(42);
        entry.Fields["CorsAllowedOriginCount"].Should().Be(1);
        entry.Fields["CorsPreflightMaxAgeSeconds"].Should().Be(600);
        entry.Fields["ResponseCacheMaxAgeSeconds"].Should().Be(75);
        entry.Fields["AuditLogIpHash"].Should().Be(true);

        // Redaction guard — neither the plaintext key, hash hex, nor
        // RemoteIpSalt may appear in the rendered message OR any
        // structured field value.
        entry.Message.Should().NotContain(validHash);
        entry.Message.Should().NotContain(TestApiKeys.ValidPlaintext);
        entry.Message.Should().NotContain("salt-do-not-log");

        foreach (var kv in entry.Fields)
        {
            var rendered = kv.Value?.ToString() ?? string.Empty;
            rendered.Should().NotContain(validHash, "API key hash MUST NOT leak through structured field {0}", kv.Key);
            rendered.Should().NotContain(TestApiKeys.ValidPlaintext, "API key plaintext MUST NOT leak through structured field {0}", kv.Key);
            rendered.Should().NotContain("salt-do-not-log", "RemoteIpSalt MUST NOT leak through structured field {0}", kv.Key);
        }

        await host.StopAsync(CancellationToken.None);
    }
}
