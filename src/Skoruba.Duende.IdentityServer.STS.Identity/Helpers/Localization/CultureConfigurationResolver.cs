// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Skoruba.Duende.IdentityServer.STS.Identity.Configuration;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Helpers.Localization;

/// <summary>
/// Result of <see cref="CultureConfigurationResolver.Resolve"/>: the materialized
/// list of supported cultures, the chosen default culture, and any culture codes
/// from the input that could not be parsed by <see cref="CultureInfo.GetCultureInfo(string)"/>.
/// Returned as a value object with init-only properties so callers cannot mutate it.
/// </summary>
public sealed class CultureConfigurationResolverResult
{
    public IReadOnlyList<CultureInfo> SupportedCultures { get; init; } = Array.Empty<CultureInfo>();

    public CultureInfo DefaultCulture { get; init; } = CultureInfo.InvariantCulture;

    public IReadOnlyList<string> InvalidCultureCodes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Pure, stateless mapping from <see cref="CultureConfiguration"/> (read from <c>appsettings.json</c>)
/// to a <see cref="CultureConfigurationResolverResult"/> suitable for configuring
/// <c>RequestLocalizationOptions</c> on the STS host.
/// <para>
/// The resolver:
/// <list type="bullet">
///   <item>Performs no I/O, no logging, and has no DI dependencies.</item>
///   <item>Never throws on bad input. Unparseable culture codes from the user's
///         <see cref="CultureConfiguration.Cultures"/> list are returned via
///         <see cref="CultureConfigurationResolverResult.InvalidCultureCodes"/> so the caller
///         (typically <c>StartupHelpers.AddMvcWithLocalization</c>) can log an Error per code
///         (Requirement 7.7).</item>
///   <item>Falls back to <see cref="CultureConfiguration.AvailableCultures"/> ∪ <c>{fallbackCulture}</c>
///         when the input <see cref="CultureConfiguration.Cultures"/> is null or empty
///         (Requirement 7.2).</item>
///   <item>Picks the default culture from the input when supported, else from
///         <paramref name="fallbackCulture"/> when supported, else the first supported culture
///         (Requirement 7.3).</item>
///   <item>Does <strong>not</strong> mutate the static <see cref="CultureConfiguration.DefaultRequestCulture"/>
///         field (Requirement 7.3).</item>
/// </list>
/// </para>
/// </summary>
public static class CultureConfigurationResolver
{
    /// <summary>
    /// Default fallback culture for STS host instances when the operator does not configure one.
    /// Vietnamese (<c>"vi"</c>) per Requirement 7.3.
    /// </summary>
    public const string StsHostFallbackCulture = "vi";

    /// <summary>
    /// Resolves the supported cultures and the default culture from a <see cref="CultureConfiguration"/>
    /// without performing any I/O or throwing on malformed input.
    /// </summary>
    /// <param name="configuration">
    /// The bound <c>CultureConfiguration</c> section, or <c>null</c> if absent. Both the input
    /// and its <see cref="CultureConfiguration.Cultures"/> list may be null.
    /// </param>
    /// <param name="fallbackCulture">
    /// Culture code applied when <paramref name="configuration"/> is null/empty or its default
    /// is unsupported. Defaults to <see cref="StsHostFallbackCulture"/> (<c>"vi"</c>).
    /// </param>
    /// <param name="availableCultures">
    /// Optional override of the static <see cref="CultureConfiguration.AvailableCultures"/> pool.
    /// Intended primarily for testing; pass <c>null</c> in production code paths.
    /// </param>
    /// <returns>
    /// A <see cref="CultureConfigurationResolverResult"/> with non-null collections.
    /// <see cref="CultureConfigurationResolverResult.SupportedCultures"/> is guaranteed
    /// non-empty whenever the available pool contains at least one parseable code.
    /// </returns>
    public static CultureConfigurationResolverResult Resolve(
        CultureConfiguration? configuration,
        string fallbackCulture = StsHostFallbackCulture,
        IEnumerable<string>? availableCultures = null)
    {
        // Defensive: bad fallback string degrades to the documented default rather than throwing.
        var resolvedFallback = string.IsNullOrWhiteSpace(fallbackCulture)
            ? StsHostFallbackCulture
            : fallbackCulture;

        // 1. Build the allowed pool: AvailableCultures ∪ { fallbackCulture }, case-insensitive distinct,
        //    preserving original ordering so the language switcher renders in a stable order.
        var poolOrdered = new List<string>();
        var poolLookup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var code in availableCultures ?? CultureConfiguration.AvailableCultures)
        {
            if (string.IsNullOrWhiteSpace(code)) continue;
            if (poolLookup.Add(code)) poolOrdered.Add(code);
        }

