// Copyright (c) Jan Skoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.
//
// Feature: login-ui-redesign-i18n, Task 11.4 — Property 15
// Validates: Requirements 5.1, 5.2, 5.3, 5.4, 5.5, 5.6, 5.7, 5.8, 5.10, 5.11
//
// ----------------------------------------------------------------------------
// This file does NOT need a full WebApplicationFactory<Program>. The STS host
// resolves view/controller resx files via the default
// `ResourceManagerStringLocalizerFactory`, which picks resources up from the
// embedded `Resources\**\*.resx` glob compiled into the production STS host
// assembly. We can therefore drive the same factory from a tiny
// `ServiceCollection` + `AddLocalization` + `AddViewLocalization` graph, as
// long as we point ResourcesPath at "Resources" (matching
// `ConfigurationConsts.ResourcesPath`) and we pass marker types whose
// FullName matches the canonical view path (the production manifest already
// declares those marker types — see
// `Helpers/Localization/LocalizationManifestValidator.cs`).
//
// Property 15 is therefore implemented as two `[Theory]` cases per
// supported UI culture (vi, en) iterating every entry in
// `LocalizationManifest.Entries`. For each tuple it asserts:
//   1. The IStringLocalizer reports `ResourceNotFound == false`.
//   2. The localized `Value` is different from the bare key (defense in depth
//      — a missing key would fall back to `Value = Name = key`).
//
// Adding a third culture later (Requirement 7.6) requires just adding a new
// `[InlineData]` row plus the matching `.resx` file — no test code change.
// ----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

using Skoruba.Duende.IdentityServer.STS.Identity.Helpers.Localization;

using Xunit;
using Xunit.Abstractions;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.IntegrationTests.Tests
{
    /// <summary>
    /// **Property 15: Localization manifest covers every required key in every
    /// supported culture.**
    ///
    /// Resolves the real <see cref="IStringLocalizerFactory"/> against the STS
    /// host assembly's embedded resx files and asserts that every
    /// <c>(Entry, CultureInfo)</c> pair from
    /// <c>LocalizationManifest.Entries × { vi, en }</c> produces a found
    /// localized string with a value distinct from the lookup key.
    /// </summary>
    public sealed class LocalizationManifest_Property15_Tests
    {
        private const string ResourcesPath = "Resources";

        private readonly ITestOutputHelper _output;

        public LocalizationManifest_Property15_Tests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Theory]
        [InlineData("en")]
        [InlineData("vi")]
        public void Manifest_covers_every_required_key_for_supported_culture(string cultureName)
        {
            // Build a minimal service graph that hosts the same factory the
            // production STS host uses. AddLocalization wires up the default
            // `ResourceManagerStringLocalizerFactory`, which is enough to
            // resolve the `Resources\**\*.resx` files embedded in the STS host
            // assembly via its default ApplicationPart. The factory has a
            // hard dependency on `ILoggerFactory`, so AddLogging() is required
            // even for an otherwise dependency-free unit test.
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddLocalization(opts => opts.ResourcesPath = ResourcesPath);

            using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IStringLocalizerFactory>();

            var culture = CultureInfo.GetCultureInfo(cultureName);

            // Switch the ambient culture so the .NET ResourceManager picks the
            // matching `.{culture}.resx` for every lookup. The validator
            // production code does the same thing in `ValidateAtStartup`.
            var previousUiCulture = CultureInfo.CurrentUICulture;
            var previousCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentUICulture = culture;
                CultureInfo.CurrentCulture = culture;

                var failures = new List<string>();

                foreach (var entry in LocalizationManifest.Entries)
                {
                    var localizer = factory.Create(entry.ResourceType);
                    var localized = localizer[entry.Key];

                    if (localized.ResourceNotFound)
                    {
                        failures.Add(
                            $"  - ResourceNotFound for ({entry.ResourceType.FullName}, '{entry.Key}', '{cultureName}')");
                        continue;
                    }

                    // Defence in depth — when a key is missing, the default
                    // string-localizer behaviour falls back to `Value = Name`.
                    // Because real resx values must differ from the key for
                    // every login-redesign entry, equality here is also a bug.
                    if (string.Equals(localized.Value, entry.Key, StringComparison.Ordinal))
                    {
                        failures.Add(
                            $"  - Value equals Key for ({entry.ResourceType.FullName}, '{entry.Key}', '{cultureName}'). Resx fallback suspected.");
                    }
                }

                if (failures.Count > 0)
                {
                    var message =
                        $"Localization manifest coverage gaps for culture '{cultureName}':"
                        + Environment.NewLine
                        + string.Join(Environment.NewLine, failures);

                    _output.WriteLine(message);
                    failures.Should().BeEmpty(message);
                }
            }
            finally
            {
                CultureInfo.CurrentUICulture = previousUiCulture;
                CultureInfo.CurrentCulture = previousCulture;
            }
        }

        /// <summary>
        /// Sanity check: the manifest itself must be non-empty and free of
        /// duplicates so the iteration above is meaningful.
        /// </summary>
        [Fact]
        public void Manifest_entries_are_non_empty_and_unique()
        {
            LocalizationManifest.Entries.Should().NotBeEmpty();

            var distinct = LocalizationManifest.Entries
                .Select(e => (e.ResourceType, e.Key))
                .Distinct()
                .Count();

            distinct.Should().Be(LocalizationManifest.Entries.Count,
                "every (ResourceType, Key) tuple in the manifest must be unique");
        }
    }
}
