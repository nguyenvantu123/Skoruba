// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Skoruba.Duende.IdentityServer.STS.Identity.UnitTests.TestInfrastructure;
using Skoruba.Duende.IdentityServer.STS.Identity.ViewModels.Account;

namespace Skoruba.Duende.IdentityServer.STS.Identity.UnitTests.Views;

// Feature: login-ui-redesign-i18n, Property 6: Verify back-link preserves `returnUrl` with URL encoding
//
// For any returnUrl value (including null, empty, whitespace, paths with reserved
// query characters, fragments, and arbitrary URL-safe ASCII), rendering
// ~/Views/Account/LoginWithPhone/Verify.cshtml MUST set the back-link <a> href to:
//   * "/Account/Login" when returnUrl is null OR empty (matching the view's
//     `string.IsNullOrEmpty` check exactly), and
//   * "/Account/Login?returnUrl=" + Uri.EscapeDataString(returnUrl) for every other
//     value, so reserved query characters round-trip safely through the URL.
//
// The encoded form is what guarantees a returnUrl such as `/Foo?a=1&b=2` survives
// the back-link click without splitting into ambiguous query parameters
// (Requirement 9.1). Whitespace-only inputs intentionally fall in the "encode"
// branch because the view does NOT call IsNullOrWhiteSpace — only IsNullOrEmpty.
//
// Validates: Requirements 3.7, 9.1
public sealed class Verify_Property6_BackLinkReturnUrl_Tests
{
    /// <summary>
    /// Generator that mixes explicit edge cases the requirement enumerates with a
    /// stream of URL-safe ASCII strings biased to include reserved query characters
    /// (<c>?</c>, <c>&amp;</c>, <c>=</c>, <c>#</c>, <c>%</c>) so that
    /// <see cref="Uri.EscapeDataString"/> actually transforms the input. Arbitrary
    /// <see cref="System.String"/> draws are avoided because they include C0
    /// control characters and U+0000 which AngleSharp's HTML5 parser legitimately
    /// normalizes during attribute-value parsing — a layer the view does not claim
    /// to preserve.
    /// </summary>
    private static Gen<string?> ReturnUrlGenerator()
    {
        var edgeCases = Gen.Elements<string?>(
            null,
            string.Empty,
            " ",
            "/Foo",
            "/Foo?a=1&b=2",
            "/Foo#bar");

        const string urlSafeChars =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789" +
            "-._~/" +
            // Reserved query characters that must be percent-encoded so the round-trip
            // through Uri.EscapeDataString actually changes the input.
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
    // Feature: login-ui-redesign-i18n, Property 6: Verify back-link preserves `returnUrl` with URL encoding
    public Property Verify_BackLink_Preserves_ReturnUrl_With_UrlEncoding()
    {
        return Prop.ForAll(
            ReturnUrlGenerator().ToArbitrary(),
            returnUrl =>
            {
                using var harness = new RazorRenderHost();

                var model = new PhoneVerifyViewModel
                {
                    MaskedPhone = "******",
                    OtpLength = 6,
                    ReturnUrl = returnUrl,
                    ResendCooldownRemainingSeconds = 0
                };

                var html = harness.RenderPartialAsync(
                        "~/Views/Account/LoginWithPhone/Verify.cshtml",
                        model: model)
                    .GetAwaiter().GetResult();

                var backLink = LocateBackLink(html);
                if (backLink is null)
                {
                    return false.Label(
                        $"back-link not found for returnUrl='{Display(returnUrl)}'; html='{Truncate(html)}'");
                }

                var rawHref = backLink.GetAttribute("href") ?? string.Empty;
                var expected = string.IsNullOrEmpty(returnUrl)
                    ? "/Account/Login"
                    : "/Account/Login?returnUrl=" + Uri.EscapeDataString(returnUrl);

                var ok = string.Equals(rawHref, expected, StringComparison.Ordinal);

                return ok.Label(
                    $"returnUrl='{Display(returnUrl)}' " +
                    $"href='{rawHref}' expected='{expected}'");
            });
    }

    /// <summary>
    /// Locates the back-link anchor in the rendered Verify.cshtml. Prefers
    /// <c>a.link-secondary</c> per the view's class contract, then falls back to any
    /// <c>a[href^='/Account/Login']</c> in case the styling class is renamed in a
    /// future redesign — both selectors target the same element today.
    /// </summary>
    private static IElement? LocateBackLink(string html)
    {
        var parser = new HtmlParser();
        using var document = parser.ParseDocument(html);

        var anchor = document
            .QuerySelectorAll("a.link-secondary")
            .OfType<IHtmlAnchorElement>()
            .FirstOrDefault();

        if (anchor is not null)
        {
            return anchor;
        }

        return document
            .QuerySelectorAll("a")
            .OfType<IHtmlAnchorElement>()
            .FirstOrDefault(a =>
            {
                var href = a.GetAttribute("href") ?? string.Empty;
                return href.StartsWith("/Account/Login", StringComparison.Ordinal);
            });
    }

    private static string Display(string? value)
        => value is null
            ? "<null>"
            : value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    private static string Truncate(string value)
        => value.Length <= 400 ? value : value.Substring(0, 400) + "...";
}
