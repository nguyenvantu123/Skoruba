// Feature: tenant-client-cache-expansion, Task 6
//
// Property test for Audit_Event structure + redaction emitted by
// TenantClientCacheService. Locks down (a) exactly-one log entry per
// public op, (b) the structured field set, (c) the documented
// (Outcome → LogLevel) table, and (d) the secret-redaction contract
// when an underlying IDistributedCache exception carries connection
// string credentials.
//
// Property: P14 Audit logging fields + redaction + log levels.
//
// Validates: Requirements 13.1, 13.3, 13.4, 13.5, 13.6, 13.7, 16.1

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache.Helpers;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache;

public sealed class TenantClientCacheLoggingProperties
{
    // ===== Generators ==================================================

    public static class Arbs
    {
        private static readonly char[] AsciiPool =
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_".ToCharArray();

        private static Gen<string> NonEmptyIdGen()
            => from len in Gen.Choose(1, 16)
               from chars in Gen.Elements(AsciiPool).ListOf(len)
               select new string(chars.ToArray());

        public static Arbitrary<NonEmptyId> NonEmptyId()
            => NonEmptyIdGen().Select(s => new NonEmptyId(s)).ToArbitrary();

        public static Arbitrary<OperationKind> OperationKind()
            => Gen.Elements(
                    TenantClientCacheLoggingProperties.OperationKind.WriteSuccess,
                    TenantClientCacheLoggingProperties.OperationKind.InvalidateSuccess,
                    TenantClientCacheLoggingProperties.OperationKind.WriteSkippedDisabled,
                    TenantClientCacheLoggingProperties.OperationKind.InvalidateSkippedDisabled,
                    TenantClientCacheLoggingProperties.OperationKind.ReadHit,
                    TenantClientCacheLoggingProperties.OperationKind.ReadMiss,
                    TenantClientCacheLoggingProperties.OperationKind.ReadSkippedDisabled,
                    TenantClientCacheLoggingProperties.OperationKind.WriteFailedTransient,
                    TenantClientCacheLoggingProperties.OperationKind.InvalidateFailedTransient)
                .ToArbitrary();
    }

    public sealed record NonEmptyId(string Value);

    /// <summary>Operation flavour the property iterates over.</summary>
    public enum OperationKind
    {
        WriteSuccess,
        InvalidateSuccess,
        ReadHit,
        ReadMiss,
        WriteSkippedDisabled,
        InvalidateSkippedDisabled,
        ReadSkippedDisabled,
        WriteFailedTransient,
        InvalidateFailedTransient,
    }

    private static readonly IReadOnlyDictionary<OperationKind, (string EventType, LogLevel Level, string Outcome)> ExpectedShape =
        new Dictionary<OperationKind, (string, LogLevel, string)>
        {
            [OperationKind.WriteSuccess]                = ("TenantClientCacheWrite",      LogLevel.Information, "write_succeeded"),
            [OperationKind.InvalidateSuccess]           = ("TenantClientCacheInvalidate", LogLevel.Information, "invalidate_succeeded"),
            [OperationKind.ReadHit]                     = ("TenantClientCacheRead",       LogLevel.Debug,       "hit"),
            [OperationKind.ReadMiss]                    = ("TenantClientCacheRead",       LogLevel.Debug,       "miss"),
            [OperationKind.WriteSkippedDisabled]        = ("TenantClientCacheWrite",      LogLevel.Debug,       "write_skipped_disabled"),
            [OperationKind.InvalidateSkippedDisabled]   = ("TenantClientCacheInvalidate", LogLevel.Debug,       "write_skipped_disabled"),
            [OperationKind.ReadSkippedDisabled]         = ("TenantClientCacheRead",       LogLevel.Debug,       "write_skipped_disabled"),
            [OperationKind.WriteFailedTransient]        = ("TenantClientCacheWrite",      LogLevel.Warning,     "write_failed_transient"),
            [OperationKind.InvalidateFailedTransient]   = ("TenantClientCacheInvalidate", LogLevel.Warning,     "invalidate_failed_transient"),
        };

    // ===== Helpers =====================================================

    private static TenantClientCacheOptions BuildOptions(bool enabled = true)
        => new()
        {
            Enabled = enabled,
            AbsoluteTtl = TimeSpan.FromHours(1),
            SlidingTtl = null,
            RefreshInterval = TimeSpan.FromHours(1),
            WriteTimeoutMs = 2000,
            MaxClientsPerTenant = 5000,
        };

