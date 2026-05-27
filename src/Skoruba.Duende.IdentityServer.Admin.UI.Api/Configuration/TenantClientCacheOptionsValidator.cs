// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;

/// <summary>
/// Fail-fast validator for <see cref="TenantClientCacheOptions"/>.
/// </summary>
/// <remarks>
/// Implements R1.3 – R1.6 of the <c>tenant-client-cache-expansion</c> spec.
/// Every failure message MUST name the offending configuration key path
/// (e.g. <c>TenantClientCache:AbsoluteTtl</c>) AND the observed value.
/// When <see cref="TenantClientCacheOptions.Enabled"/> is <c>false</c>, range checks
/// are skipped (per R1.7/R1.8 the cache is a no-op anyway).
/// </remarks>
internal sealed class TenantClientCacheOptionsValidator : IValidateOptions<TenantClientCacheOptions>
{
    internal static readonly TimeSpan AbsoluteTtlMin = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan AbsoluteTtlMax = TimeSpan.FromHours(24);
    internal static readonly TimeSpan SlidingTtlMin = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan RefreshIntervalMin = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan RefreshIntervalMax = TimeSpan.FromHours(24);
    internal const int WriteTimeoutMsMin = 100;
    internal const int WriteTimeoutMsMax = 10_000;
    internal const int MaxClientsPerTenantMin = 1;
    internal const int MaxClientsPerTenantMax = 50_000;

    public ValidateOptionsResult Validate(string? name, TenantClientCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // R1.7 / R1.8: when disabled, skip range checks. The cache is a no-op.
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        // R1.3 — AbsoluteTtl ∈ [00:05:00, 24:00:00]
        if (options.AbsoluteTtl < AbsoluteTtlMin || options.AbsoluteTtl > AbsoluteTtlMax)
        {
            failures.Add(FormatRangeFailure(
                key: $"{TenantClientCacheOptions.SectionName}:{nameof(TenantClientCacheOptions.AbsoluteTtl)}",
                observed: FormatTimeSpan(options.AbsoluteTtl),
                min: FormatTimeSpan(AbsoluteTtlMin),
                max: FormatTimeSpan(AbsoluteTtlMax)));
        }

        // R1.4 — SlidingTtl, when non-null, ∈ [00:01:00, AbsoluteTtl]
        if (options.SlidingTtl is { } sliding)
        {
            if (sliding < SlidingTtlMin || sliding > options.AbsoluteTtl)
            {
                failures.Add(FormatRangeFailure(
                    key: $"{TenantClientCacheOptions.SectionName}:{nameof(TenantClientCacheOptions.SlidingTtl)}",
                    observed: FormatTimeSpan(sliding),
                    min: FormatTimeSpan(SlidingTtlMin),
                    max: FormatTimeSpan(options.AbsoluteTtl)));
            }
        }

        // R1.5 — RefreshInterval ∈ [00:05:00, 24:00:00]
        if (options.RefreshInterval < RefreshIntervalMin || options.RefreshInterval > RefreshIntervalMax)
        {
            failures.Add(FormatRangeFailure(
                key: $"{TenantClientCacheOptions.SectionName}:{nameof(TenantClientCacheOptions.RefreshInterval)}",
                observed: FormatTimeSpan(options.RefreshInterval),
                min: FormatTimeSpan(RefreshIntervalMin),
                max: FormatTimeSpan(RefreshIntervalMax)));
        }

        // R1.6 — WriteTimeoutMs ∈ [100, 10000]
        if (options.WriteTimeoutMs < WriteTimeoutMsMin || options.WriteTimeoutMs > WriteTimeoutMsMax)
        {
            failures.Add(FormatRangeFailure(
                key: $"{TenantClientCacheOptions.SectionName}:{nameof(TenantClientCacheOptions.WriteTimeoutMs)}",
                observed: options.WriteTimeoutMs.ToString(CultureInfo.InvariantCulture),
                min: WriteTimeoutMsMin.ToString(CultureInfo.InvariantCulture),
                max: WriteTimeoutMsMax.ToString(CultureInfo.InvariantCulture)));
        }

        // R1.6 — MaxClientsPerTenant ∈ [1, 50000]
        if (options.MaxClientsPerTenant < MaxClientsPerTenantMin || options.MaxClientsPerTenant > MaxClientsPerTenantMax)
        {
            failures.Add(FormatRangeFailure(
                key: $"{TenantClientCacheOptions.SectionName}:{nameof(TenantClientCacheOptions.MaxClientsPerTenant)}",
                observed: options.MaxClientsPerTenant.ToString(CultureInfo.InvariantCulture),
                min: MaxClientsPerTenantMin.ToString(CultureInfo.InvariantCulture),
                max: MaxClientsPerTenantMax.ToString(CultureInfo.InvariantCulture)));
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static string FormatRangeFailure(string key, string observed, string min, string max) =>
        $"Configuration value '{key}' = '{observed}' is outside the allowed inclusive range [{min}, {max}].";

    private static string FormatTimeSpan(TimeSpan value) =>
        value.ToString("c", CultureInfo.InvariantCulture);
}
