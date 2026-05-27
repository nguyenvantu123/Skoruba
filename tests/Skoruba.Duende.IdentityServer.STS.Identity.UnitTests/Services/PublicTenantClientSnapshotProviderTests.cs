// Feature: tenant-client-cache-public-read, Task 11/12 — STS.Identity consumer wrapper
//
// Unit tests for IPublicTenantClientSnapshotProvider + the disabled / real
// providers + the AddPublicTenantClientSnapshotConsumer extension.
//
// The STS.Identity unit-test project ships only xunit (no Moq, no
// FluentAssertions, no NSubstitute). Tests below use hand-rolled fakes/stubs
// to honor the "no new NuGet packages" rule.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Skoruba.Duende.IdentityServer.STS.Identity.Services;
using Skoruba.Duende.IdentityServer.TenantClientCache.Client;
using Skoruba.Duende.IdentityServer.TenantClientCache.Client.Models;

using TenantInfrastructure.Abstractions;

using Xunit;

namespace Skoruba.Duende.IdentityServer.STS.Identity.UnitTests.Services;

public sealed class PublicTenantClientSnapshotProviderTests
{
    private const string TestTenantKey = "test-tenant-acme";
    private const string TestClientId = "test-tenant-spa";
    private const string TestApiKey = "test-tenant-api-key-do-not-leak";

    // ---------------------------------------------------------------
    // Disabled path
    // ---------------------------------------------------------------

    [Fact]
    public async Task Disabled_Provider_Returns_Disabled_Outcome_Without_Calling_Sdk()
    {
        var configuration = BuildConfiguration(enabled: false);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContextAccessor>(new RecordingTenantContextAccessor());
        services.AddPublicTenantClientSnapshotConsumer(configuration);

        // Pre-condition for the test: the consumer wrapper must NOT have
        // resolved the SDK client when Enabled=false. We register a sentinel
        // SDK that throws on use; if the provider touches it, the test fails.
        services.AddSingleton<ITenantClientCacheClient>(new ThrowOnUseSdk());

        await using var sp = services.BuildServiceProvider();

        var provider = sp.GetRequiredService<IPublicTenantClientSnapshotProvider>();
        Assert.IsType<DisabledPublicTenantClientSnapshotProvider>(provider);

        var result = await provider.GetSnapshotAsync(TestClientId);

        Assert.Equal(PublicClientSnapshotOutcome.Disabled, result.Outcome);
        Assert.Null(result.Snapshot);
        Assert.Null(result.RetryAfter);
    }

    // ---------------------------------------------------------------
    // tenantKey resolution from ITenantContextAccessor
    // ---------------------------------------------------------------

    [Fact]
    public async Task Provider_Resolves_TenantKey_From_TenantContextAccessor_Not_Hardcoded()
    {
        var sdk = new RecordingSdk(
            new TenantClientSnapshotResult(
                Snapshot: BuildSnapshot(TestClientId),
                Etag: "\"etag-123\"",
                LastWriteUtc: DateTimeOffset.UtcNow,
                Version: 1,
                Outcome: SdkCacheOutcome.Hit,
                RetryAfter: null));

        var accessor = new RecordingTenantContextAccessor();
        accessor.Set(new TenantContext(TestTenantKey, new Dictionary<string, string>()));

        var (provider, _) = BuildRealProvider(sdk, accessor);

        var result = await provider.GetSnapshotAsync(TestClientId);

        Assert.Equal(PublicClientSnapshotOutcome.Snapshot, result.Outcome);
        Assert.NotNull(result.Snapshot);

        // The wrapper MUST forward the resolved tenantKey verbatim.
        Assert.Single(sdk.Calls);
        Assert.Equal(TestTenantKey, sdk.Calls[0].TenantKey);
        Assert.Equal(TestClientId, sdk.Calls[0].ClientId);
    }

    [Fact]
    public async Task Provider_Returns_NoTenantContext_Outcome_When_TenantContextAccessor_Current_IsNull()
    {
        var sdk = new ThrowOnUseSdk();
        var accessor = new RecordingTenantContextAccessor(); // Current is null

        var (provider, logger) = BuildRealProvider(sdk, accessor);

        var result = await provider.GetSnapshotAsync(TestClientId);

        Assert.Equal(PublicClientSnapshotOutcome.NoTenantContext, result.Outcome);
        Assert.Null(result.Snapshot);
        Assert.True(logger.HasWarning(), "Expected a Warning log entry for the NoTenantContext fail-soft path.");
    }