    private static ClientDto BuildClient(string clientId)
        => new()
        {
            ClientId = clientId,
            ClientName = "Sample",
            Enabled = true,
            ProtocolType = "oidc",
            AllowedScopes = new List<string> { "openid", "profile" },
            RedirectUris = new List<string> { "https://app/callback" },
        };

    private static FakeTimeProvider FixedTime()
        => new(new DateTimeOffset(2025, 6, 15, 10, 30, 45, TimeSpan.Zero));

    /// <summary>
    /// Run the operation flavour against a freshly-built service +
    /// CapturingLogger and return the captured log entries plus the raw
    /// service handle in case a test needs to issue further calls.
    /// </summary>
    private static async Task<CapturingLogger<TenantClientCacheService>> RunOperationAsync(
        OperationKind kind,
        string tenantKey,
        string clientId)
    {
        ThrowingDistributedCache cache = new();
        var optionsValue = kind is OperationKind.WriteSkippedDisabled
            or OperationKind.InvalidateSkippedDisabled
            or OperationKind.ReadSkippedDisabled
                ? BuildOptions(enabled: false)
                : BuildOptions(enabled: true);

        var options = new StubOptionsMonitor<TenantClientCacheOptions>(optionsValue);
        var metrics = new TenantClientCacheMetrics();
        var logger = new CapturingLogger<TenantClientCacheService>();
        var service = new TenantClientCacheService(cache, options, logger, metrics, FixedTime());

        switch (kind)
        {
            case OperationKind.WriteSuccess:
                await service.WriteSnapshotAsync(tenantKey, BuildClient(clientId), CancellationToken.None);
                break;

            case OperationKind.InvalidateSuccess:
                await service.InvalidateSnapshotAsync(tenantKey, clientId, CancellationToken.None);
                break;

            case OperationKind.ReadHit:
                await service.WriteSnapshotAsync(tenantKey, BuildClient(clientId), CancellationToken.None);
                logger.Clear(); // discard the write entry, only assert the read.
                await service.ReadSnapshotAsync(tenantKey, clientId, CancellationToken.None);
                break;

            case OperationKind.ReadMiss:
                await service.ReadSnapshotAsync(tenantKey, clientId, CancellationToken.None);
                break;

            case OperationKind.WriteSkippedDisabled:
                await service.WriteSnapshotAsync(tenantKey, BuildClient(clientId), CancellationToken.None);
                break;

            case OperationKind.InvalidateSkippedDisabled:
                await service.InvalidateSnapshotAsync(tenantKey, clientId, CancellationToken.None);
                break;

            case OperationKind.ReadSkippedDisabled:
                await service.ReadSnapshotAsync(tenantKey, clientId, CancellationToken.None);
                break;

            case OperationKind.WriteFailedTransient:
                cache.ThrowOnNext(new InvalidOperationException("redis down for write"));
                await service.WriteSnapshotAsync(tenantKey, BuildClient(clientId), CancellationToken.None);
                break;

            case OperationKind.InvalidateFailedTransient:
                cache.ThrowOnNext(new InvalidOperationException("redis down for invalidate"));
                await service.InvalidateSnapshotAsync(tenantKey, clientId, CancellationToken.None);
                break;
        }

        return logger;
    }

    // ===== Property 14 — Audit log fields + redaction + log levels =====

