// Copyright (c) Jan Skoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.
//
// Tests for OidcUiLocalesRequestCultureProvider — the OIDC `ui_locales`
// authorization parameter -> ASP.NET Core RequestLocalization bridge.
//
// Two complementary surfaces are exercised:
//   1. Example-based [Theory] tests pinning specific OIDC scenarios
//      (single tag, multi-tag preference order, region-fallback, mismatch,
//       case-insensitive, malformed tag, query+form sourcing).
//   2. A property-based test asserting that for ANY space-separated tag list
//      drawn from a fixed pool of known cultures plus an optional malformed
//      tag, the provider always returns the FIRST tag whose name is in the
//      host's SupportedUICultures, OR null when no tag matches.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Skoruba.Duende.IdentityServer.STS.Identity.Helpers.Localization;

using Xunit;

namespace Skoruba.Duende.IdentityServer.STS.Identity.UnitTests.Helpers.Localization;

public sealed class OidcUiLocalesRequestCultureProvider_Tests
{
    private static readonly string[] SupportedPool = { "vi", "en" };

    private static HttpContext BuildContext(
        string? queryValue = null,
        string? formValue = null,
        IEnumerable<string>? supportedUiCultures = null)
    {
        var services = new ServiceCollection();
        var options = new RequestLocalizationOptions
        {
            SupportedUICultures = (supportedUiCultures ?? SupportedPool)
                .Select(CultureInfo.GetCultureInfo)
                .ToList(),
            SupportedCultures = (supportedUiCultures ?? SupportedPool)
                .Select(CultureInfo.GetCultureInfo)
                .ToList(),
        };
        services.AddSingleton<IOptions<RequestLocalizationOptions>>(new OptionsWrapper<RequestLocalizationOptions>(options));
        var sp = services.BuildServiceProvider();

        var ctx = new DefaultHttpContext { RequestServices = sp };
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/connect/authorize";

        if (queryValue is not null)
        {
            ctx.Request.QueryString = new QueryString("?ui_locales=" + Uri.EscapeDataString(queryValue));
        }

        if (formValue is not null)
        {
            ctx.Request.Method = "POST";
            ctx.Request.ContentType = "application/x-www-form-urlencoded";
            var formBody = "ui_locales=" + Uri.EscapeDataString(formValue);
            var bytes = Encoding.UTF8.GetBytes(formBody);
            ctx.Request.Body = new MemoryStream(bytes);
            ctx.Request.ContentLength = bytes.Length;
        }

        return ctx;
    }

    [Theory]
    [InlineData("vi", "vi")]
    [InlineData("en", "en")]
    [InlineData("VI", "vi")]                    // case-insensitive match
    [InlineData("vi-VN", "vi")]                 // RFC 4647 truncate-on-mismatch fallback
    [InlineData("zh-Hant fr en vi", "en")]      // first matchable wins
    [InlineData("fr de", null)]                 // none match, fall through
    [InlineData("", null)]                       // empty -> fall through
    [InlineData("   ", null)]                    // whitespace -> fall through
    [InlineData("not-a-real-tag", null)]         // unparseable -> fall through
    public async Task Query_ui_locales_resolves_first_supported_or_null(string raw, string? expected)
    {
        var provider = new OidcUiLocalesRequestCultureProvider();
        var ctx = BuildContext(queryValue: raw);

        var result = await provider.DetermineProviderCultureResult(ctx);

        if (expected is null)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.NotNull(result);
            Assert.Equal(expected, result!.Cultures.Single().Value);
            Assert.Equal(expected, result.UICultures.Single().Value);
        }
    }

    [Fact]
    public async Task Form_post_ui_locales_is_read_when_query_is_absent()
    {
        var provider = new OidcUiLocalesRequestCultureProvider();
        var ctx = BuildContext(formValue: "vi en");

        var result = await provider.DetermineProviderCultureResult(ctx);

        Assert.NotNull(result);
        Assert.Equal("vi", result!.UICultures.Single().Value);
    }

    [Fact]
    public async Task Query_takes_precedence_over_form_when_both_present()
    {
        var provider = new OidcUiLocalesRequestCultureProvider();
        var ctx = BuildContext(queryValue: "en", formValue: "vi");

        var result = await provider.DetermineProviderCultureResult(ctx);

        Assert.NotNull(result);
        Assert.Equal("en", result!.UICultures.Single().Value);
    }

    [Fact]
    public async Task Returns_null_when_supported_cultures_list_is_empty()
    {
        var provider = new OidcUiLocalesRequestCultureProvider();
        var ctx = BuildContext(queryValue: "vi", supportedUiCultures: Array.Empty<string>());

        var result = await provider.DetermineProviderCultureResult(ctx);

        Assert.Null(result);
    }

    // Property: for any preference list drawn from the known pool plus an optional
    // malformed tag, the resolved culture is the first pool-member tag in the list,
    // or null when no pool-member appears. The property is robust against:
    //   * Tag ordering (first-match-wins).
    //   * Malformed tags interleaved between valid ones (the malformed tag is
    //     skipped and the next valid tag is considered).
    //   * Empty lists / lists containing only malformed tags (null).
    [Property(MaxTest = 100)]
    // Feature: login-ui-redesign-i18n, OIDC ui_locales bridge — first supported tag wins
    public Property First_supported_tag_in_preference_list_is_resolved_or_null()
    {
        var poolTagGen = Gen.Elements("vi", "en", "fr", "de", "ja", "zz-XX");
        var listGen =
            from len in Gen.Choose(0, 6)
            from tags in poolTagGen.ListOf(len)
            select tags.ToList();

        return Prop.ForAll(
            listGen.ToArbitrary(),
            tags =>
            {
                var raw = string.Join(' ', tags);

                // Compute expected result independent of the implementation.
                string? expected = null;
                foreach (var tag in tags)
                {
                    if (tag == "vi") { expected = "vi"; break; }
                    if (tag == "en") { expected = "en"; break; }
                    // Other tags are either unsupported (fr, de, ja) or malformed (zz-XX).
                    // None match the host's SupportedUICultures = { "vi", "en" }, so we
                    // continue scanning for a possible later vi/en hit.
                }

                var provider = new OidcUiLocalesRequestCultureProvider();
                var ctx = BuildContext(queryValue: raw);
                var result = provider.DetermineProviderCultureResult(ctx).GetAwaiter().GetResult();

                if (expected is null)
                {
                    return (result is null).Label(
                        $"tags=[{raw}] expected=null actual={result?.UICultures.Single().Value ?? "<null>"}");
                }

                var actual = result?.UICultures.Single().Value;
                return string.Equals(actual, expected, StringComparison.Ordinal).Label(
                    $"tags=[{raw}] expected={expected} actual={actual ?? "<null>"}");
            });
    }
}