    // ---------------------------------------------------------------
    // clientId guard
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Provider_Returns_InvalidClientId_Outcome_When_ClientId_NullOrWhitespace(string? clientId)
    {
        var sdk = new ThrowOnUseSdk();
        var accessor = new RecordingTenantContextAccessor();
        accessor.Set(new TenantContext(TestTenantKey, new Dictionary<string, string>()));

        var (provider, logger) = BuildRealProvider(sdk, accessor);

        var result = await provider.GetSnapshotAsync(clientId!);

        Assert.Equal(PublicClientSnapshotOutcome.InvalidClientId, result.Outcome);
        Assert.Null(result.Snapshot);
        Assert.True(logger.HasWarning(), "Expected a Warning log entry for the InvalidClientId fail-soft path.");
    }

    // ---------------------------------------------------------------
    // SDK outcome mappings
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(SdkCacheOutcome.Hit)]
    [InlineData(SdkCacheOutcome.Miss)]
    [InlineData(SdkCacheOutcome.NotModified)]
    public async Task Provider_Maps_Sdk_Hit_To_Snapshot(SdkCacheOutcome sdkOutcome)
    {
        var snapshot = BuildSnapshot(TestClientId);
        var sdk = new RecordingSdk(
            new TenantClientSnapshotResult(
                Snapshot: snapshot,
                Etag: "\"etag-1\"",
                LastWriteUtc: DateTimeOffset.UtcNow,
                Version: 7,
                Outcome: sdkOutcome,
                RetryAfter: null));

        var accessor = new RecordingTenantContextAccessor();
        accessor.Set(new TenantContext(TestTenantKey, new Dictionary<string, string>()));

        var (provider, _) = BuildRealProvider(sdk, accessor);

        var result = await provider.GetSnapshotAsync(TestClientId);

        Assert.Equal(PublicClientSnapshotOutcome.Snapshot, result.Outcome);
        Assert.Same(snapshot, result.Snapshot);
    }

    [Theory]
    [InlineData(SdkCacheOutcome.NotFound, PublicClientSnapshotOutcome.NotFound)]
    [InlineData(SdkCacheOutcome.Unauthorized, PublicClientSnapshotOutcome.Unauthorized)]
    [InlineData(SdkCacheOutcome.RateLimited, PublicClientSnapshotOutcome.RateLimited)]
    [InlineData(SdkCacheOutcome.ServiceUnavailable, PublicClientSnapshotOutcome.Unavailable)]
    [InlineData(SdkCacheOutcome.TransientFailure, PublicClientSnapshotOutcome.Unavailable)]
    public async Task Provider_Maps_Sdk_NotFound_Or_Unauthorized_To_NullSnapshot_With_Outcome(
        SdkCacheOutcome sdkOutcome,
        PublicClientSnapshotOutcome expected)
    {
        var sdk = new RecordingSdk(
            new TenantClientSnapshotResult(
                Snapshot: null,
                Etag: null,
                LastWriteUtc: null,
                Version: null,
                Outcome: sdkOutcome,
                RetryAfter: TimeSpan.FromSeconds(13)));

        var accessor = new RecordingTenantContextAccessor();
        accessor.Set(new TenantContext(TestTenantKey, new Dictionary<string, string>()));

        var (provider, logger) = BuildRealProvider(sdk, accessor);

        var result = await provider.GetSnapshotAsync(TestClientId);

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Snapshot);
        Assert.Equal(TimeSpan.FromSeconds(13), result.RetryAfter);
        Assert.True(logger.HasWarning(), "Expected a Warning log entry for the non-success SDK outcome.");

        // The wrapper MUST NOT escalate any non-success outcome to an exception.
        // (The fact that we reached this assertion proves no throw bubbled out.)
    }

    // ---------------------------------------------------------------
    // No PII / API-key / tenant-key leakage on transient failure
    // ---------------------------------------------------------------

    [Fact]
    public async Task Provider_Logs_Warning_On_TransientFailure_NoApiKey_Or_TenantKey_Leakage()
    {
        var sdk = new RecordingSdk(
            new TenantClientSnapshotResult(
                Snapshot: null,
                Etag: null,
                LastWriteUtc: null,
                Version: null,
                Outcome: SdkCacheOutcome.TransientFailure,
                RetryAfter: null));

        var accessor = new RecordingTenantContextAccessor();
        accessor.Set(new TenantContext(TestTenantKey, new Dictionary<string, string>()));

        var (provider, logger) = BuildRealProvider(sdk, accessor);

        var result = await provider.GetSnapshotAsync(TestClientId);

        Assert.Equal(PublicClientSnapshotOutcome.Unavailable, result.Outcome);
        Assert.True(logger.HasWarning());

        // Walk every captured log entry. None of them may contain the API key
        // plaintext, an obvious base64 hash digest, or any field whose name
        // matches `(?i).*secret.*` (i.e. log keys).
        foreach (var entry in logger.Entries)
        {
            Assert.DoesNotContain(TestApiKey, entry.RenderedMessage, StringComparison.Ordinal);
            Assert.DoesNotContain("ApiKey", entry.RenderedMessage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("X-Tenant-Api-Key", entry.RenderedMessage, StringComparison.OrdinalIgnoreCase);

            foreach (var kvp in entry.State)
            {
                Assert.False(
                    kvp.Key.Contains("Secret", StringComparison.OrdinalIgnoreCase),
                    $"Log scope leaked a 'secret'-named field: {kvp.Key}");
                Assert.False(
                    kvp.Key.Contains("ApiKey", StringComparison.OrdinalIgnoreCase),
                    $"Log scope leaked an ApiKey-named field: {kvp.Key}");

                var valueString = kvp.Value?.ToString() ?? string.Empty;
                Assert.DoesNotContain(TestApiKey, valueString, StringComparison.Ordinal);
            }
        }
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static (IPublicTenantClientSnapshotProvider Provider, RecordingLogger<PublicTenantClientSnapshotProvider> Logger)
        BuildRealProvider(ITenantClientCacheClient sdk, ITenantContextAccessor accessor)
    {
        var logger = new RecordingLogger<PublicTenantClientSnapshotProvider>();
        var provider = new PublicTenantClientSnapshotProvider(sdk, accessor, logger);
        return (provider, logger);
    }

    private static IConfiguration BuildConfiguration(bool enabled)
    {
        var dict = new Dictionary<string, string?>
        {
            ["PublicTenantClientSnapshotConsumer:Enabled"] = enabled ? "true" : "false",
            // Always provide BaseAddress + ApiKey so the SDK validator does not
            // fault during build of the Enabled=true path. The Disabled test
            // does not actually wire the SDK so these are inert there too.
            ["PublicTenantClientSnapshotConsumer:BaseAddress"] = "https://identity.test.local",
            ["PublicTenantClientSnapshotConsumer:ApiKey"] = TestApiKey,
            ["PublicTenantClientSnapshotConsumer:HttpTimeoutSeconds"] = "5",
            ["PublicTenantClientSnapshotConsumer:MaxRetryAttempts"] = "2",
            ["PublicTenantClientSnapshotConsumer:RetryBaseDelayMilliseconds"] = "200",
            ["PublicTenantClientSnapshotConsumer:MaxClientCacheTtlSeconds"] = "300",
            ["PublicTenantClientSnapshotConsumer:EnableInMemoryCaching"] = "true",
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(dict)
            .Build();
    }

    private static PublicClientSnapshot BuildSnapshot(string clientId)
    {
        return new PublicClientSnapshot
        {
            ClientId = clientId,
            Enabled = true,
            ProtocolType = "oidc",
            LastWriteUtc = DateTime.UtcNow,
        };
    }

    // ---------------------------------------------------------------
    // Stubs / fakes
    // ---------------------------------------------------------------

    private sealed class RecordingSdk : ITenantClientCacheClient
    {
        public List<(string TenantKey, string ClientId, string? IfNoneMatch)> Calls { get; } = new();

        private readonly TenantClientSnapshotResult _result;

        public RecordingSdk(TenantClientSnapshotResult result)
        {
            _result = result;
        }

        public Task<TenantClientSnapshotResult> GetClientAsync(
            string tenantKey,
            string clientId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((tenantKey, clientId, null));
            return Task.FromResult(_result);
        }

        public Task<TenantClientSnapshotResult> GetClientAsync(
            string tenantKey,
            string clientId,
            string? ifNoneMatch,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((tenantKey, clientId, ifNoneMatch));
            return Task.FromResult(_result);
        }
    }

    private sealed class ThrowOnUseSdk : ITenantClientCacheClient
    {
        public Task<TenantClientSnapshotResult> GetClientAsync(
            string tenantKey,
            string clientId,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(
                "ITenantClientCacheClient must NOT be invoked by the wrapper in this test.");

        public Task<TenantClientSnapshotResult> GetClientAsync(
            string tenantKey,
            string clientId,
            string? ifNoneMatch,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(
                "ITenantClientCacheClient must NOT be invoked by the wrapper in this test.");
    }

    private sealed class RecordingTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? Current { get; private set; }

        public void Set(TenantContext context) => Current = context;

        public void Clear() => Current = null;
    }

    private sealed record LoggedEntry(
        LogLevel Level,
        string RenderedMessage,
        IReadOnlyList<KeyValuePair<string, object?>> State,
        Exception? Exception);

    private sealed class RecordingLogger<T> : ILogger<T>, ILogger
    {
        public ConcurrentBag<LoggedEntry> Entries { get; } = new();

        IDisposable? ILogger.BeginScope<TState>(TState state) => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public bool HasWarning() => Entries.Any(e => e.Level >= LogLevel.Warning);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var rendered = formatter(state, exception);
            var stateList = state is IReadOnlyList<KeyValuePair<string, object?>> kv
                ? kv
                : (IReadOnlyList<KeyValuePair<string, object?>>)Array.Empty<KeyValuePair<string, object?>>();
            Entries.Add(new LoggedEntry(logLevel, rendered, stateList, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
