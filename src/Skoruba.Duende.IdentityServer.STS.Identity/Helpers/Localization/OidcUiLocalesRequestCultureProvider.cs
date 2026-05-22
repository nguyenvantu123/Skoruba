// Copyright (c) Jan Skoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.
//
// OIDC ui_locales -> ASP.NET Core RequestLocalization bridge.
//
// The OpenID Connect Core spec (Section 3.1.2.1, "Authentication Request") defines
// `ui_locales` as a space-separated list of BCP-47 language tags ordered by user
// preference. ASP.NET Core's RequestLocalizationMiddleware does not consume this
// parameter natively, so this provider bridges it: when present on the request
// (typically on /connect/authorize, but read on every request for portability),
// the provider walks the list and returns the first tag that matches the host's
// resolved SupportedUICultures.
//
// Matching rules:
//   1. Exact case-insensitive match against SupportedUICultures (e.g. "vi" -> "vi", "vi-VN" -> "vi-VN").
//   2. If a specific tag like "vi-VN" is requested but only the neutral parent
//      "vi" is supported, the parent is used (RFC 4647 lookup, truncate-on-mismatch).
//   3. Tags that do not parse as a valid CultureInfo are skipped.
//   4. The first match wins; remaining tags in the list are ignored.
//
// When no tag matches, the provider returns null so the next provider in the
// chain (QueryString, Cookie, Accept-Language) gets a chance.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Helpers.Localization
{
    /// <summary>
    /// Bridges the OpenID Connect <c>ui_locales</c> authorization-request parameter
    /// (RFC 6749 / OIDC Core 1.0 § 3.1.2.1) into ASP.NET Core's
    /// <see cref="RequestLocalizationMiddleware"/> pipeline.
    /// <para>
    /// The provider reads <c>ui_locales</c> from the query string (and falls back to
    /// the form body when the request method is <c>POST</c> with a form
    /// content-type) and resolves the first BCP-47 tag that matches the host's
    /// configured <see cref="RequestLocalizationOptions.SupportedUICultures"/>. If
    /// no tag matches, <c>null</c> is returned so the next provider in the chain
    /// can take over.
    /// </para>
    /// <para>
    /// This provider should be registered <b>before</b>
    /// <see cref="QueryStringRequestCultureProvider"/>,
    /// <see cref="CookieRequestCultureProvider"/>, and
    /// <see cref="AcceptLanguageHeaderRequestCultureProvider"/> so that explicit
    /// OIDC requests win over inferred or sticky preferences.
    /// </para>
    /// </summary>
    public sealed class OidcUiLocalesRequestCultureProvider : RequestCultureProvider
    {
        /// <summary>
        /// The OIDC parameter name. The value is space-separated per OIDC Core 1.0.
        /// </summary>
        public const string ParameterName = "ui_locales";

        /// <inheritdoc />
        public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
        {
            if (httpContext is null)
            {
                throw new ArgumentNullException(nameof(httpContext));
            }

            var raw = ExtractParameter(httpContext);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Task.FromResult<ProviderCultureResult?>(null);
            }

            var supported = ResolveSupportedUICultures(httpContext);
            if (supported.Count == 0)
            {
                return Task.FromResult<ProviderCultureResult?>(null);
            }

            // OIDC: tags are space-separated and ordered by user preference.
            // Be lenient on whitespace — some providers emit tabs/newlines between tags.
            var tags = raw.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var tag in tags)
            {
                if (!TryParseCultureName(tag, out var canonical))
                {
                    continue;
                }

                // 1. Exact case-insensitive match against SupportedUICultures.
                var exact = supported.FirstOrDefault(c =>
                    string.Equals(c.Name, canonical, StringComparison.OrdinalIgnoreCase));
                if (exact is not null)
                {
                    return Task.FromResult<ProviderCultureResult?>(
                        new ProviderCultureResult(exact.Name, exact.Name));
                }

                // 2. RFC 4647 lookup fallback: walk parent chain (vi-VN -> vi).
                var parent = TruncateToParent(canonical);
                while (!string.IsNullOrEmpty(parent))
                {
                    var parentMatch = supported.FirstOrDefault(c =>
                        string.Equals(c.Name, parent, StringComparison.OrdinalIgnoreCase));
                    if (parentMatch is not null)
                    {
                        return Task.FromResult<ProviderCultureResult?>(
                            new ProviderCultureResult(parentMatch.Name, parentMatch.Name));
                    }
                    parent = TruncateToParent(parent);
                }
            }

            return Task.FromResult<ProviderCultureResult?>(null);
        }

        /// <summary>
        /// Reads <c>ui_locales</c> from query first; falls back to form when the
        /// request body is form-encoded. The query path covers the canonical OIDC
        /// case (<c>GET /connect/authorize?ui_locales=...</c>); the form path
        /// covers the OIDC <c>response_mode=form_post</c> POST and any client
        /// that surfaces preferences via a form submit.
        /// </summary>
        private static string? ExtractParameter(HttpContext httpContext)
        {
            if (httpContext.Request.Query.TryGetValue(ParameterName, out var fromQuery)
                && !StringValues.IsNullOrEmpty(fromQuery))
            {
                return fromQuery.ToString();
            }

            if (httpContext.Request.HasFormContentType)
            {
                if (httpContext.Request.Form.TryGetValue(ParameterName, out var fromForm)
                    && !StringValues.IsNullOrEmpty(fromForm))
                {
                    return fromForm.ToString();
                }
            }

            return null;
        }

        /// <summary>
        /// Reads <see cref="RequestLocalizationOptions.SupportedUICultures"/> from
        /// DI. Falls back to an empty list when the options are not registered (in
        /// which case there is nothing for this provider to match against).
        /// </summary>
        private static IReadOnlyList<CultureInfo> ResolveSupportedUICultures(HttpContext httpContext)
        {
            var options = httpContext.RequestServices?
                .GetService(typeof(IOptions<RequestLocalizationOptions>)) as IOptions<RequestLocalizationOptions>;
            var list = options?.Value?.SupportedUICultures;
            return list is null
                ? Array.Empty<CultureInfo>()
                : (list as IReadOnlyList<CultureInfo>) ?? list.ToList();
        }

        /// <summary>
        /// Validates a BCP-47 tag and returns its canonical form (matches what
        /// <see cref="CultureInfo.GetCultureInfo(string)"/> would emit). Returns
        /// false for empty / malformed / unparseable inputs.
        /// </summary>
        private static bool TryParseCultureName(string tag, out string canonical)
        {
            canonical = string.Empty;
            if (string.IsNullOrWhiteSpace(tag))
            {
                return false;
            }

            // Reject tags with control characters or otherwise invalid shapes early
            // — CultureInfo.GetCultureInfo on invariant-runtime can be lenient.
            tag = tag.Trim();
            if (tag.Length > 35)
            {
                // BCP-47 max length per RFC 5646 § 2.1 is 35 chars including hyphens.
                return false;
            }

            try
            {
                var culture = CultureInfo.GetCultureInfo(tag);
                if (culture.LCID == CultureInfo.InvariantCulture.LCID
                    && !string.Equals(tag, "iv", StringComparison.OrdinalIgnoreCase))
                {
                    // GetCultureInfo on globalization-invariant runtimes returns
                    // InvariantCulture for unknown tags instead of throwing. Reject
                    // those so we don't accidentally pin every request to invariant.
                    return false;
                }
                canonical = culture.Name;
                return true;
            }
            catch (CultureNotFoundException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>
        /// Returns the parent culture tag by stripping the last <c>-</c>-delimited
        /// segment, or null when there is no parent (single-segment tag).
        /// </summary>
        private static string? TruncateToParent(string tag)
        {
            var idx = tag.LastIndexOf('-');
            return idx <= 0 ? null : tag.Substring(0, idx);
        }
    }
}
