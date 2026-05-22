// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.AspNetCore.Http;
using Skoruba.Duende.IdentityServer.STS.Identity.UnitTests.TestInfrastructure;
using TenantInfrastructure.Abstractions;

namespace Skoruba.Duende.IdentityServer.STS.Identity.UnitTests.Views.Common;

// Feature: login-ui-redesign-i18n, Property 8: Tenant pill aria-label contains both label and host
//
// For any non-empty, URL-safe ASCII host string and an active TenantContext, rendering
// Views/Shared/Common/_LoginTenantPill.cshtml MUST:
//   * Render a <span class="login-shell__tenant-pill"> element exactly once.
//   * Set its aria-label attribute to a non-empty string that contains BOTH the localized
//     label "Current tenant" AND the generated host string verbatim, separated by at
//     least one whitespace character (in either order).
//
// The harness's RazorRenderHost.LocalizerStore is pre-seeded with
// Login.TenantPillLabel = "Current tenant" and Login.TenantPillAriaLabel = "Current tenant".
// FakeTenantContextAccessor.Set(...) is used to provide a non-null TenantContext so the
// pill renders (the partial short-circuits when Current is null).
//
// Validates: Requirements 1.5, 8.5
public class LoginTenantPill_Property8_Tests
{
    private const string ExpectedLabel = "Current tenant";

    /// <summary>
    /// Generator producing valid DNS-style host strings that round-trip verbatim
    /// through ASP.NET Core's <see cref="HostString"/> and its underlying
    /// <c>System.Globalization.IdnMapping</c>.
    /// <para>
    /// When ASP.NET Core renders <c>HostString.ToUriComponent()</c> during view
    /// rendering, it routes the host through <c>IdnMapping.GetAscii</c> for any
    /// non-ASCII / structurally-suspect input. RFC 1035 / IDN structural rules
    /// reject hosts that:
    /// <list type="bullet">
    /// <item>start or end with a hyphen (<c>-</c>),</item>
    /// <item>contain consecutive dots (<c>..</c>),</item>
    /// <item>start or end with a dot,</item>
    /// <item>contain underscores (legal in <c>Host</c> headers but interact poorly
    /// with the IDN path).</item>
    /// </list>
    /// To keep this property focused on the partial's "label + host" contract
    /// (rather than the framework's IDN behavior) the generator emits 1..3
    /// labels joined by a single <c>.</c>, where each label:
    /// <list type="bullet">
    /// <item>is 1..20 characters drawn from <c>[A-Za-z0-9-]</c>,</item>
    /// <item>does not start or end with a hyphen (first/last char is alphanumeric).</item>
    /// </list>
    /// Arbitrary <c>System.String</c> draws are avoided for the same reason as
    /// Property 7 (HTML attribute-value normalization).
    /// </para>
    /// </summary>
    private static Gen<string> HostGenerator()
    {
        const string alphaNumChars =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        const string alphaNumHyphenChars = alphaNumChars + "-";

        var alphaNum = Gen.Elements(alphaNumChars.ToCharArray());
        var alphaNumHyphen = Gen.Elements(alphaNumHyphenChars.ToCharArray());

        // A single DNS label: 1..20 chars, first and last alphanumeric, interior
        // chars may include hyphens. RFC 1035 caps labels at 63 chars; 20 is well
        // within that bound and keeps generated cases small for shrinking.
        var label =
            from len in Gen.Choose(1, 20)
            from first in alphaNum
            from middle in alphaNumHyphen.ListOf(Math.Max(0, len - 2))
            from last in alphaNum
            select len == 1
                ? first.ToString()
                : len == 2
                    ? new string(new[] { first, last })
                    : first + new string(middle.ToArray()) + last;

        // 1..3 labels joined by '.'. Bounded list size keeps the total host length
        // safely under DNS limits.
        return
            from count in Gen.Choose(1, 3)
            from list in label.ListOf(count)
            select string.Join('.', list);
    }

    [Property(MaxTest = 100)]
    // Feature: login-ui-redesign-i18n, Property 8: Tenant pill aria-label contains both label and host
    public Property TenantPill_AriaLabel_Contains_Both_Label_And_Host()
    {
        return Prop.ForAll(
            HostGenerator().ToArbitrary(),
            host =>
            {
                using var harness = new RazorRenderHost();

                // Provide a non-null TenantContext so the partial does not short-circuit.
                // The pill text uses Context.Request.Host.Value, not the TenantContext
                // fields, but the partial gates rendering on Current != null.
                harness.TenantContextAccessor.Set(new TenantContext(
                    TenantKey: "tenant-test",
                    ConnectionSecrets: new Dictionary<string, string>(StringComparer.Ordinal)));

                var html = harness.RenderPartialAsync(
                    "Common/_LoginTenantPill",
                    requestHost: new HostString(host)).GetAwaiter().GetResult();

                var (rendered, ariaLabel) = ParsePill(html);

                // Claim 1: the pill renders.
                if (!rendered)
                {
                    return false.Label(
                        $"pill did not render for host='{Display(host)}', html='{Truncate(html)}'");
                }

                // Claim 2: aria-label is non-empty.
                if (string.IsNullOrEmpty(ariaLabel))
                {
                    return false.Label(
                        $"aria-label was null/empty for host='{Display(host)}', html='{Truncate(html)}'");
                }

                // Claim 3 + 4: aria-label contains BOTH the label and the host, separated
                // by at least one whitespace character (in either order). HostString
                // values that include a port render with a ':' separator (e.g.
                // "example.com:8443"); HostString.Value is byte-equal to the input we
                // assigned so we can search for the input verbatim.
                var labelIndex = ariaLabel.IndexOf(ExpectedLabel, StringComparison.Ordinal);
                var hostIndex = ariaLabel.IndexOf(host, StringComparison.Ordinal);

                if (labelIndex < 0 || hostIndex < 0)
                {
                    return false.Label(
                        $"aria-label='{ariaLabel}' missing label or host (labelIndex={labelIndex}, hostIndex={hostIndex}) " +
                        $"for host='{Display(host)}'");
                }

                // The label and host must not overlap; pick the segment between them and
                // require at least one whitespace character.
                var (firstEnd, secondStart) = labelIndex < hostIndex
                    ? (labelIndex + ExpectedLabel.Length, hostIndex)
                    : (hostIndex + host.Length, labelIndex);

                if (firstEnd > secondStart)
                {
                    return false.Label(
                        $"label and host overlap in aria-label='{ariaLabel}' for host='{Display(host)}'");
                }

                var separator = ariaLabel.Substring(firstEnd, secondStart - firstEnd);
                var hasWhitespaceSeparator = separator.Any(char.IsWhiteSpace);

                return hasWhitespaceSeparator.Label(
                    $"aria-label='{ariaLabel}' separator='{Display(separator)}' " +
                    $"label='{ExpectedLabel}' host='{Display(host)}'");
            });
    }

    private static (bool Rendered, string AriaLabel) ParsePill(string html)
    {
        var parser = new HtmlParser();
        using var document = parser.ParseDocument(html);

        var pill = document.QuerySelector("span.login-shell__tenant-pill");
        if (pill is null)
        {
            return (false, string.Empty);
        }

        var ariaLabel = pill.GetAttribute("aria-label") ?? string.Empty;
        return (true, ariaLabel);
    }

    private static string Display(string? value)
        => value is null
            ? "<null>"
            : value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    private static string Truncate(string value)
        => value.Length <= 200 ? value : value.Substring(0, 200) + "...";
}
