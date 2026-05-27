// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

#nullable enable

using System;

using Microsoft.Extensions.Logging;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.PublicTenantClients;

/// <summary>
/// Static helper that emits the canonical <c>Audit_Event_Public_Read</c>
/// structured log entries for every terminal outcome of the public-read
/// endpoint pipeline. Centralises log-level choice (R8.2) and field-set
/// redaction (R8.7) so callers cannot accidentally leak the API-key
/// header, its hash, the response body, or the snapshot envelope.
/// </summary>
/// <remarks>
/// <para>
/// Schema fields (Glossary entry <c>Audit_Event_Public_Read</c>):
/// </para>
/// <list type="bullet">
///   <item><description><see cref="AuditFields.EventType"/> — required, e.g. <c>TenantClientCachePublicRead.Hit</c>.</description></item>
///   <item><description><see cref="AuditFields.TenantKey"/> — OMITTED for <c>Unauthorized</c> / <c>BadRequest</c> (R8.4 anti-enumeration).</description></item>
///   <item><description><see cref="AuditFields.ClientId"/> — OMITTED for <c>Unauthorized</c> / <c>BadRequest</c> (R8.4 anti-enumeration).</description></item>
///   <item><description><see cref="AuditFields.Outcome"/> — one of {Hit, NotModified, Miss, Unauthorized, RateLimited, BadRequest, ServiceUnavailable}.</description></item>
///   <item><description><see cref="AuditFields.DurationMs"/> — non-negative wall-clock measure.</description></item>
///   <item><description><see cref="AuditFields.CorrelationId"/> — optional, typically <c>Activity.Current?.TraceId.ToString()</c> (R8.6).</description></item>
///   <item><description><see cref="AuditFields.RemoteIpHash"/> — optional, never raw IP (R9.6).</description></item>
///   <item><description><see cref="AuditFields.HttpStatus"/> — terminal status code.</description></item>
///   <item><description><see cref="AuditFields.ETagSent"/> — set on <c>Hit</c> / <c>NotModified</c> only.</description></item>
///   <item><description><see cref="AuditFields.RetryAfterSeconds"/> — set on <c>RateLimited</c> / <c>ServiceUnavailable</c> only.</description></item>
/// </list>
/// <para>
/// Forbidden fields (R3.4, R8.7, R8.8, R10.10): the raw <c>X-Tenant-Api-Key</c>
/// value, the SHA-256 hash of the key, the response body bytes, the snapshot
/// envelope, the raw remote IP, AND any field whose name matches
/// <c>(?i).*secret.*</c>. The <see cref="AuditFields"/> shape is closed so
/// these fields are structurally impossible to log.
/// </para>
/// </remarks>
internal static class AuditEventPublicRead
{
    /// <summary>Event type prefix shared by every emit helper.</summary>
    public const string EventTypePrefix = "TenantClientCachePublicRead.";

    /// <summary>Canonical message template — every emit helper uses the
    /// same template so structured-log consumers can match by literal
    /// text without per-outcome branching.</summary>
    /// <remarks>
    /// The template lists every schema field as a positional placeholder
    /// so structured backends (Serilog, OTel) record them by name.
    /// </remarks>
    private const string MessageTemplate =
        "{EventType} tenant={TenantKey} client={ClientId} outcome={Outcome} "
        + "durationMs={DurationMs} corr={CorrelationId} remoteIpHash={RemoteIpHash} "
        + "status={HttpStatus} etag={ETagSent} retryAfterSeconds={RetryAfterSeconds}";

    // ===== Emit_* helpers =================================================
    //
    // Each helper picks the matching log level per R8.2 and applies the
    // R8.4 / R8.7 redaction policy by RE-PROJECTING the input fields when
    // the outcome forbids them (Unauthorized / BadRequest must NOT leak
    // tenantKey / clientId).

    /// <summary>Emit <c>Outcome=Hit</c> (200) at level Information (R8.2).</summary>
    public static void EmitHit(ILogger logger, AuditFields fields)
        => Emit(logger, LogLevel.Information, EnsureOutcome(fields, AuditOutcome.Hit));

    /// <summary>Emit <c>Outcome=NotModified</c> (304) at level Information (R8.2).</summary>
    public static void EmitNotModified(ILogger logger, AuditFields fields)
        => Emit(logger, LogLevel.Information, EnsureOutcome(fields, AuditOutcome.NotModified));

    /// <summary>Emit <c>Outcome=Miss</c> (404) at level Debug (R8.2).</summary>
    public static void EmitMiss(ILogger logger, AuditFields fields)
        => Emit(logger, LogLevel.Debug, EnsureOutcome(fields, AuditOutcome.Miss));

    /// <summary>Emit <c>Outcome=Unauthorized</c> (401) at level Warning (R8.2).
    /// Redacts <c>TenantKey</c> + <c>ClientId</c> per R8.4 anti-enumeration.</summary>
    public static void EmitUnauthorized(ILogger logger, AuditFields fields)
        => Emit(logger, LogLevel.Warning, RedactTenantIdentity(fields, AuditOutcome.Unauthorized));

    /// <summary>Emit <c>Outcome=RateLimited</c> (429) at level Warning (R8.2).</summary>
    public static void EmitRateLimited(ILogger logger, AuditFields fields)
        => Emit(logger, LogLevel.Warning, EnsureOutcome(fields, AuditOutcome.RateLimited));

    /// <summary>Emit <c>Outcome=BadRequest</c> (400) at level Warning (R8.2).
    /// Redacts <c>TenantKey</c> + <c>ClientId</c> per R8.4 anti-enumeration.</summary>
    public static void EmitBadRequest(ILogger logger, AuditFields fields)
        => Emit(logger, LogLevel.Warning, RedactTenantIdentity(fields, AuditOutcome.BadRequest));

