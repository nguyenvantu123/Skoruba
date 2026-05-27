// Feature: tenant-client-cache-public-read, Task 12
//
// Reflection-based security regression tests that pin invariants the
// runbook and the design.md "Security Model" table promise. Each test
// here corresponds to a row of the Task 12 review checklist; together
// they reinforce P16 (metric tag policy) and P18 (Public_Safe_Fields
// whitelist) and add structural guards that no future refactor can
// silently break.
//
//   * Controller_Has_No_DbContext_Or_IClientService_Or_IClientRepository_In_Constructor
//       Validates: Requirements 2.7, 12.10
//   * PublicClientSnapshot_Has_No_Forbidden_Field_Names
//       Validates: Requirements 12.7 (P18 reinforcement)
//   * Controller_DoesNotExposeEnvelope_Type_In_Response_Schema
//       Validates: Requirements 2.5, 12.7
//   * Cors_Default_Allowlist_Empty_Implies_No_AllowOrigin_Echoed
//       Validates: Requirements 5.1, 5.4
//   * RateLimiter_Counter_Tag_Policy_Excludes_TenantKey_For_Unauthorized_BadRequest
//       Validates: Requirements 8.4 (P16 reinforcement)
//   * ApiKeyStore_Holds_Only_Sha256_Hex_Strings
//       Validates: Requirements 1.4, 9.5

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using FluentAssertions;

using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Controllers;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Helpers;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.PublicTenantClients;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache.Helpers;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.PublicTenantClients;

[Collection(PublicReadMetricCollection.Name)]
public sealed class SecurityRegressionTests
{
    // ===== 1. Controller dependency surface =================================

    /// <summary>
    /// R2.7, R12.10. The public-read controller MUST depend only on
    /// <see cref="ITenantClientCacheService"/> and a small set of
    /// observability helpers. It MUST NOT take a constructor parameter
    /// whose type is (or implements / derives from) any of:
    /// <list type="bullet">
    ///   <item><description><c>Microsoft.EntityFrameworkCore.DbContext</c></description></item>
    ///   <item><description><c>IClientService</c> (Admin BusinessLogic)</description></item>
    ///   <item><description><c>IClientRepository</c> (Admin EntityFramework)</description></item>
    ///   <item><description><c>IAdminConfigurationDbContext</c> (Admin EntityFramework)</description></item>
    ///   <item><description>Any type whose name matches the regex
    ///         <c>(?i).*ClientService.*</c> or <c>(?i).*ClientRepository.*</c>
    ///         or ends with <c>DbContext</c> — defensive against future
    ///         renames that route around the explicit type list.</description></item>
    /// </list>
    /// </summary>
    [Fact]
    public void Controller_Has_No_DbContext_Or_IClientService_Or_IClientRepository_In_Constructor()
    {
        var ctor = typeof(PublicTenantClientsController)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public)
            .Single("a single public constructor is the wired DI entry point");

        var paramTypes = ctor
            .GetParameters()
            .Select(p => p.ParameterType)
            .ToArray();

        // Allow-list of dependency types the controller is permitted to
        // take. We assert positively first so a future refactor that
        // removes a benign dependency does not silently widen the
        // allow-list — it surfaces here as a missing entry that the
        // reviewer must accept explicitly.
        var allowed = new[]
        {
            typeof(ITenantClientCacheService),
            typeof(Microsoft.Extensions.Options.IOptionsMonitor<TenantClientCachePublicReadOptions>),
            typeof(TenantClientCacheMetrics),
            typeof(Microsoft.Extensions.Logging.ILogger<PublicTenantClientsController>),
            typeof(IpHashHelper),
        };
        paramTypes.Should().BeEquivalentTo(allowed,
            "the public-read controller's dependency surface is locked by "
            + "design (R2.7, R12.10) — see SecurityRegressionTests for the rationale");

