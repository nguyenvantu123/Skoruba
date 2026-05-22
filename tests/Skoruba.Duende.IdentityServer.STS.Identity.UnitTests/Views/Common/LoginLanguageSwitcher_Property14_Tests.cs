// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.AspNetCore.Localization;
using Skoruba.Duende.IdentityServer.STS.Identity.Models.Login;
using Skoruba.Duende.IdentityServer.STS.Identity.UnitTests.TestInfrastructure;

namespace Skoruba.Duende.IdentityServer.STS.Identity.UnitTests.Views.Common;

// Feature: login-ui-redesign-i18n, Property 14: Language switcher hides itself when fewer
// than two cultures are configured.
//
// For any list of N CultureInfos drawn from a fixed pool of well-known cultures with N
// in [0, 6], rendering Views/Shared/Common/_LoginLanguageSwitcher.cshtml MUST:
//   * When N < 2: produce HTML containing NO <form id="selectLanguageForm"> AND
//     NO <select> element. The Razor partial uses an early `return;` so the rendered
//     output is effectively empty.
//   * When N >= 2: produce HTML containing exactly one <form id="selectLanguageForm">.
//
// Validates: Requirements 6.8
public sealed class LoginLanguageSwitcher_Property14_Tests
{
    /// <summary>
    /// Fixed pool of well-known cultures the generator draws from. Six entries is enough
    /// to satisfy the [0, 6] size range while keeping the property's worst-case render cost
    /// to N=6 cultures per iteration.
    /// </summary>
    private static readonly string[] CulturePool =
    {
        "en", "vi", "fr", "de", "es", "ja"
    };

    /// <summary>
    /// Generator for an ordered, distinct subset of <see cref="CulturePool"/> with size in
    /// [0, 6] — exercising every branch:
    ///   * N = 0 — empty list, hide branch.
    ///   * N = 1 — single culture, hide branch.
    ///   * N in [2, 6] — show branch.
    /// </summary>
    private static Gen<List<CultureInfo>> CulturesGen()
    {
        return
            from size in Gen.Choose(0, CulturePool.Length)
            from order in Gen.Choose(0, int.MaxValue).ListOf(CulturePool.Length)
            let pairs = CulturePool.Zip(order, (code, k) => (code, k))
            let sorted = pairs.OrderBy(p => p.k).Select(p => p.code).Take(size)
            select sorted.Select(CultureInfo.GetCultureInfo).ToList();
    }

    [Property(MaxTest = 100)]
    // Feature: login-ui-redesign-i18n, Property 14: Language switcher hides itself when fewer than two cultures are configured
    public Property Switcher_renders_empty_when_fewer_than_two_cultures_configured()
    {
        return Prop.ForAll(
            CulturesGen().ToArbitrary(),
            cultures =>
            {
                using var harness = new RazorRenderHost();
                harness.LocalizationOptions.SetSupportedUICultures(cultures);

                // When N == 0 there is no current culture to surface. When N >= 1, picking
                // the first element keeps the request culture inside the supplied set so the
                // show branch isn't accidentally polluted by a non-matching culture.
                var requestCulture = cultures.Count >= 1
                    ? new RequestCulture(cultures[0], cultures[0])
                    : new RequestCulture(CultureInfo.InvariantCulture, CultureInfo.InvariantCulture);

                var html = harness.RenderPartialAsync(
                        "Common/_LoginLanguageSwitcher",
                        model: new LoginShellHeaderModel
                        {
                            CurrentPath = "/Account/Login",
                            CurrentQuery = string.Empty,
                        },
                        requestPath: "/Account/Login",
                        requestQuery: string.Empty,
                        requestCulture: requestCulture)
                    .GetAwaiter().GetResult();

                var parser = new HtmlParser();
                using var document = parser.ParseDocument(html);

                var formCount = document
                    .QuerySelectorAll("form#selectLanguageForm")
                    .Count();
                // Defense in depth: assert no <select> at all in the hide branch. The partial
                // could in principle emit a stray <select> outside the form, and the
                // requirement is "renders nothing", not just "no form".
                var selectCount = document
                    .QuerySelectorAll("select")
                    .Count();

                bool ok;
                if (cultures.Count < 2)
                {
                    // Hide branch: zero forms, zero selects.
                    ok = formCount == 0 && selectCount == 0;
                }
                else
                {
                    // Show branch: exactly one form. We don't constrain the select count
                    // beyond "non-zero" here because Property 12 already pins it to 1; this
                    // test focuses on the on/off behaviour described by Requirement 6.8.
                    ok = formCount == 1;
                }

                return ok.Label(
                    $"N={cultures.Count} " +
                    $"cultures=[{string.Join(",", cultures.Select(c => c.Name))}] " +
                    $"forms={formCount} selects={selectCount}");
            });
    }
}
