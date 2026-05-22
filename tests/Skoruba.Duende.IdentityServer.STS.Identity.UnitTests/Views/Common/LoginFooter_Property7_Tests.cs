// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Skoruba.Duende.IdentityServer.STS.Identity.Configuration;
using Skoruba.Duende.IdentityServer.STS.Identity.UnitTests.TestInfrastructure;

namespace Skoruba.Duende.IdentityServer.STS.Identity.UnitTests.Views.Common;

// Feature: login-ui-redesign-i18n, Property 7: Footer anchors fall back to `#` when URLs are null or whitespace
//
// For any (termsUrl, privacyUrl, supportUrl) triple — each independently null, empty, whitespace, or arbitrary —
// rendering Views/Shared/Common/_LoginFooter.cshtml MUST:
//   * Always render exactly 5 anchors with class "login-shell__footer-link"
//     (2 inside the TermsNotice paragraph + 3 utility links).
//   * For each anchor whose corresponding configured URL is null/empty/whitespace, set href="#".
//   * For each anchor whose corresponding URL is non-whitespace, set href to that URL verbatim.
//   * Never omit an anchor regardless of input.
//
// Validates: Requirements 4.1, 4.2, 4.4
public class LoginFooter_Property7_Tests
{
    /// <summary>
    /// Generator that mixes explicit edge cases (null, empty, whitespace variants) with
    /// non-whitespace URL-safe strings drawn from the RFC 3986 unreserved + sub-delims +
    /// reserved character set. The edge-case branch covers the null/whitespace fallback
    /// path the requirement targets; the non-whitespace branch verifies verbatim
    /// pass-through. Arbitrary <c>System.String</c> draws are intentionally avoided
    /// because they include C0 control characters and U+0000, which AngleSharp's HTML5
    /// parser legitimately normalizes during attribute-value parsing — a layer the
    /// requirement does not claim to preserve.
    /// </summary>
    private static Gen<string?> UrlInputGenerator()
    {
        var edgeCases = Gen.Elements<string?>(
            null, string.Empty, " ", "  ", "\t", "\n", " \r\n ",
            "https://example.com/terms",
            "https://example.com/privacy",
            "https://example.com/support",
            "/local/path",
            "#fragment");

        // RFC 3986 URL-safe characters: unreserved + sub-delims + ":/?#[]@".
        // This is the union of characters that may appear in a URI without percent-encoding.
        const string urlSafeChars =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789" +
            "-._~" +
            ":/?#[]@" +
            "!$&'()*+,;=";

        var urlSafeChar = Gen.Elements(urlSafeChars.ToCharArray());

        var prefix = Gen.Elements("https://", "/local/", "#", string.Empty);

        var urlSafeString =
            from len in Gen.Choose(1, 40)
            from chars in urlSafeChar.ListOf(len)
            from p in prefix
            select (string?)(p + new string(chars.ToArray()));

        // Filter out values the edge-case branch already covers so the non-whitespace
        // branch is unambiguous. With urlSafeChars containing no whitespace this is a
        // no-op for non-empty results, but guards the empty-prefix + zero-length tail
        // corner regardless.
        var nonWhitespaceUrl = urlSafeString.Where(s => !string.IsNullOrWhiteSpace(s));

        return Gen.Frequency(
            (5, edgeCases),
            (1, nonWhitespaceUrl));
    }

