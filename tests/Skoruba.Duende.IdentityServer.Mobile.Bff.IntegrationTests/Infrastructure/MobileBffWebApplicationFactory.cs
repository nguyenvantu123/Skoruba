// In-process test host for the BFF. Replaces:
//   * JWT bearer auth with TestAuthenticationHandler (header-driven).
//   * ITenantClientCacheClient with FakeTenantClientCacheClient (stage-driven).
//
// Synthetic tenant prefix `test-tenant-` is required by AGENTS.md. Real
// tenantKey / clientId values must NEVER appear in tests.

using System.Collections.Concurrent;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using Skoruba.Duende.IdentityServer.TenantClientCache.Client;

namespace Skoruba.Duende.IdentityServer.Mobile.Bff.IntegrationTests.Infrastructure;

public sealed class MobileBffWebApplicationFactory : WebApplicationFactory<Program>
{
    public FakeTenantClientCacheClient FakeSdk { get; } = new();
    public CapturingLoggerProvider Logs { get; } = new();

    public void ResetCounters()
    {
        FakeSdk.ResetCounters();
        Logs.Entries.Clear();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            // Synthetic config; no real secrets. Validator requires:
            // Authority non-empty, BaseAddress absolute URI, ApiKey non-empty,
            // numeric ranges within [1,60] / [0,5] / [0,3600].
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MobileBff:Authentication:Authority"] = "https://localhost",
                ["MobileBff:Authentication:RequireHttpsMetadata"] = "false",
                ["MobileBff:Authentication:Audience"] = null,
                ["MobileBff:TenantClientCache:BaseAddress"] = "https://localhost",
                ["MobileBff:TenantClientCache:ApiKey"] = "test-api-key-PLACEHOLDER",
                ["MobileBff:TenantClientCache:HttpTimeoutSeconds"] = "5",
                ["MobileBff:TenantClientCache:MaxRetryAttempts"] = "0",
                ["MobileBff:TenantClientCache:MaxClientCacheTtlSeconds"] = "0"
            });
        });

        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(Logs);
            logging.SetMinimumLevel(LogLevel.Trace);
        });

        builder.ConfigureTestServices(services =>
        {
            // Override the SDK with a stage-driven fake. The real SDK is
            // singleton-scoped; remove and re-register so the test scope wins.
            services.RemoveAll<ITenantClientCacheClient>();
            services.AddSingleton<ITenantClientCacheClient>(FakeSdk);

            // Override authentication: swap JwtBearer for the test scheme.
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultScheme = TestAuthenticationDefaults.Scheme;
                options.DefaultAuthenticateScheme = TestAuthenticationDefaults.Scheme;
                options.DefaultChallengeScheme = TestAuthenticationDefaults.Scheme;
                options.DefaultForbidScheme = TestAuthenticationDefaults.Scheme;
                options.DefaultSignInScheme = TestAuthenticationDefaults.Scheme;
                options.DefaultSignOutScheme = TestAuthenticationDefaults.Scheme;
            });
            services.AddAuthentication(TestAuthenticationDefaults.Scheme)
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationDefaults.Scheme,
                    _ => { });

            // Drop the JwtBearer post-configure callbacks that touch the
            // (test-only) Authority URL — we don't want metadata fetches.
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Authority = string.Empty;
                options.MetadataAddress = null;
                options.RequireHttpsMetadata = false;
            });
        });
    }
}

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<CapturedLogEntry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);

    public void Dispose() { }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<CapturedLogEntry> _sink;

        public CapturingLogger(string category, ConcurrentQueue<CapturedLogEntry> sink)
        {
            _category = category;
            _sink = sink;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            var rendered = formatter(state, exception);
            var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (state is IReadOnlyList<KeyValuePair<string, object?>> kvs)
            {
                foreach (var kv in kvs)
                {
                    fields[kv.Key] = kv.Value?.ToString();
                }
            }
            _sink.Enqueue(new CapturedLogEntry(_category, logLevel, rendered, fields, exception));
        }
    }
}

public sealed record CapturedLogEntry(
    string Category,
    LogLevel Level,
    string RenderedMessage,
    IReadOnlyDictionary<string, string?> Fields,
    Exception? Exception);
