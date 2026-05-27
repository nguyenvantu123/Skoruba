// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;

/// <summary>
/// Fail-fast validator for <see cref="TenantClientCachePublicReadOptions"/>.
/// </summary>
/// <remarks>
/// Implements R1.4 / R1.5, R4.3 / R4.4, R5.6 / R5.7, R6.2, R9.6 of spec
/// <c>tenant-client-cache-public-read</c>. Failure messages MUST name the
/// offending key (tenantKey, configuration path, or list entry) BUT MUST
/// NEVER include the raw API-key hash value (R1.4 anti-leak requirement).
/// </remarks>
internal sealed class TenantClientCachePublicReadOptionsValidator
    : IValidateOptions<TenantClientCachePublicReadOptions>
{
    /// <summary>R1.4 — 64-char lowercase hex digest format.</summary>
    internal static readonly Regex Sha256HexLower =
        new("^[0-9a-f]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal const int RateLimitTokenLimitMin = 1;
    internal const int RateLimitTokenLimitMax = 10_000;
    internal static readonly TimeSpan ReplenishmentPeriodMin = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan ReplenishmentPeriodMax = TimeSpan.FromHours(1);
    internal const int CorsPreflightMaxAgeMin = 0;
    internal const int CorsPreflightMaxAgeMax = 86_400;
    internal const int ResponseCacheMaxAgeMin = 0;
    internal const int ResponseCacheMaxAgeMax = 3600;

    private const string Section = TenantClientCachePublicReadOptions.SectionName;

    private readonly IHostEnvironment _env;

    public TenantClientCachePublicReadOptionsValidator(IHostEnvironment env)
    {
        _env = env ?? throw new ArgumentNullException(nameof(env));
    }

    public ValidateOptionsResult Validate(string? name, TenantClientCachePublicReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        ValidateApiKeys(options, failures);
        ValidateRateLimit(options, failures);
        ValidateCors(options, failures);
        ValidateResponseCache(options, failures);
        ValidateAudit(options, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateApiKeys(
        TenantClientCachePublicReadOptions options,
        List<string> failures)
    {
        // R1.7: empty store is allowed — every request will fall through
        // to 401/invalid_api_key at runtime. Validator must not fail here.
        if (options.ApiKeys is null)
        {
            return;
        }

        foreach (var (rawKey, rawValue) in options.ApiKeys)
        {
            // R1.5 — tenantKey shape (must be trimmed + lowercase).
            var key = rawKey ?? string.Empty;
            if (key.Length == 0)
            {
                failures.Add(
                    $"{Section}:ApiKeys contains an empty tenant key.");
            }
            else
            {
                if (key != key.Trim())
                {
                    failures.Add(
                        $"{Section}:ApiKeys key '{key}' must be trimmed (no leading or trailing whitespace).");
                }

                if (key.Any(char.IsUpper))
                {
                    failures.Add(
                        $"{Section}:ApiKeys key '{key}' must be lowercase.");
                }
            }

            // R1.4 — value MUST be 64-char lowercase hex.
            // SECURITY: never include the offending VALUE in the failure
            // message. Reference the tenant key only.
            if (string.IsNullOrEmpty(rawValue) || !Sha256HexLower.IsMatch(rawValue))
            {
                failures.Add(
                    $"{Section}:ApiKeys[{key}] is not a 64-char lowercased hex SHA-256 digest.");
            }
        }
    }

    private static void ValidateRateLimit(
        TenantClientCachePublicReadOptions options,
        List<string> failures)
    {
        var rl = options.RateLimit;
        if (rl is null)
        {
            failures.Add($"{Section}:RateLimit is required.");
            return;
        }

        // R4.3
        if (rl.TokenLimit < RateLimitTokenLimitMin || rl.TokenLimit > RateLimitTokenLimitMax)
        {
            failures.Add(
                $"{Section}:RateLimit:TokenLimit = '{rl.TokenLimit.ToString(CultureInfo.InvariantCulture)}' is outside the allowed inclusive range [{RateLimitTokenLimitMin}, {RateLimitTokenLimitMax}].");
        }

        // R4.4
        if (rl.ReplenishmentPeriod < ReplenishmentPeriodMin || rl.ReplenishmentPeriod > ReplenishmentPeriodMax)
        {
            failures.Add(
                $"{Section}:RateLimit:ReplenishmentPeriod = '{FormatTimeSpan(rl.ReplenishmentPeriod)}' is outside the allowed inclusive range [{FormatTimeSpan(ReplenishmentPeriodMin)}, {FormatTimeSpan(ReplenishmentPeriodMax)}].");
        }
    }

    private static void ValidateCors(
        TenantClientCachePublicReadOptions options,
        List<string> failures)
    {
        var cors = options.Cors;
        if (cors is null)
        {
            failures.Add($"{Section}:Cors is required.");
            return;
        }

        // R5.6 — every entry must be a valid absolute URL with scheme https
        // (or http for localhost only).
        if (cors.AllowedOrigins is not null)
        {
            foreach (var origin in cors.AllowedOrigins)
            {
                if (!Uri.TryCreate(origin, UriKind.Absolute, out var u))
                {
                    failures.Add(
                        $"{Section}:Cors:AllowedOrigins entry '{origin}' is not an absolute URL.");
                    continue;
                }

                var isLocalhost = string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase);
                var isHttps = string.Equals(u.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);
                var isHttpLocalhost = string.Equals(u.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) && isLocalhost;

                if (!isHttps && !isHttpLocalhost)
                {
                    failures.Add(
                        $"{Section}:Cors:AllowedOrigins entry '{origin}' must use scheme https (or http for localhost).");
                }
            }
        }

        // R5.7
        if (cors.PreflightMaxAgeSeconds < CorsPreflightMaxAgeMin || cors.PreflightMaxAgeSeconds > CorsPreflightMaxAgeMax)
        {
            failures.Add(
                $"{Section}:Cors:PreflightMaxAgeSeconds = '{cors.PreflightMaxAgeSeconds.ToString(CultureInfo.InvariantCulture)}' is outside the allowed inclusive range [{CorsPreflightMaxAgeMin}, {CorsPreflightMaxAgeMax}].");
        }
    }

    private static void ValidateResponseCache(
        TenantClientCachePublicReadOptions options,
        List<string> failures)
    {
        var rc = options.ResponseCache;
        if (rc is null)
        {
            failures.Add($"{Section}:ResponseCache is required.");
            return;
        }

        // R6.2
        if (rc.MaxAgeSeconds < ResponseCacheMaxAgeMin || rc.MaxAgeSeconds > ResponseCacheMaxAgeMax)
        {
            failures.Add(
                $"{Section}:ResponseCache:MaxAgeSeconds = '{rc.MaxAgeSeconds.ToString(CultureInfo.InvariantCulture)}' is outside the allowed inclusive range [{ResponseCacheMaxAgeMin}, {ResponseCacheMaxAgeMax}].");
        }
    }

    private void ValidateAudit(
        TenantClientCachePublicReadOptions options,
        List<string> failures)
    {
        var audit = options.Audit;
        if (audit is null)
        {
            failures.Add($"{Section}:Audit is required.");
            return;
        }

        // R9.6: salt MUST be non-empty in Production. Dev/Staging may default
        // to empty (the host enforces production-only fail-fast here so that
        // local development remains friction-free).
        if (_env.IsProduction() && string.IsNullOrWhiteSpace(audit.RemoteIpSalt))
        {
            failures.Add(
                $"{Section}:Audit:RemoteIpSalt is required in the Production environment (R9.6).");
        }
    }

    private static string FormatTimeSpan(TimeSpan value) =>
        value.ToString("c", CultureInfo.InvariantCulture);
}
