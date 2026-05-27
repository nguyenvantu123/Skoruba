// Feature: tenant-client-cache-public-read, Task 9
//
// DI-registration tests for AddTenantClientCacheClient.
// Asserts:
//   - Strict options validation (R10.7, R10.8): BaseAddress, ApiKey,
//     HttpTimeout, MaxRetryAttempts, RetryBaseDelay, MaxClientCacheTtl.
//   - Named HttpClient "TenantClientCachePublicRead" wired with the
//     configured BaseAddress, Timeout, Accept and User-Agent headers.
//   - IMemoryCache, retry policy, metrics and ITenantClientCacheClient
//     all resolve from DI.
//   - Idempotent registration (TryAdd-style) so the extension can be
//     called multiple times without duplicate singleton instances.
//
// Validates: Requirements 10.6, 10.7, 10.8, 10.9, 10.11, 11.12

#nullable enable

using System;
using System.Net.Http;

using FluentAssertions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Skoruba.Duende.IdentityServer.TenantClientCache.Client.Internal;

using Xunit;

namespace Skoruba.Duende.IdentityServer.TenantClientCache.Client.UnitTests;

public sealed class TenantClientCacheClientServiceCollectionExtensionsTests
{
    [Fact]
    public void AddTenantClientCacheClient_Resolves_Public_And_Internal_Types()
    {
        using var provider = BuildValid();

        provider.GetRequiredService<ITenantClientCacheClient>().Should().NotBeNull();
        provider.GetRequiredService<IMemoryCache>().Should().NotBeNull();
        provider.GetRequiredService<TenantClientCacheClientMetrics>().Should().NotBeNull();
        provider.GetRequiredService<TenantClientCacheClientRetryPolicy>().Should().NotBeNull();
        provider.GetRequiredService<IHttpClientFactory>().Should().NotBeNull();
    }

    [Fact]
    public void Named_HttpClient_Has_Configured_BaseAddress_And_Timeout()
    {
        using var provider = BuildValid();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        using var http = factory.CreateClient(
            TenantClientCacheClientServiceCollectionExtensions.HttpClientName);

        http.BaseAddress.Should().Be(new Uri("https://identity.example.com/"));
        http.Timeout.Should().Be(TimeSpan.FromSeconds(7));
        http.DefaultRequestHeaders.Accept.Should()
            .Contain(h => h.MediaType == "application/json");
        http.DefaultRequestHeaders.UserAgent.ToString()
            .Should().StartWith("Skoruba.Duende.IdentityServer.TenantClientCache.Client/");
    }

    [Fact]
    public void Idempotent_Registration_Does_Not_Duplicate_Singletons()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddTenantClientCacheClient(o =>
        {
            o.BaseAddress = new Uri("https://identity.example.com");
            o.ApiKey = "test";
        });
        services.AddTenantClientCacheClient(o =>
        {
            o.BaseAddress = new Uri("https://identity.example.com");
            o.ApiKey = "test";
        });

        using var provider = services.BuildServiceProvider();
        var first = provider.GetRequiredService<ITenantClientCacheClient>();
        var second = provider.GetRequiredService<ITenantClientCacheClient>();
        first.Should().BeSameAs(second);

