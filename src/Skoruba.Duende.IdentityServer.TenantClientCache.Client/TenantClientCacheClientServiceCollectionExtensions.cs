// Feature: tenant-client-cache-public-read, Task 9
//
// DI wiring entry point for SDK consumers. Registers:
// - TenantClientCacheClientOptions with strict validation (R10.7, R10.8).
// - A named HttpClient "TenantClientCachePublicRead" (R10.6) configured
//   with BaseAddress, Timeout (R11.12), Accept and User-Agent headers
//   (R10.9).
// - IMemoryCache (R10.7) — TryAdd-style: callers may already have one.
// - The retry policy + metrics wrapper (singletons).
// - ITenantClientCacheClient → TenantClientCacheClient (singleton, R10.2,
//   R10.11 — no global static state).
//
// Validates: Requirements 10.2, 10.6, 10.7, 10.8, 10.9, 10.11, 11.12

#nullable enable

using System;
using System.Net.Http.Headers;
using System.Reflection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Skoruba.Duende.IdentityServer.TenantClientCache.Client.Internal;

namespace Skoruba.Duende.IdentityServer.TenantClientCache.Client;

/// <summary>
/// <see cref="IServiceCollection"/> extensions that register the
/// <see cref="ITenantClientCacheClient"/> SDK and its dependencies.
/// </summary>
public static class TenantClientCacheClientServiceCollectionExtensions
{
    /// <summary>
    /// Named <see cref="System.Net.Http.IHttpClientFactory"/> identifier used by the SDK (R10.6).
    /// </summary>
    public const string HttpClientName = "TenantClientCachePublicRead";

    /// <summary>
    /// Register the SDK consumer types.
    /// </summary>
    /// <param name="services">The service collection to mutate.</param>
    /// <param name="configure">
    /// Strongly-typed configuration callback. The supplied options are
    /// validated on host start (R10.8) and again on every option
    /// snapshot read.
    /// </param>
    public static IServiceCollection AddTenantClientCacheClient(
        this IServiceCollection services,
        Action<TenantClientCacheClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        // R10.7 + R10.8 — bind, validate, fail-fast on host start.
        services.AddOptions<TenantClientCacheClientOptions>()
            .Configure(configure)
            .Validate(static o =>
            {
                if (o.BaseAddress is null) return false;
                if (!o.BaseAddress.IsAbsoluteUri) return false;

                var isLocalhost = string.Equals(
                    o.BaseAddress.Host, "localhost", StringComparison.OrdinalIgnoreCase);
                if (o.BaseAddress.Scheme != Uri.UriSchemeHttps && !isLocalhost) return false;

                if (string.IsNullOrWhiteSpace(o.ApiKey)) return false;

                if (o.HttpTimeout < TimeSpan.FromSeconds(1)
                    || o.HttpTimeout > TimeSpan.FromSeconds(60)) return false;
                if (o.MaxRetryAttempts < 0 || o.MaxRetryAttempts > 5) return false;
                if (o.RetryBaseDelay < TimeSpan.FromMilliseconds(10)
                    || o.RetryBaseDelay > TimeSpan.FromSeconds(5)) return false;
                if (o.MaxClientCacheTtl < TimeSpan.Zero
                    || o.MaxClientCacheTtl > TimeSpan.FromHours(1)) return false;

                return true;
            },
            "TenantClientCacheClientOptions failed validation (R10.7, R10.8).")
            .ValidateOnStart();

        // R10.6 — named HttpClient. Resolved through IHttpClientFactory
        // by the SDK; consumers MUST NOT depend on the named instance
        // directly (encapsulation R10.11).
        services.AddHttpClient(HttpClientName, (sp, http) =>
        {
            var opts = sp.GetRequiredService<IOptions<TenantClientCacheClientOptions>>().Value;

            http.BaseAddress = opts.BaseAddress;     // R10.7
            http.Timeout = opts.HttpTimeout;          // R11.12
            http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            http.DefaultRequestHeaders.UserAgent.ParseAdd(BuildUserAgent());   // R10.9
        });

        // Idempotent registrations: TryAdd lets this extension be called
        // multiple times in composition roots that bolt the SDK onto an
        // existing host without disturbing prior registrations.
        services.AddMemoryCache();                                              // R10.7
        services.TryAddSingleton<TenantClientCacheClientMetrics>();             // R11.11
        services.TryAddSingleton<TenantClientCacheClientRetryPolicy>();
        services.TryAddSingleton<ITenantClientCacheClient, TenantClientCacheClient>(); // R10.2, R10.11

        return services;
    }

    private static string BuildUserAgent()
    {
        var asm = typeof(TenantClientCacheClientServiceCollectionExtensions).Assembly;
        var ver = asm.GetName().Version?.ToString()
                  ?? asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                  ?? "0.0.0";
        return $"Skoruba.Duende.IdentityServer.TenantClientCache.Client/{ver}";
    }
}