        // Defensive: walk the type closure of every parameter (the type
        // itself plus every interface and base type it implements /
        // inherits) and assert no entry matches the forbidden patterns.
        // This catches the case where a benign-looking type secretly
        // pulls in a forbidden interface via a base class.
        var forbiddenExactTypeNames = new[]
        {
            "DbContext",
            "IClientService",
            "IClientRepository",
            "IAdminConfigurationDbContext",
        };

        var forbiddenPatterns = new[]
        {
            new Regex(@"(?i)^.*ClientService.*$"),
            new Regex(@"(?i)^.*ClientRepository.*$"),
            new Regex(@"(?i)^.*DbContext$"),
        };

        foreach (var param in ctor.GetParameters())
        {
            foreach (var t in TypeClosure(param.ParameterType))
            {
                forbiddenExactTypeNames.Should().NotContain(t.Name,
                    $"ctor param '{param.Name}' (type {param.ParameterType.FullName}) "
                    + $"transitively depends on {t.FullName} which is forbidden by R2.7 / R12.10");

                foreach (var pattern in forbiddenPatterns)
                {
                    pattern.IsMatch(t.Name).Should().BeFalse(
                        $"ctor param '{param.Name}' (type {param.ParameterType.FullName}) "
                        + $"transitively depends on {t.FullName} which matches the "
                        + $"forbidden pattern {pattern} (R2.7 / R12.10)");
                }
            }
        }
    }

    // ===== 2. PublicClientSnapshot field-name whitelist =====================

    /// <summary>
    /// R12.7 + P18 reinforcement. Every property on
    /// <see cref="Skoruba.Duende.IdentityServer.TenantClientCache.Client.Models.PublicClientSnapshot"/>
    /// MUST NOT match any of the forbidden regexes:
    /// <c>clientSecrets</c>, <c>claims</c>, <c>properties</c>,
    /// <c>identityProviderRestrictions</c>, <c>pairWiseSubjectSalt</c>,
    /// <c>id</c>, <c>(?i).*secret.*</c>. The exact-match list is checked
    /// against both the C# property name and the JSON property name so a
    /// future refactor that renames the C# property but leaves the JSON
    /// name behind cannot smuggle a forbidden name into the wire format.
    /// </summary>
    [Fact]
    public void PublicClientSnapshot_Has_No_Forbidden_Field_Names()
    {
        var snapshotType = typeof(
            Skoruba.Duende.IdentityServer.TenantClientCache.Client.Models.PublicClientSnapshot);

        var properties = snapshotType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToArray();

        properties.Should().NotBeEmpty(
            "PublicClientSnapshot is the public-read DTO and MUST surface the Public_Safe_Fields whitelist");

        var forbiddenExact = new[]
        {
            "clientSecrets",
            "claims",
            "properties",
            "identityProviderRestrictions",
            "pairWiseSubjectSalt",
            // Note: a single-character `id` field is forbidden too — the
            // database primary key MUST NEVER appear on the wire. The
            // canonical wire field is `clientId` (Duende identity), not
            // `id` (database PK).
            "id",
        };

        // Broad pattern requested by Task 12: `(?i).*secret.*`. This
        // pattern would, on its own, catch `RequireClientSecret` —
        // a LEGITIMATE Public_Safe_Field boolean toggle (it advertises
        // whether a client secret is required to call the token endpoint;
        // the toggle is NOT a secret value). The narrowed P18 test in
        // `PublicClientSnapshotProperties` uses `secrets$|password|pairWise`
        // for that reason. We keep the broad pattern here AND carve out
        // the documented whitelist exemption so a future field whose
        // name actually leaks secret material (`clientSecretValue`,
        // `accessSecret`, `apiSecret`, ...) trips the guard while
        // `requireClientSecret` continues to pass.
        var forbiddenSecretRegex = new Regex(@"(?i).*secret.*");
        var allowedSecretToggleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "RequireClientSecret",
            "requireClientSecret",
        };

        foreach (var prop in properties)
        {
            // C# property name check.
            foreach (var bad in forbiddenExact)
            {
                string.Equals(prop.Name, bad, StringComparison.OrdinalIgnoreCase)
                    .Should().BeFalse(
                        $"PublicClientSnapshot property '{prop.Name}' matches forbidden field name '{bad}' (R12.7)");
            }

            var nameMatchesSecret = forbiddenSecretRegex.IsMatch(prop.Name);
            var nameIsAllowedToggle = allowedSecretToggleNames.Contains(prop.Name);
            (nameMatchesSecret && !nameIsAllowedToggle).Should().BeFalse(
                $"PublicClientSnapshot property '{prop.Name}' matches forbidden regex "
                + "(?i).*secret.* and is not the documented `RequireClientSecret` toggle exemption (R12.7)");

            // JsonPropertyName check — covers the wire shape.
            var jsonName = prop
                .GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()
                ?.Name;
            if (jsonName is null)
            {
                continue;
            }

            foreach (var bad in forbiddenExact)
            {
                string.Equals(jsonName, bad, StringComparison.OrdinalIgnoreCase)
                    .Should().BeFalse(
                        $"PublicClientSnapshot property '{prop.Name}' has JsonPropertyName "
                        + $"'{jsonName}' which matches forbidden field name '{bad}' (R12.7)");
            }

            var jsonNameMatchesSecret = forbiddenSecretRegex.IsMatch(jsonName);
            var jsonNameIsAllowedToggle = allowedSecretToggleNames.Contains(jsonName);
            (jsonNameMatchesSecret && !jsonNameIsAllowedToggle).Should().BeFalse(
                $"PublicClientSnapshot property '{prop.Name}' has JsonPropertyName "
                + $"'{jsonName}' which matches forbidden regex (?i).*secret.* "
                + "and is not the documented `requireClientSecret` toggle exemption (R12.7)");
        }
    }

    // ===== 3. Controller does not expose the envelope type =================

    /// <summary>
    /// R2.5 + R12.7. The controller action's return type is
    /// <see cref="IActionResult"/> — a polymorphic surface. The contract
    /// promises the wire shape is the <c>PublicClientSnapshot</c>
    /// (whitelisted fields), NOT the parent-spec
    /// <see cref="ClientCacheSnapshotEnvelope"/>. We pin two structural
    /// invariants here:
    /// <list type="number">
    ///   <item><description>The controller does not declare a
    ///         <c>[ProducesResponseType]</c> attribute that names
    ///         <see cref="ClientCacheSnapshotEnvelope"/> as the success
    ///         body type.</description></item>
    ///   <item><description>No constructor parameter, no public
    ///         property, no public method on the controller exposes
    ///         <see cref="ClientCacheSnapshotEnvelope"/> via its public
    ///         signature — the envelope type is a private implementation
    ///         detail consumed by the controller body and never named on
    ///         the OpenAPI surface.</description></item>
    /// </list>
    /// </summary>
    [Fact]
    public void Controller_DoesNotExposeEnvelope_Type_In_Response_Schema()
    {
        var controllerType = typeof(PublicTenantClientsController);

        // (1) ProducesResponseType / Produces should never name the envelope.
        var responseTypeAttrs = controllerType
            .GetCustomAttributes(inherit: true)
            .Concat(controllerType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .SelectMany(m => m.GetCustomAttributes(inherit: true)))
            .Where(a => a is ProducesResponseTypeAttribute or ProducesAttribute);

        foreach (var attr in responseTypeAttrs)
        {
            // ProducesResponseType<T> exposes a `Type` property that
            // points to the response body type. ProducesAttribute exposes
            // a `Type` property too (when constructed with a type arg).
            var typeProp = attr.GetType().GetProperty(
                "Type",
                BindingFlags.Public | BindingFlags.Instance);
            var responseType = typeProp?.GetValue(attr) as Type;
            responseType.Should().NotBe(typeof(ClientCacheSnapshotEnvelope),
                "the public-read action MUST NOT expose ClientCacheSnapshotEnvelope on the OpenAPI surface (R2.5)");
        }

        // (2) Constructor params, public properties, and public method
        // signatures (return + parameter types) MUST NOT name the
        // envelope. The controller body uses it as a local variable —
        // that is fine — but it should never leak across the type's
        // public surface.
        var publicSurface = new List<Type?>();

        publicSurface.AddRange(controllerType
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(c => c.GetParameters().Select(p => p.ParameterType)));

        publicSurface.AddRange(controllerType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.PropertyType));

        publicSurface.AddRange(controllerType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(m => new[] { m.ReturnType }
                .Concat(m.GetParameters().Select(p => p.ParameterType))));

        publicSurface
            .Where(t => t is not null)
            .Should().NotContain(typeof(ClientCacheSnapshotEnvelope),
                "the public-read controller's PUBLIC surface (ctor / properties / methods) "
                + "MUST NOT name ClientCacheSnapshotEnvelope — the envelope is a private "
                + "implementation detail consumed inside GetAsync (R2.5, R12.7)");
    }

    // ===== 4. CORS empty allowlist =========================================

    /// <summary>
    /// R5.1 + R5.4. With the <c>Cors:AllowedOrigins</c> section absent
    /// (or empty) the named CORS policy resolved by the framework's
    /// <see cref="ICorsPolicyProvider"/> returns a policy with zero
    /// origins, no <c>AllowCredentials</c>, and the whitelisted methods
    /// <c>GET / HEAD / OPTIONS</c>. The browser CORS protocol rejects a
    /// preflight that does not match any allow-list entry, which is the
    /// canonical fail-closed behavior.
    /// </summary>
    [Fact]
    public async Task Cors_Default_Allowlist_Empty_Implies_No_AllowOrigin_Echoed()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Defaults match production. Cors:AllowedOrigins is
                // intentionally absent so the binder produces an empty
                // list (R5.4 fail-closed).
                ["TenantClientCachePublicRead:RateLimit:TokenLimit"] = "30",
                ["TenantClientCachePublicRead:RateLimit:TokensPerPeriod"] = "30",
                ["TenantClientCachePublicRead:RateLimit:ReplenishmentPeriod"] = "00:01:00",
                ["TenantClientCachePublicRead:RateLimit:QueueLimit"] = "0",
                ["TenantClientCachePublicRead:RateLimit:AutoReplenishment"] = "true",
                ["TenantClientCachePublicRead:Cors:PreflightMaxAgeSeconds"] = "600",
                ["TenantClientCachePublicRead:ResponseCache:MaxAgeSeconds"] = "60",
                ["TenantClientCachePublicRead:Audit:LogIpHash"] = "true",
                ["TenantClientCachePublicRead:Audit:RemoteIpSalt"] = string.Empty,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TenantClientCacheMetrics>();
        services.AddSingleton<IHostEnvironment>(new CorsTestHostEnvironment());
        services.AddTenantClientCachePublicRead(configuration);

        using var provider = services.BuildServiceProvider(validateScopes: true);
        var policyProvider = provider.GetRequiredService<ICorsPolicyProvider>();

        var policy = await policyProvider.GetPolicyAsync(
            new DefaultHttpContext(),
            StartupHelpers.PublicReadCorsPolicyName);

        policy.Should().NotBeNull();
        policy!.Origins.Should()
            .BeEmpty("an absent / empty Cors:AllowedOrigins section MUST yield zero origins (R5.4)");
        policy.SupportsCredentials.Should()
            .BeFalse("the public-read policy MUST disallow credentials (R5.3)");
        policy.Methods.Should()
            .BeEquivalentTo(new[] { "GET", "HEAD", "OPTIONS" },
                "R5.2 — only safe verbs are allowed");
    }

    private sealed class CorsTestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "TestHost";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    // ===== 5. Metric tag policy reinforcement (R8.4 / P16) =================

    /// <summary>
    /// R8.4 (P16 reinforcement). Drive the metric helpers directly and
    /// capture every measurement via <see cref="RecordingMeterListener"/>.
    /// Assert the <c>tenantKey</c> tag is ABSENT from the
    /// <c>tenant_client_cache.public_read.unauthorized</c> and
    /// <c>tenant_client_cache.public_read.bad_request</c> counters and
    /// PRESENT on every other public-read counter. The duration histogram
    /// is checked the same way for the outcomes that record it.
    /// </summary>
    [Fact]
    public void RateLimiter_Counter_Tag_Policy_Excludes_TenantKey_For_Unauthorized_BadRequest()
    {
        using var metrics = new TenantClientCacheMetrics();
        using var listener = new RecordingMeterListener(TenantClientCacheMetrics.MeterName);

        // Drive every public-read helper exactly once. Use a fixed
        // tenantKey on the tagged outcomes so we can assert the tag
        // value, not just its presence.
        const string tenantKey = "acme";
        const double durationMs = 12.34;

        metrics.PublicReadHit(tenantKey, durationMs);
        metrics.PublicReadNotModified(tenantKey, durationMs);
        metrics.PublicReadMiss(tenantKey, durationMs);
        metrics.PublicReadRateLimited(tenantKey, durationMs);
        metrics.PublicReadServiceUnavailable(tenantKey);
        metrics.PublicReadUnauthorized();
        metrics.PublicReadBadRequest();

        // Counter checks --------------------------------------------------
        var taggedCounters = new[]
        {
            TenantClientCacheMetrics.PublicReadHitCounterName,
            TenantClientCacheMetrics.PublicReadNotModifiedCounterName,
            TenantClientCacheMetrics.PublicReadMissCounterName,
            TenantClientCacheMetrics.PublicReadRateLimitedCounterName,
            TenantClientCacheMetrics.PublicReadServiceUnavailableCounterName,
        };

        foreach (var name in taggedCounters)
        {
            var measurements = listener.ForInstrument(name);
            measurements.Should().ContainSingle($"{name} should be incremented exactly once");
            var tags = measurements.Single().Tags;
            tags.Should().ContainKey("tenantKey",
                $"{name} MUST carry the tenantKey tag (R8.4)");
            tags["tenantKey"].Should().Be(tenantKey);
            tags.Should().NotContainKey("clientId",
                $"{name} MUST NOT carry the clientId tag (cardinality budget — parent R16.3)");
        }

        var untaggedCounters = new[]
        {
            TenantClientCacheMetrics.PublicReadUnauthorizedCounterName,
            TenantClientCacheMetrics.PublicReadBadRequestCounterName,
        };

        foreach (var name in untaggedCounters)
        {
            var measurements = listener.ForInstrument(name);
            measurements.Should().ContainSingle($"{name} should be incremented exactly once");
            var tags = measurements.Single().Tags;
            tags.Should().NotContainKey("tenantKey",
                $"{name} MUST NOT carry the tenantKey tag (R8.4 anti-enumeration)");
            tags.Should().NotContainKey("clientId",
                $"{name} MUST NOT carry the clientId tag");
        }

        // Histogram checks -----------------------------------------------
        // The duration histogram is recorded for Hit / NotModified / Miss /
        // RateLimited (4 measurements). ServiceUnavailable / Unauthorized /
        // BadRequest do not record a histogram entry — see
        // TenantClientCacheMetrics for the rationale.
        var histogramMeasurements = listener.ForInstrument(
            TenantClientCacheMetrics.PublicReadDurationHistogramName);
        histogramMeasurements.Should().HaveCount(4,
            "Hit / NotModified / Miss / RateLimited each record one duration sample");

        foreach (var sample in histogramMeasurements)
        {
            sample.Tags.Should().ContainKey("outcome",
                "the duration histogram MUST carry the outcome tag (R8.5)");
            sample.Tags.Should().ContainKey("tenantKey",
                "the duration histogram tags tenantKey for outcomes that already disclose tenant identity");
            sample.Tags.Should().NotContainKey("clientId",
                "the duration histogram MUST NOT tag clientId");
            sample.Value.Should().Be(durationMs);
        }
    }

    // ===== 6. Api key store holds only sha-256 hex (R1.4 / R9.5) ===========

    /// <summary>
    /// R1.4 + R9.5. The <see cref="TenantClientCachePublicReadOptions.ApiKeys"/>
    /// dictionary value type is documented as the SHA-256 hex digest of
    /// the plaintext API key. The validator enforces the digest format
    /// at host startup. We assert the structural invariant here so a
    /// future refactor that swaps the value type for a <c>byte[]</c>,
    /// a typed token record, or anything else surfaces as a deliberate
    /// review event (the regex-based hex match would no longer apply
    /// either way).
    /// </summary>
    [Fact]
    public void ApiKeyStore_Holds_Only_Sha256_Hex_Strings()
    {
        var apiKeysProp = typeof(TenantClientCachePublicReadOptions)
            .GetProperty(nameof(TenantClientCachePublicReadOptions.ApiKeys),
                BindingFlags.Public | BindingFlags.Instance);

        apiKeysProp.Should().NotBeNull();

        var apiKeysType = apiKeysProp!.PropertyType;
        apiKeysType.IsGenericType.Should().BeTrue(
            "ApiKeys MUST be a generic IDictionary<string,string>");

        var typeArgs = apiKeysType.GetGenericArguments();
        typeArgs.Should().HaveCount(2);
        typeArgs[0].Should().Be(typeof(string),
            "ApiKeys keys MUST be normalized tenant key strings (R1.5)");
        typeArgs[1].Should().Be(typeof(string),
            "ApiKeys values MUST be SHA-256 hex digest strings — NEVER plaintext, NEVER byte[], "
            + "NEVER a typed token wrapper. Plaintext is the consumer's responsibility (R1.4, R9.5)");

        // Drive the validator with a malformed value to confirm the
        // structural type check is reinforced by the runtime validator.
        // The validator's error message MUST NOT include the offending
        // value (R1.4 — never echo the digest into a log).
        var validator = new TenantClientCachePublicReadOptionsValidator(
            new CorsTestHostEnvironment());

        var options = new TenantClientCachePublicReadOptions();
        options.ApiKeys["acme"] = "not-a-sha256-hex-digest";

        var result = validator.Validate(name: null, options);

        result.Failed.Should().BeTrue(
            "a malformed ApiKeys value MUST fail validation (R1.4)");
        var failures = result.Failures?.ToArray() ?? Array.Empty<string>();
        failures.Should().NotBeEmpty();
        foreach (var failure in failures)
        {
            failure.Should().NotContain("not-a-sha256-hex-digest",
                "validator error messages MUST NOT echo the offending API key digest (R1.4)");
        }
    }

    // ===== Helpers ===========================================================

    private static IEnumerable<Type> TypeClosure(Type type)
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>();
        queue.Enqueue(type);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current))
            {
                continue;
            }

            yield return current;

            foreach (var iface in current.GetInterfaces())
            {
                if (seen.Add(iface))
                {
                    yield return iface;
                }
            }

            var baseType = current.BaseType;
            while (baseType is not null && seen.Add(baseType))
            {
                yield return baseType;
                baseType = baseType.BaseType;
            }
        }
    }
}

internal static class SingleHelper
{
    public static T Single<T>(this IEnumerable<T> source, string because)
    {
        try
        {
            return Enumerable.Single(source);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"Expected a single element because {because}.", ex);
        }
    }
}