        provider.GetRequiredService<TenantClientCacheClientMetrics>().Should()
            .BeSameAs(provider.GetRequiredService<TenantClientCacheClientMetrics>());
        provider.GetRequiredService<TenantClientCacheClientRetryPolicy>().Should()
            .BeSameAs(provider.GetRequiredService<TenantClientCacheClientRetryPolicy>());
    }

    // ===== Validation: BaseAddress =================================

    [Fact]
    public void Validation_Fails_When_BaseAddress_Null()
    {
        var act = () => Validate(o => { o.BaseAddress = null; o.ApiKey = "x"; });
        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void Validation_Fails_When_BaseAddress_Not_Absolute()
    {
        var act = () => Validate(o =>
        {
            o.BaseAddress = new Uri("/relative", UriKind.Relative);
            o.ApiKey = "x";
        });
        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void Validation_Fails_When_BaseAddress_Http_Non_Localhost()
    {
        var act = () => Validate(o =>
        {
            o.BaseAddress = new Uri("http://identity.example.com");
            o.ApiKey = "x";
        });
        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void Validation_Allows_Http_Localhost_For_Dev()
    {
        var act = () => Validate(o =>
        {
            o.BaseAddress = new Uri("http://localhost:5000");
            o.ApiKey = "x";
        });
        act.Should().NotThrow();
    }

    // ===== Validation: ApiKey ======================================

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validation_Fails_When_ApiKey_Empty_Or_Whitespace(string apiKey)
    {
        var act = () => Validate(o =>
        {
            o.BaseAddress = new Uri("https://identity.example.com");
            o.ApiKey = apiKey;
        });
        act.Should().Throw<OptionsValidationException>();
    }

    // ===== Validation: HttpTimeout ==================================

    [Theory]
    [InlineData(0)]
    [InlineData(900)]    // 0.9s — below 1s minimum
    [InlineData(60_001)] // above 60s maximum
    public void Validation_Fails_When_HttpTimeout_Out_Of_Range(int milliseconds)
    {
        var act = () => Validate(o =>
        {
            o.BaseAddress = new Uri("https://identity.example.com");
            o.ApiKey = "x";
            o.HttpTimeout = TimeSpan.FromMilliseconds(milliseconds);
        });
        act.Should().Throw<OptionsValidationException>();
    }

    // ===== Validation: MaxRetryAttempts ============================

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    public void Validation_Fails_When_MaxRetryAttempts_Out_Of_Range(int attempts)
    {
        var act = () => Validate(o =>
        {
            o.BaseAddress = new Uri("https://identity.example.com");
            o.ApiKey = "x";
            o.MaxRetryAttempts = attempts;
        });
        act.Should().Throw<OptionsValidationException>();
    }

    // ===== Validation: RetryBaseDelay ==============================

    [Theory]
    [InlineData(5)]      // below 10ms minimum
    [InlineData(5_001)]  // above 5s maximum
    public void Validation_Fails_When_RetryBaseDelay_Out_Of_Range(int milliseconds)
    {
        var act = () => Validate(o =>
        {
            o.BaseAddress = new Uri("https://identity.example.com");
            o.ApiKey = "x";
            o.RetryBaseDelay = TimeSpan.FromMilliseconds(milliseconds);
        });
        act.Should().Throw<OptionsValidationException>();
    }

    // ===== Validation: MaxClientCacheTtl ===========================

    [Theory]
    [InlineData(-1)]
    [InlineData(60 * 61)] // above 1h
    public void Validation_Fails_When_MaxClientCacheTtl_Out_Of_Range(int seconds)
    {
        var act = () => Validate(o =>
        {
            o.BaseAddress = new Uri("https://identity.example.com");
            o.ApiKey = "x";
            o.MaxClientCacheTtl = TimeSpan.FromSeconds(seconds);
        });
        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void Validation_Allows_Zero_MaxClientCacheTtl()
    {
        var act = () => Validate(o =>
        {
            o.BaseAddress = new Uri("https://identity.example.com");
            o.ApiKey = "x";
            o.MaxClientCacheTtl = TimeSpan.Zero;
        });
        act.Should().NotThrow();
    }

    // ===== Validation: defaults pass ===============================

    [Fact]
    public void Validation_Defaults_Plus_BaseAddress_And_ApiKey_Pass()
    {
        var act = () => Validate(o =>
        {
            o.BaseAddress = new Uri("https://identity.example.com");
            o.ApiKey = "test";
        });
        act.Should().NotThrow();
    }

    // ===== Helpers =================================================

    private static ServiceProvider BuildValid()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTenantClientCacheClient(o =>
        {
            o.BaseAddress = new Uri("https://identity.example.com/");
            o.ApiKey = "test-api-key";
            o.HttpTimeout = TimeSpan.FromSeconds(7);
            o.MaxRetryAttempts = 2;
            o.RetryBaseDelay = TimeSpan.FromMilliseconds(200);
            o.MaxClientCacheTtl = TimeSpan.FromMinutes(5);
        });
        return services.BuildServiceProvider();
    }

    private static void Validate(Action<TenantClientCacheClientOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTenantClientCacheClient(configure);

        using var provider = services.BuildServiceProvider();
        // Resolve the strongly-typed options to trigger eager validation.
        var monitor = provider.GetRequiredService<IOptionsMonitor<TenantClientCacheClientOptions>>();
        _ = monitor.CurrentValue;
    }
}