        if (poolLookup.Add(resolvedFallback)) poolOrdered.Add(resolvedFallback);

        // 2. Decide whether the user supplied a non-empty Cultures list, or we should default to the pool.
        var userProvidedCultures = configuration?.Cultures is { Count: > 0 };
        var inputCodes = userProvidedCultures
            ? configuration!.Cultures
            : (IList<string>)poolOrdered;

        // 3. Parse + filter to the pool. Unparseable codes from the user-provided list go to
        //    InvalidCultureCodes so the caller can emit one Error log per offending code (Req 7.7).
        var supportedCultures = new List<CultureInfo>();
        var supportedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalidCultureCodes = new List<string>();

        for (var i = 0; i < inputCodes.Count; i++)
        {
            var code = inputCodes[i];

            if (string.IsNullOrWhiteSpace(code))
            {
                if (userProvidedCultures)
                {
                    invalidCultureCodes.Add(code ?? string.Empty);
                }
                continue;
            }

            if (!TryGetCultureInfo(code, out var parsed))
            {
                if (userProvidedCultures)
                {
                    invalidCultureCodes.Add(code);
                }
                continue;
            }

            // A parseable code that is not in the allowed pool is silently dropped to match the
            // existing intersect-with-AvailableCultures behavior in StartupHelpers (pre-refactor).
            if (!poolLookup.Contains(code) && !poolLookup.Contains(parsed!.Name))
            {
                continue;
            }

            if (supportedNames.Add(parsed!.Name))
            {
                supportedCultures.Add(parsed);
            }
        }

        // 4. If a user-provided list filters down to nothing (all invalid or all out-of-pool), fall
        //    back to the pool itself so the host always renders in some locale (matches the
        //    original `if (!supportedCultureCodes.Any())` safety net).
        if (supportedCultures.Count == 0 && userProvidedCultures)
        {
            foreach (var code in poolOrdered)
            {
                if (!TryGetCultureInfo(code, out var parsed)) continue;
                if (supportedNames.Add(parsed!.Name))
                {
                    supportedCultures.Add(parsed);
                }
            }
        }

        // 5. Pick the default culture.
        var defaultCulture = ResolveDefaultCulture(
            supportedCultures,
            configuration?.DefaultCulture,
            resolvedFallback);

        return new CultureConfigurationResolverResult
        {
            SupportedCultures = supportedCultures,
            DefaultCulture = defaultCulture,
            InvalidCultureCodes = invalidCultureCodes,
        };
    }

    private static CultureInfo ResolveDefaultCulture(
        IReadOnlyList<CultureInfo> supportedCultures,
        string? requestedDefault,
        string fallbackCulture)
    {
        if (!string.IsNullOrWhiteSpace(requestedDefault)
            && TryFindSupported(supportedCultures, requestedDefault!, out var fromInput))
        {
            return fromInput!;
        }

        if (TryFindSupported(supportedCultures, fallbackCulture, out var fromFallback))
        {
            return fromFallback!;
        }

        return supportedCultures.Count > 0
            ? supportedCultures[0]
            : CultureInfo.InvariantCulture;
    }

    private static bool TryFindSupported(
        IReadOnlyList<CultureInfo> supportedCultures,
        string code,
        out CultureInfo? culture)
    {
        for (var i = 0; i < supportedCultures.Count; i++)
        {
            var sc = supportedCultures[i];
            if (string.Equals(sc.Name, code, StringComparison.OrdinalIgnoreCase))
            {
                culture = sc;
                return true;
            }
        }
        culture = null;
        return false;
    }

    private static bool TryGetCultureInfo(string code, out CultureInfo? culture)
    {
        try
        {
            culture = CultureInfo.GetCultureInfo(code);
            return true;
        }
        catch (CultureNotFoundException)
        {
            culture = null;
            return false;
        }
        catch (ArgumentException)
        {
            // Defensive: very long or otherwise malformed codes can throw ArgumentException
            // on some runtimes. Treat them the same as CultureNotFoundException — never throw.
            culture = null;
            return false;
        }
    }
}
