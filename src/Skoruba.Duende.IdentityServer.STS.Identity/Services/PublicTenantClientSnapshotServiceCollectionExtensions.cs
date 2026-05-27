// Feature: tenant-client-cache-public-read, Task 11/12 — STS.Identity consumer wrapper
//
// DI extension that wires the host-side facade around the TenantClientCache SDK.
// FAIL-CLOSED default: when Enabled=false the SDK is NOT registered and a no-op
// provider is bound. When Enabled=true the SDK is wired and the production
// fail-fast validator (BaseAddress / ApiKey / range checks) propagates as is.
//
// Idempotent: TryAdd-style registrations let the extension be called more than
// once in composite startup paths without duplicating singletons.

#nullable enable

using System;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Skoruba.Duende.IdentityServer.TenantClientCache.Client;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Services;

/// <summary>
/// Registration entry points for the public-read consumer wrapper used by
/// STS.Identity downstream services.
/// </summary>
public static class PublicTenantClientSnapshotServiceCollectionExtensions
{
    /// <summary>
    /// Wire <see cref="IPublicTenantClientSnapshotProvider"/>. The wrapper is
    /// driven by the <c>PublicTenantClientSnapshotConsumer</c> configuration
    /// section. When <c>Enabled=false</c> (the shipping default) the SDK is
    /// NOT touched and a no-op provider is registered, so missing credentials
    /// in dev / test / freshly-bootstrapped production hosts do NOT crash the
    /// app. When <c>Enabled=true</c> the SDK is wired and the SDK validator
    /// fail-fasts on invalid options.
    /// </summary>
    public static IServiceCollection AddPublicTenantClientSnapshotConsumer(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(PublicTenantClientSnapshotConsumerConfiguration.SectionName);
        services.Configure<PublicTenantClientSnapshotConsumerConfiguration>(section);

        var consumerOptions =
            section.Get<PublicTenantClientSnapshotConsumerConfiguration>()
            ?? new PublicTenantClientSnapshotConsumerConfiguration();

        if (!consumerOptions.Enabled)
        {
            // FAIL-CLOSED default: register the no-op provider. The SDK is NOT
            // wired, so missing BaseAddress / ApiKey do NOT cause the host to
            // refuse to start.
            services.TryAddSingleton<IPublicTenantClientSnapshotProvider, DisabledPublicTenantClientSnapshotProvider>();
            return services;
        }

        // Enabled path: wire the SDK. The SDK validator's ValidateOnStart hook
        // throws OptionsValidationException on invalid options at host startup,
        // which is the desired production fail-fast.
        services.AddTenantClientCacheClient(o =>
        {
            o.BaseAddress = TryParseAbsoluteUri(consumerOptions.BaseAddress);
            o.ApiKey = consumerOptions.ApiKey ?? string.Empty;
            o.HttpTimeout = TimeSpan.FromSeconds(consumerOptions.HttpTimeoutSeconds);
            o.MaxRetryAttempts = consumerOptions.MaxRetryAttempts;
            o.RetryBaseDelay = TimeSpan.FromMilliseconds(consumerOptions.RetryBaseDelayMilliseconds);
            o.MaxClientCacheTtl = TimeSpan.FromSeconds(consumerOptions.MaxClientCacheTtlSeconds);
            o.EnableInMemoryCaching = consumerOptions.EnableInMemoryCaching;
        });

        services.TryAddSingleton<IPublicTenantClientSnapshotProvider, PublicTenantClientSnapshotProvider>();
        return services;
    }

    /// <summary>
    /// Best-effort parse so that an empty / malformed value reaches the SDK
    /// validator (which produces a clear, structured error message) rather
    /// than a <see cref="UriFormatException"/> from inside the configure
    /// callback.
    /// </summary>
    private static Uri? TryParseAbsoluteUri(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return Uri.TryCreate(raw, UriKind.Absolute, out var uri) ? uri : null;
    }
}
