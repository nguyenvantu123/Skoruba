// Copyright (c) Jan Skoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.
//
// Feature: login-ui-redesign-i18n, Property 16: Localization manifest validator
// emits exactly one Warning per missing tuple and dedupes across invocations.
//
// Validates: Requirements 5.12
//
// Strategy
// --------
// LocalizationManifestValidator.ValidateAtStartup walks the static
// LocalizationManifest.Entries list (the canonical, unparameterised set of
// (ResourceType, Key) tuples) against every supplied culture and emits one
// Warning per missing tuple, deduping process-wide via a private static
// HashSet<(Type, string, string)> named "_warnedTuples".
//
// Property 16 phrases the postcondition over an arbitrary "Es" of entries and
// "Cs" of cultures, but the public API does not accept "Es" — the manifest is
// fixed by the validator. So this test fixes Es = LocalizationManifest.Entries
// and parameterises only Cs (1..3 well-known cultures from a fixed pool).
//
// Because _warnedTuples is process-wide, prior iterations would otherwise
// poison subsequent ones (the second iteration's missing tuples for vi/en/fr
// would already be present in the dedupe set, dropping the observed count to
// zero). To keep each FsCheck iteration deterministic we clear _warnedTuples
// via reflection at iteration start, under the validator's own _gate lock for
// thread safety. This pattern stays inside the unit-test boundary; the
// production class continues to expose no Reset method.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

using FsCheck.Xunit;

using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

using Skoruba.Duende.IdentityServer.STS.Identity.Helpers.Localization;

using Xunit;

namespace Skoruba.Duende.IdentityServer.STS.Identity.UnitTests.Helpers.Localization
{
    public sealed class LocalizationManifestValidator_Property16_Tests
    {
        // Feature: login-ui-redesign-i18n, Property 16: Localization manifest validator emits exactly one Warning per missing tuple and dedupes across invocations
        [Property(MaxTest = 50)]
        public void Validator_emits_one_warning_per_missing_tuple_and_dedupes_across_invocations(byte cultureSeed)
        {
            // Bound the culture count to 1..3 so each iteration is fast and the
            // expected warning count (Entries.Count * cultureCount) stays small.
            var cultures = BuildCultures(cultureSeed);

            // Clear the validator's process-wide dedupe set so the first
            // ValidateAtStartup call observes a clean slate. Without this,
            // earlier FsCheck iterations would have already added the same
            // (ResourceType, Key, Culture) tuples and the second LogWarning
            // for them would be suppressed, breaking the equality assertion.
            ResetWarnedTuples();

            var logger = new RecordingLogger();
            var services = new StubServiceProvider(new MissingKeyLocalizerFactory());

            // First invocation: every entry is "missing" because the stub
            // localizer reports ResourceNotFound = true for every key, so the
            // validator must log Entries.Count * cultures.Count warnings,
            // all distinct on (ResourceType, Key, Culture).
            LocalizationManifestValidator.ValidateAtStartup(services, cultures, logger);

            var firstSnapshot = logger.WarningRecords;
            var expectedFirstCount = LocalizationManifest.Entries.Count * cultures.Length;

            Assert.Equal(expectedFirstCount, firstSnapshot.Count);
            Assert.Equal(firstSnapshot.Count, firstSnapshot.Distinct().Count());

            // Second invocation against the same fixture: every tuple is
            // already present in _warnedTuples, so EmitWarningOnce must
            // short-circuit and add zero further log records.
            LocalizationManifestValidator.ValidateAtStartup(services, cultures, logger);

            var secondSnapshot = logger.WarningRecords;
            Assert.Equal(firstSnapshot.Count, secondSnapshot.Count);
        }

        private static CultureInfo[] BuildCultures(byte seed)
        {
            // Pick a deterministic non-empty subset of 1..3 cultures from a
            // small, well-known pool. Using fixed BCP-47 codes avoids the
            // platform-specific "GetCultureInfo throws on synthetic codes"
            // pitfall while still exercising the multi-culture loop.
            var pool = new[] { "vi", "en", "fr" };
            var count = (seed % pool.Length) + 1;
            return pool.Take(count).Select(CultureInfo.GetCultureInfo).ToArray();
        }

        private static void ResetWarnedTuples()
        {
            var validatorType = typeof(LocalizationManifestValidator);
            var setField = validatorType.GetField("_warnedTuples", BindingFlags.Static | BindingFlags.NonPublic);
            var gateField = validatorType.GetField("_gate", BindingFlags.Static | BindingFlags.NonPublic);
            if (setField is null || gateField is null)
            {
                return;
            }

            var hashSet = setField.GetValue(null);
            var gate = gateField.GetValue(null);
            if (hashSet is null || gate is null)
            {
                return;
            }

            var clearMethod = hashSet.GetType().GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance);
            if (clearMethod is null)
            {
                return;
            }

            // Hold the validator's own gate so the clear is observable to
            // subsequent EmitWarningOnce calls without races.
            lock (gate)
            {
                clearMethod.Invoke(hashSet, parameters: null);
            }
        }

        private sealed class StubServiceProvider : IServiceProvider
        {
            private readonly IStringLocalizerFactory _factory;

            public StubServiceProvider(IStringLocalizerFactory factory)
            {
                _factory = factory;
            }

            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(IStringLocalizerFactory))
                {
                    return _factory;
                }
                return null;
            }
        }

        private sealed class MissingKeyLocalizerFactory : IStringLocalizerFactory
        {
            public IStringLocalizer Create(Type resourceSource) => new MissingKeyLocalizer();

            public IStringLocalizer Create(string baseName, string location) => new MissingKeyLocalizer();
        }

        private sealed class MissingKeyLocalizer : IStringLocalizer
        {
            public LocalizedString this[string name]
                => new LocalizedString(name, name, resourceNotFound: true);

            public LocalizedString this[string name, params object[] arguments]
                => new LocalizedString(name, name, resourceNotFound: true);

            public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
                => Array.Empty<LocalizedString>();
        }

        private sealed class RecordingLogger : ILogger
        {
            private readonly object _lock = new();
            private readonly List<(string ResourceType, string Key, string Culture)> _records = new();

            public IReadOnlyList<(string ResourceType, string Key, string Culture)> WarningRecords
            {
                get
                {
                    lock (_lock)
                    {
                        return _records.ToArray();
                    }
                }
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel != LogLevel.Warning)
                {
                    return;
                }

                // Microsoft.Extensions.Logging's structured-log state implements
                // IReadOnlyList<KeyValuePair<string, object?>>; extract the
                // three named placeholders the validator passes to LogWarning.
                var resourceType = string.Empty;
                var key = string.Empty;
                var culture = string.Empty;

                if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
                {
                    foreach (var kvp in values)
                    {
                        switch (kvp.Key)
                        {
                            case "ResourceType":
                                resourceType = kvp.Value?.ToString() ?? string.Empty;
                                break;
                            case "Key":
                                key = kvp.Value?.ToString() ?? string.Empty;
                                break;
                            case "Culture":
                                culture = kvp.Value?.ToString() ?? string.Empty;
                                break;
                        }
                    }
                }

                lock (_lock)
                {
                    _records.Add((resourceType, key, culture));
                }
            }
        }
    }
}