    /// <summary>Emit <c>Outcome=ServiceUnavailable</c> (503) at level Error (R8.2).</summary>
    public static void EmitServiceUnavailable(ILogger logger, AuditFields fields)
        => Emit(logger, LogLevel.Error, EnsureOutcome(fields, AuditOutcome.ServiceUnavailable));

    // ===== Emit core ======================================================

    private static void Emit(ILogger logger, LogLevel level, AuditFields fields)
    {
        ArgumentNullException.ThrowIfNull(logger);

        // Emit at the chosen level only when enabled — the logger pipeline
        // can short-circuit and avoid materialising the structured state.
        if (!logger.IsEnabled(level))
        {
            return;
        }

        // Pre-compose the EventType so structured backends can match by
        // exact literal (e.g. "TenantClientCachePublicRead.Hit").
        var eventType = EventTypePrefix + fields.Outcome;

        logger.Log(
            level,
            eventId: default,
            exception: null,
            message: MessageTemplate,
            args: new object?[]
            {
                eventType,
                fields.TenantKey,
                fields.ClientId,
                fields.Outcome,
                fields.DurationMs,
                fields.CorrelationId,
                fields.RemoteIpHash,
                fields.HttpStatus,
                fields.ETagSent,
                fields.RetryAfterSeconds,
            });
    }

    private static AuditFields EnsureOutcome(AuditFields fields, string expected)
        => string.Equals(fields.Outcome, expected, StringComparison.Ordinal)
            ? fields
            : fields with { Outcome = expected };

    /// <summary>
    /// R8.4 anti-enumeration: <c>Unauthorized</c> and <c>BadRequest</c>
    /// outcomes MUST NOT include <c>TenantKey</c> nor <c>ClientId</c>.
    /// We re-project the record so callers cannot accidentally leak them.
    /// </summary>
    private static AuditFields RedactTenantIdentity(AuditFields fields, string outcome)
        => fields with
        {
            Outcome = outcome,
            TenantKey = null,
            ClientId = null,
        };
}

/// <summary>
/// Structured payload for <see cref="AuditEventPublicRead"/>. The shape
/// is closed: callers cannot extend it with arbitrary fields, which
/// guarantees no log entry includes the raw API-key header, its hash,
/// the response body, the snapshot envelope, or any field whose name
/// matches <c>*Secret*</c> (R3.4, R8.7, R8.8, R10.10).
/// </summary>
/// <param name="EventType">
/// Pre-formatted event type, typically
/// <c>TenantClientCachePublicRead.{Outcome}</c>. The
/// <see cref="AuditEventPublicRead"/> helpers compose this automatically
/// from <see cref="Outcome"/>.
/// </param>
/// <param name="TenantKey">
/// Normalized (<c>Trim().ToLowerInvariant()</c>) tenant key. OMIT (set to
/// <c>null</c>) for <c>Unauthorized</c> / <c>BadRequest</c> outcomes
/// (R8.4 anti-enumeration). The helpers enforce this redaction — passing
/// a non-null value on those outcomes is silently dropped.
/// </param>
/// <param name="ClientId">
/// Trimmed client identifier. OMIT for <c>Unauthorized</c> /
/// <c>BadRequest</c> outcomes (R8.4 anti-enumeration).
/// </param>
/// <param name="Outcome">
/// One of <c>{Hit, NotModified, Miss, Unauthorized, RateLimited,
/// BadRequest, ServiceUnavailable}</c> (R8.1). Use <see cref="AuditOutcome"/>
/// constants.
/// </param>
/// <param name="DurationMs">
/// Total wall-clock for the request, in milliseconds. Non-negative.
/// </param>
/// <param name="CorrelationId">
/// Typically <c>Activity.Current?.TraceId.ToString()</c> (R8.6). Null is
/// allowed — for example background sweeps emit no Activity.
/// </param>
/// <param name="RemoteIpHash">
/// Hashed remote IP (<see cref="IpHashHelper"/>). Null when
/// <c>Audit.LogIpHash = false</c> (R3.6) or the connection has no
/// remote IP. Raw IP MUST NEVER be passed in (R9.6).
/// </param>
/// <param name="HttpStatus">
/// Terminal HTTP status code (R8.1).
/// </param>
/// <param name="ETagSent">
/// Set on <c>Hit</c> / <c>NotModified</c> only — the
/// <c>W/&quot;&lt;hex&gt;&quot;</c> ETag value the controller emitted.
/// Never the <c>If-None-Match</c> request value.
/// </param>
/// <param name="RetryAfterSeconds">
/// Set on <c>RateLimited</c> / <c>ServiceUnavailable</c> only — the
/// <c>Retry-After</c> response header value (seconds).
/// </param>
internal sealed record AuditFields(
    string EventType,
    string? TenantKey,
    string? ClientId,
    string Outcome,
    double DurationMs,
    string? CorrelationId,
    string? RemoteIpHash,
    int HttpStatus,
    string? ETagSent,
    int? RetryAfterSeconds);

/// <summary>
/// Outcome string constants that match the <see cref="AuditFields.Outcome"/>
/// values listed in Glossary entry <c>Audit_Event_Public_Read</c> (R8.1).
/// Centralised so log entries and metric tags use byte-exact strings.
/// </summary>
internal static class AuditOutcome
{
    public const string Hit                 = "Hit";
    public const string NotModified         = "NotModified";
    public const string Miss                = "Miss";
    public const string Unauthorized        = "Unauthorized";
    public const string RateLimited         = "RateLimited";
    public const string BadRequest          = "BadRequest";
    public const string ServiceUnavailable  = "ServiceUnavailable";
}