    [Property(MaxTest = 100)]
    // Feature: login-ui-redesign-i18n, Property 7: Footer anchors fall back to `#` when URLs are null or whitespace
    public Property Footer_Anchors_FallbackTo_Hash_When_UrlsAreNullOrWhitespace()
    {
        return Prop.ForAll(
            UrlInputGenerator().ToArbitrary(),
            UrlInputGenerator().ToArbitrary(),
            UrlInputGenerator().ToArbitrary(),
            (termsUrl, privacyUrl, supportUrl) =>
            {
                using var harness = new RazorRenderHost();
                harness.RootConfiguration.SetAdminConfiguration(new AdminConfiguration
                {
                    TermsOfServiceUri = termsUrl,
                    PrivacyPolicyUri = privacyUrl,
                    SupportUri = supportUrl,
                    // Footer doesn't read these but the type carries them; assign null so
                    // any future regression that surfaces them in the footer is visible.
                    MarketingProductsUri = null,
                    MarketingFeaturesUri = null,
                    MarketingPricingUri = null,
                });

                var html = harness.RenderPartialAsync("Common/_LoginFooter").GetAwaiter().GetResult();

                var anchors = ParseFooterAnchors(html, termsUrl, privacyUrl, supportUrl);

                // Claim 1: exactly 5 anchors render — 2 inside the TermsNotice <p> and 3
                // utility links inside the <ul class="login-shell__footer-links">.
                var anchorCountIs5 = anchors.Count == 5;

                // Claim 2 + 3: every anchor's href matches the resolution rule:
                //   ResolveUrl(input) = "#" if string.IsNullOrWhiteSpace(input) else input verbatim.
                var allHrefsCorrect = anchors.All(a =>
                    string.Equals(a.Href, ExpectedHref(a.SourceUrl), StringComparison.Ordinal));

                // Claim 4: no anchor renders empty text. The partial sources link text from
                // localized resources, so a missing/blank text would indicate a regression.
                var allHaveText = anchors.All(a => !string.IsNullOrEmpty(a.Text));

                var ok = anchorCountIs5 && allHrefsCorrect && allHaveText;

                return ok.Label(
                    $"anchors={anchors.Count} " +
                    $"hrefs=[{string.Join(", ", anchors.Select(a => $"'{a.Href}' (expected '{ExpectedHref(a.SourceUrl)}')"))}] " +
                    $"texts=[{string.Join(", ", anchors.Select(a => $"'{a.Text}'"))}] " +
                    $"input=(terms='{Display(termsUrl)}', privacy='{Display(privacyUrl)}', support='{Display(supportUrl)}')");
            });
    }

    private sealed record FooterAnchor(string Href, string Text, string? SourceUrl);

    /// <summary>
    /// Parses the rendered footer HTML, extracting every anchor carrying the
    /// <c>login-shell__footer-link</c> class. The partial's render order is:
    /// (1) two anchors inside the TermsNotice paragraph (terms, privacy), then
    /// (2) three anchors in the utility list (terms, privacy, support). The positional
    /// mapping below mirrors that order so each anchor can be matched against its
    /// source URL.
    /// </summary>
    private static List<FooterAnchor> ParseFooterAnchors(
        string html, string? termsUrl, string? privacyUrl, string? supportUrl)
    {
        var parser = new HtmlParser();
        using var document = parser.ParseDocument(html);

        var anchors = document
            .QuerySelectorAll("a.login-shell__footer-link")
            .OfType<IHtmlAnchorElement>()
            .ToList();

        var sourceUrls = new[] { termsUrl, privacyUrl, termsUrl, privacyUrl, supportUrl };

        var result = new List<FooterAnchor>(anchors.Count);
        for (var i = 0; i < anchors.Count; i++)
        {
            var anchor = anchors[i];
            // GetAttribute returns the raw href (e.g. "#") rather than AngleSharp's
            // base-URI-resolved Href property which would absolutize relative links.
            var rawHref = anchor.GetAttribute("href") ?? string.Empty;
            var text = anchor.TextContent?.Trim() ?? string.Empty;
            var sourceUrl = i < sourceUrls.Length ? sourceUrls[i] : null;
            result.Add(new FooterAnchor(rawHref, text, sourceUrl));
        }
        return result;
    }

    private static string ExpectedHref(string? input)
        => string.IsNullOrWhiteSpace(input) ? "#" : input!;

    private static string Display(string? value)
        => value is null
            ? "<null>"
            : value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
}
