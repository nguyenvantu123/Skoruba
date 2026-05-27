// Feature: tenant-client-cache-expansion, Task 12
//
// Reflection-driven security regression tests that hold the public-facing
// surface to the spec contract:
//
//   1. Mapper_Reflection_Surface_Excludes_All_Forbidden_Field_Names —
//      enumerates every property on ClientCacheSnapshotDto and asserts
//      none of them collide with the Public_Safe_Fields forbidden set.
//      Catches accidental drift the moment a property is added.
//
//   2. No_Public_Endpoint_Exposes_Snapshot — enumerates every action
//      method on ClientsController and asserts none of them returns
//      ClientCacheSnapshotDto / ClientCacheSnapshotEnvelope (R15.3).
//
// Validates: Requirements 15.1, 15.2, 15.3, 15.4.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using FluentAssertions;

using Microsoft.AspNetCore.Mvc;

using Skoruba.Duende.IdentityServer.Admin.UI.Api.Controllers;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache;

public sealed class SecurityRegressionTests
{
    /// <summary>
    /// Forbidden property names that MUST never appear on
    /// <see cref="ClientCacheSnapshotDto"/>. Mirrors the spec's
    /// <c>Public_Safe_Fields</c> exclusion list (R2.2, R15.1).
    /// </summary>
    private static readonly HashSet<string> ExactForbiddenNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ClientSecrets",
        "Claims",
        "Properties",
        "IdentityProviderRestrictions",
        "Id",
        "PairWiseSubjectSalt",
        "AccessTokenTypes",
        "RefreshTokenExpirations",
        "RefreshTokenUsages",
        "ProtocolTypes",
        "DPoPValidationModes",
        "TenantRedirectPairs",
    };

    /// <summary>
    /// Pattern that catches the SelectList-style view-helper suffix
    /// (<c>*Items</c>) that the snapshot must never replicate (R15.4).
    /// Kept narrow on purpose: <c>RequireClientSecret</c> is a legitimate
    /// boolean toggle in <c>Public_Safe_Fields</c>, so a wider
    /// <c>(?i).*secret.*</c> pattern would produce false positives.
    /// </summary>
    private static readonly Regex ForbiddenPattern = new(
        @"^.*Items$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void Mapper_Reflection_Surface_Excludes_All_Forbidden_Field_Names()
    {
        var props = typeof(ClientCacheSnapshotDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();

        props.Should().NotBeEmpty(
            "ClientCacheSnapshotDto must expose at least one public property");

        // Exact-match exclusions.
        foreach (var forbidden in ExactForbiddenNames)
        {
            props.Should().NotContain(
                forbidden,
                $"R15.1: '{forbidden}' must never be added to ClientCacheSnapshotDto");
        }

        // Pattern-match exclusions (e.g. *Items, *Secret*, *Password*).
        foreach (var prop in props)
        {
            ForbiddenPattern.IsMatch(prop)
                .Should()
                .BeFalse(
                    $"R15.4: property '{prop}' on ClientCacheSnapshotDto matches " +
                    "the *Items SelectList view-helper suffix and must not be " +
                    "added to the snapshot whitelist.");
        }
    }

    [Fact]
    public void No_Public_Endpoint_Exposes_Snapshot()
    {
        var forbiddenReturnTypes = new[]
        {
            typeof(ClientCacheSnapshotDto),
            typeof(ClientCacheSnapshotEnvelope),
        };

        var actions = typeof(ClientsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .ToList();

        actions.Should().NotBeEmpty(
            "ClientsController must expose at least one action method");

        foreach (var action in actions)
        {
            var leakedType = ExtractAllTypes(action.ReturnType)
                .FirstOrDefault(t => forbiddenReturnTypes.Contains(t));

            leakedType.Should().BeNull(
                $"R15.3: action '{action.Name}' must not return '{leakedType?.Name}' " +
                "(no public HTTP surface may expose tenant-client cache snapshots)");
        }
    }

    /// <summary>
    /// Recursively expands a return type into the set of types it
    /// transports — covers <see cref="Task{T}"/>, <see cref="ValueTask{T}"/>,
    /// <see cref="ActionResult{T}"/>, and any single-arity generic wrapper
    /// MVC may add later. Multiple-arity generics fall back to walking
    /// every type argument.
    /// </summary>
    private static IEnumerable<Type> ExtractAllTypes(Type? type)
    {
        var stack = new Stack<Type>();
        if (type is not null)
        {
            stack.Push(type);
        }

        var visited = new HashSet<Type>();

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            yield return current;

            if (current.IsGenericType)
            {
                foreach (var arg in current.GetGenericArguments())
                {
                    stack.Push(arg);
                }
            }
        }
    }
}
