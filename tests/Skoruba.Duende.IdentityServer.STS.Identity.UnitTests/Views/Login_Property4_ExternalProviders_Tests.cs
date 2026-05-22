// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Configuration;
using Skoruba.Duende.IdentityServer.STS.Identity.UnitTests.TestInfrastructure;
using Skoruba.Duende.IdentityServer.STS.Identity.ViewModels.Account;

namespace Skoruba.Duende.IdentityServer.STS.Identity.UnitTests.Views;

// Feature: login-ui-redesign-i18n, Property 4: External providers grid iterates exactly one anchor per visible provider
//
// For any LoginViewModel with N (N in [0, 5]) external providers (each carrying a
// URL-safe AuthenticationScheme and an optional URL-safe DisplayName), rendering
// ~/Views/Account/Login.cshtml MUST emit exactly one <a> targeting
// `/Account/ExternalLogin` per *visible* provider — where visibility is decided by
// `LoginViewModel.VisibleExternalProviders` (i.e. providers whose `DisplayName` is
// non-whitespace). Each anchor MUST carry a `provider=<AuthenticationScheme>` query
// parameter and a `returnUrl=<Model.ReturnUrl>` query parameter that round-trip the
// model values without truncation, mangling, or duplication.
//
// Rendering harness setup
// -----------------------
// Login.cshtml uses the framework's `asp-action`/`asp-route-*` tag helpers, which
// call into `IUrlHelper.Action(...)`. The standard `IUrlHelper` requires an
// endpoint-routing graph that the unit-test `RazorRenderHost` does not stand up
// (this would turn the test into an integration test). We register a deterministic
// stub `IUrlHelperFactory` that synthesizes `/<controller>/<action>?<route-values>`
// URLs from the AnchorTagHelper inputs. The stub URL-encodes every route value so
// reserved query characters in `ReturnUrl` (such as `?`, `&`, `=`) are emitted
// the same way ASP.NET Core's production `UrlHelper` would emit them.
//
// `EnableLocalLogin = false` is chosen per the task brief because it makes the
// external-providers grid the *only* surface that emits `asp-action="ExternalLogin"`
// anchors. The local-login form, phone tab, and tabs container are skipped, which
// stabilizes the test against view-model permutations that are not part of the
// claim under test (Requirement 2.9).
//
// `IOptions<PhoneOtpLoginConfiguration>` is registered with `Enabled = false` so
// the phone tab and `_PhoneRequestPanel` are hidden — Login.cshtml's
// `phoneOtpEnabled` short-circuit kicks in, eliminating an additional <form>/<a>
// surface that the property does not target.
//
// Validates: Requirements 2.9, 9.3
public sealed class Login_Property4_ExternalProviders_Tests
{
    /// <summary>
    /// Generator for a single <see cref="ExternalProvider"/>. The
    /// <c>AuthenticationScheme</c> is a non-empty URL-safe ASCII string drawn from
    /// the unreserved RFC 3986 character pool so it round-trips through the
    /// AnchorTagHelper -> stub-IUrlHelper -> Razor HTML encoding pipeline without
    /// transformation. The <c>DisplayName</c> is one of: null, empty, whitespace,
    /// or a non-empty URL-safe string — covering both branches of
    /// <see cref="LoginViewModel.VisibleExternalProviders"/> visibility filter
    /// (<c>!String.IsNullOrWhiteSpace(x.DisplayName)</c>).
    /// </summary>
    private static Gen<ExternalProvider> ProviderGen()
    {
        // RFC 3986 unreserved characters — safe in any URI component without
        // percent-encoding. We exclude reserved characters from the scheme so the
        // assertion can compare scheme values directly without round-tripping
        // through Uri.UnescapeDataString.
        const string urlSafeChars =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";

        var urlSafeChar = Gen.Elements(urlSafeChars.ToCharArray());

        var schemeGen =
            from len in Gen.Choose(1, 16)
            from chars in urlSafeChar.ListOf(len)
            select new string(chars.ToArray());

        var nonNullDisplayNameGen =
            from len in Gen.Choose(1, 16)
            from chars in urlSafeChar.ListOf(len)
            select (string?)new string(chars.ToArray());

        // Mix invisible-trigger values (null / empty / whitespace) with non-empty
        // URL-safe strings so the test exercises both branches of the visibility
        // filter — the property must hold across both.
        var displayNameGen = Gen.Frequency(
            (1, Gen.Constant<string?>(null)),
            (1, Gen.Constant<string?>(string.Empty)),
            (1, Gen.Constant<string?>("   ")),
            (5, nonNullDisplayNameGen));

        return
            from scheme in schemeGen
            from displayName in displayNameGen
            select new ExternalProvider
            {
                AuthenticationScheme = scheme,
                DisplayName = displayName,
            };
    }