    /// <summary>
    /// P14: every public operation emits exactly one log entry whose
    /// structured fields conform to the documented contract and whose
    /// LogLevel matches the (Outcome → Level) table.
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Arbs) })]
    public async Task Property14_AuditLogging(
        OperationKind kind,
        NonEmptyId tenant,
        NonEmptyId client)
    {
        var logger = await RunOperationAsync(kind, tenant.Value, client.Value);

        // Exactly one log entry per logical operation (R13.1).
        logger.Entries.Should().HaveCount(1, "every public op emits exactly one Audit_Event");

        var entry = logger.Entries.Single();
        var (expectedEventType, expectedLevel, expectedOutcome) = ExpectedShape[kind];

        // Structured field set (R13.1 — EventType, TenantKey, ClientId,
        // Outcome, DurationMs, SnapshotVersion, CorrelationId).
        entry.Fields.Should().ContainKey("EventType");
        entry.Fields.Should().ContainKey("TenantKey");
        entry.Fields.Should().ContainKey("ClientId");
        entry.Fields.Should().ContainKey("Outcome");
        entry.Fields.Should().ContainKey("DurationMs");
        entry.Fields.Should().ContainKey("SnapshotVersion");
        entry.Fields.Should().ContainKey("CorrelationId");

        entry.Fields["EventType"].Should().Be(expectedEventType);
        entry.Fields["Outcome"].Should().Be(expectedOutcome);
        entry.Fields["TenantKey"].Should().Be(tenant.Value.Trim().ToLowerInvariant());
        entry.Fields["ClientId"].Should().Be(client.Value);

        // Log level matches the documented table (R13.5).
        entry.Level.Should().Be(expectedLevel);

        // Redaction guard: even when no exception was thrown the message
        // must NEVER contain a snapshot body, raw secret, raw cache key,
        // or the literal pattern of a connection string credential.
        entry.Message.Should().NotContain("clientSecrets");
        entry.Message.Should().NotContain("\"Properties\"");
        entry.Message.Should().NotContain(":clients:" + client.Value); // R13.6: no verbatim key
    }

    // ===== Example-based — fail-soft sanitisation ======================

    [Fact]
    public async Task Failed_Write_Audit_Redacts_Connection_String_Credentials()
    {
        // Inject a Redis-shaped exception whose message carries multiple
        // credential patterns. The audit's ExceptionMessage field MUST
        // NOT echo any of those values verbatim, MUST contain the "***"
        // sentinel from LogRedaction, AND MUST be ≤ 256 chars (R13.4).
        var cache = new ThrowingDistributedCache();
        cache.ThrowOnNext(new InvalidOperationException(
            "connstr=server,password=topsecret,auth=AAA;db=app"));

        var options = new StubOptionsMonitor<TenantClientCacheOptions>(BuildOptions());
        var metrics = new TenantClientCacheMetrics();
        var logger = new CapturingLogger<TenantClientCacheService>();
        var service = new TenantClientCacheService(cache, options, logger, metrics, FixedTime());

        await service.WriteSnapshotAsync("tenant-a", BuildClient("client-1"), CancellationToken.None);

        var entry = logger.Entries.Single();
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Fields.TryGetValue("ExceptionMessage", out var sanitised).Should().BeTrue();
        var sanitisedString = sanitised!.ToString()!;

        sanitisedString.Should().NotContain("topsecret");
        sanitisedString.Should().NotContain("AAA");
        sanitisedString.Should().Contain("***");
        sanitisedString.Length.Should().BeLessThanOrEqualTo(LogRedaction.MaxMessageLength);
    }

    [Fact]
    public async Task Failed_Write_Audit_Truncates_Long_Exception_Message_To_256_Chars()
    {
        // R13.4: only the first 256 chars of ex.Message survive.
        var longMessage = new string('q', 1024);
        var cache = new ThrowingDistributedCache();
        cache.ThrowOnNext(new InvalidOperationException(longMessage));

        var options = new StubOptionsMonitor<TenantClientCacheOptions>(BuildOptions());
        var metrics = new TenantClientCacheMetrics();
        var logger = new CapturingLogger<TenantClientCacheService>();
        var service = new TenantClientCacheService(cache, options, logger, metrics, FixedTime());

        await service.WriteSnapshotAsync("tenant-a", BuildClient("client-1"), CancellationToken.None);

        var entry = logger.Entries.Single();
        entry.Fields.TryGetValue("ExceptionMessage", out var sanitised).Should().BeTrue();
        sanitised!.ToString()!.Length.Should().Be(256);
    }

    [Fact]
    public async Task Audit_Message_Never_Contains_Snapshot_Body_Or_Allowed_Scope_Values()
    {
        // R13.3 negative: structured log message MUST NOT echo Allowed
        // scope strings or any other public-safe field that could be
        // mined for tenant-config information from log shipping.
        var cache = new ThrowingDistributedCache();
        var options = new StubOptionsMonitor<TenantClientCacheOptions>(BuildOptions());
        var metrics = new TenantClientCacheMetrics();
        var logger = new CapturingLogger<TenantClientCacheService>();
        var service = new TenantClientCacheService(cache, options, logger, metrics, FixedTime());

        var dto = BuildClient("client-1");
        dto.AllowedScopes = new List<string> { "secret-scope-name", "another-scope" };
        dto.RedirectUris = new List<string> { "https://internal.invalid/callback?secret=marker" };

        await service.WriteSnapshotAsync("tenant-a", dto, CancellationToken.None);

        var entry = logger.Entries.Single();

        entry.Message.Should().NotContain("secret-scope-name");
        entry.Message.Should().NotContain("internal.invalid");
        entry.Message.Should().NotContain("?secret=marker");
    }
}