    /// <summary>
    /// Produces a list of 0..5 external providers with distinct
    /// <c>AuthenticationScheme</c> values. Distinctness is required because
    /// duplicate schemes would yield duplicate `provider=...` query parameters and
    /// the per-provider assertion would no longer be unambiguous.
    /// </summary>
    private static Gen<List<ExternalProvider>> ProvidersGen()
    {
        return
            from count in Gen.Choose(0, 5)
            from providers in ProviderGen().ListOf(count)
            select providers
                .GroupBy(p => p.AuthenticationScheme, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();
    }

    /// <summary>
    /// Generator for <c>ReturnUrl</c> values. Mixes the explicit edge cases the
    /// requirement enumerates (null, empty, simple paths, paths with reserved
    /// query characters) with arbitrary URL-safe ASCII strings biased to include
    /// characters that <c>Uri.EscapeDataString</c> actually transforms — proving
    /// the round-trip survives encoding.
    /// </summary>
    private static Gen<string?> ReturnUrlGen()
    {
        var edgeCases = Gen.Elements<string?>(
            null,
            string.Empty,
            "/",
            "/Foo",
            "/Foo?a=1&b=2",
            "/Connect/Authorize?client_id=foo");

        const string urlSafeChars =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789" +
            "-._~/" +
            "?&=#%";

        var urlSafeChar = Gen.Elements(urlSafeChars.ToCharArray());

        var arbitrary =
            from len in Gen.Choose(1, 40)
            from chars in urlSafeChar.ListOf(len)
            select (string?)new string(chars.ToArray());

        return Gen.Frequency(
            (3, edgeCases),
            (5, arbitrary));
    }

    [Property(MaxTest = 100)]
    // Feature: login-ui-redesign-i18n, Property 4: External providers grid iterates exactly one anchor per visible provider
    public Property External_Providers_Grid_Iterates_One_Anchor_Per_Visible_Provider()
    {
        return Prop.ForAll(
            ProvidersGen().ToArbitrary(),
            ReturnUrlGen().ToArbitrary(),
            (providers, returnUrl) =>
            {
                using var harness = new RazorRenderHost(services =>
                {
                    // Stub IUrlHelperFactory so the AnchorTagHelper resolves
                    // `asp-action`/`asp-route-*` to deterministic paths. RemoveAll first
                    // because AddControllersWithViews() registers the framework default
                    // via TryAddSingleton, which our singleton would otherwise lose to.
                    services.RemoveAll<IUrlHelperFactory>();
                    services.AddSingleton<IUrlHelperFactory, StubUrlHelperFactory>();

                    // PhoneOtpLoginConfiguration is required by Login.cshtml's
                    // @inject IOptions<PhoneOtpLoginConfiguration>. Enabled=false hides
                    // the phone tab so the rendered surface is just the external grid.
                    services.AddSingleton<IOptions<PhoneOtpLoginConfiguration>>(
                        new OptionsWrapper<PhoneOtpLoginConfiguration>(
                            new PhoneOtpLoginConfiguration { Enabled = false }));
                });

                var model = new LoginViewModel
                {
                    Username = string.Empty,
                    Password = string.Empty,
                    ReturnUrl = returnUrl,
                    EnableLocalLogin = false,
                    ExternalProviders = providers,
                };

                string html;
                try
                {
                    html = harness.RenderPartialAsync(
                            "~/Views/Account/Login.cshtml",
                            model: model)
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    return false.Label(
                        $"render failed: {ex.GetType().Name}: {ex.Message} | " +
                        $"providers=[{string.Join(",", providers.Select(p => $"({p.AuthenticationScheme},{Display(p.DisplayName)})"))}] " +
                        $"returnUrl='{Display(returnUrl)}'");
                }

                var anchors = LocateExternalLoginAnchors(html);

                var visible = model.VisibleExternalProviders.ToList();

                // Claim 1: exactly one anchor per visible provider.
                if (anchors.Count != visible.Count)
                {
                    return false.Label(
                        $"anchor-count mismatch: anchors={anchors.Count} visible={visible.Count} " +
                        $"providers=[{string.Join(",", providers.Select(p => $"({p.AuthenticationScheme},{Display(p.DisplayName)})"))}] " +
                        $"hrefs=[{string.Join(",", anchors.Select(a => $"'{a.Href}'"))}]");
                }

                // Claim 2 + 3: every anchor's `provider` query parameter matches a
                // visible provider's AuthenticationScheme exactly, and every anchor's
                // `returnUrl` query parameter matches the model's ReturnUrl exactly.
                // We pair anchors to providers by `provider` value (set equality)
                // so the test does not lock in render order — only the multiset
                // equivalence the requirement actually claims.
                var expectedSchemes = visible
                    .Select(p => p.AuthenticationScheme)
                    .ToHashSet(StringComparer.Ordinal);

                var actualSchemes = new HashSet<string>(StringComparer.Ordinal);
                var returnUrlMismatches = new List<string>();
                var expectedReturnUrl = returnUrl ?? string.Empty;

                foreach (var anchor in anchors)
                {
                    if (!TryParseQuery(anchor.Href, out var query))
                    {
                        return false.Label(
                            $"href '{anchor.Href}' is not parseable as a path-with-query");
                    }

                    var providerValue = query["provider"] ?? string.Empty;
                    var returnUrlValue = query["returnUrl"] ?? string.Empty;

                    actualSchemes.Add(providerValue);

                    if (!string.Equals(returnUrlValue, expectedReturnUrl, StringComparison.Ordinal))
                    {
                        returnUrlMismatches.Add(
                            $"'{anchor.Href}': returnUrl='{returnUrlValue}' expected='{expectedReturnUrl}'");
                    }
                }

                var schemesMatch = expectedSchemes.SetEquals(actualSchemes);
                var returnUrlsMatch = returnUrlMismatches.Count == 0;

                var ok = schemesMatch && returnUrlsMatch;

                return ok.Label(
                    $"anchors={anchors.Count} visible={visible.Count} " +
                    $"expectedSchemes=[{string.Join(",", expectedSchemes.OrderBy(s => s))}] " +
                    $"actualSchemes=[{string.Join(",", actualSchemes.OrderBy(s => s))}] " +
                    $"returnUrlMismatches=[{string.Join(" | ", returnUrlMismatches)}] " +
                    $"providers=[{string.Join(",", providers.Select(p => $"({p.AuthenticationScheme},{Display(p.DisplayName)})"))}] " +
                    $"returnUrl='{Display(returnUrl)}'");
            });
    }

    private sealed record ExternalLoginAnchor(string Href);

    /// <summary>
    /// Returns every <c>&lt;a&gt;</c> in the rendered HTML whose <c>href</c> targets
    /// the <c>/Account/ExternalLogin</c> path. The harness's stub URL helper emits
    /// exactly that path prefix, and the Login_Page redesign also emits a
    /// <c>/Account/Register</c> sign-up anchor (when <c>RegisterConfiguration.Enabled</c>
    /// is true) which we filter out by exact path match.
    /// </summary>
    private static List<ExternalLoginAnchor> LocateExternalLoginAnchors(string html)
    {
        var parser = new HtmlParser();
        using var document = parser.ParseDocument(html);

        return document
            .QuerySelectorAll("a")
            .OfType<IHtmlAnchorElement>()
            .Select(a => a.GetAttribute("href") ?? string.Empty)
            .Where(href => href.StartsWith("/Account/ExternalLogin", StringComparison.Ordinal))
            .Select(href => new ExternalLoginAnchor(href))
            .ToList();
    }

    /// <summary>
    /// Splits <paramref name="hrefWithQuery"/> at the first <c>?</c> and parses the
    /// query string into a name/value collection. Returns <c>false</c> when the
    /// query is absent (an ExternalLogin anchor without query parameters would
    /// already fail the count claim above).
    /// </summary>
    private static bool TryParseQuery(string hrefWithQuery, out System.Collections.Specialized.NameValueCollection query)
    {
        var idx = hrefWithQuery.IndexOf('?');
        if (idx < 0 || idx == hrefWithQuery.Length - 1)
        {
            query = new System.Collections.Specialized.NameValueCollection();
            return false;
        }
        query = HttpUtility.ParseQueryString(hrefWithQuery.Substring(idx + 1));
        return true;
    }

    private static string Display(string? value)
        => value is null
            ? "<null>"
            : value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    /// <summary>
    /// Stub <see cref="IUrlHelperFactory"/> producing a deterministic
    /// <see cref="StubUrlHelper"/> per request. The factory is needed because
    /// <see cref="RazorRenderHost"/> brings up MVC + Razor view engine but does not
    /// stand up endpoint routing — the framework default factory would throw
    /// "Could not find an IRouter associated with the ActionContext" when the
    /// AnchorTagHelper tries to generate an URL.
    /// </summary>
    private sealed class StubUrlHelperFactory : IUrlHelperFactory
    {
        public IUrlHelper GetUrlHelper(ActionContext context) => new StubUrlHelper(context);
    }

    /// <summary>
    /// Stub <see cref="IUrlHelper"/> that synthesizes URLs from the
    /// AnchorTagHelper's <see cref="UrlActionContext.Action"/> /
    /// <see cref="UrlActionContext.Controller"/> / <see cref="UrlActionContext.Values"/>
    /// triple. The path component is <c>/&lt;controller&gt;/&lt;action&gt;</c>
    /// (defaulting controller to "Account" when omitted, matching the Login_Page
    /// convention). Route values are appended as a percent-encoded query string in
    /// insertion order, mirroring how ASP.NET Core's production
    /// <c>UrlHelper.Action(...)</c> serializes them — modulo route templates this
    /// stub does not consult.
    /// </summary>
    private sealed class StubUrlHelper : IUrlHelper
    {
        public StubUrlHelper(ActionContext actionContext)
        {
            ActionContext = actionContext;
        }

        public ActionContext ActionContext { get; }

        public string Action(UrlActionContext actionContext)
        {
            var controller = actionContext.Controller ?? "Account";
            var action = actionContext.Action ?? string.Empty;
            var path = "/" + controller + "/" + action;

            if (actionContext.Values is null)
            {
                return path;
            }

            var routeValues = new RouteValueDictionary(actionContext.Values);
            if (routeValues.Count == 0)
            {
                return path;
            }

            var parts = new List<string>(routeValues.Count);
            foreach (var kvp in routeValues)
            {
                var value = kvp.Value?.ToString() ?? string.Empty;
                parts.Add(Uri.EscapeDataString(kvp.Key) + "=" + Uri.EscapeDataString(value));
            }
            return path + "?" + string.Join("&", parts);
        }

        // Members below are unused by Login.cshtml but must be implemented for the
        // IUrlHelper contract. They return deterministic stubs so any accidental
        // call surfaces visibly in the rendered HTML rather than throwing.
        public string Content(string contentPath) => contentPath ?? string.Empty;

        public bool IsLocalUrl(string url)
            => !string.IsNullOrEmpty(url) && url.StartsWith("/", StringComparison.Ordinal);

        public string Link(string routeName, object values) => "/" + (routeName ?? string.Empty);

        public string RouteUrl(UrlRouteContext routeContext)
            => "/" + (routeContext.RouteName ?? string.Empty);
    }
}
