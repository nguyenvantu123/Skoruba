// Feature: tenant-client-cache-expansion, Task 4
//
// Default <see cref="IClientTenantScopeResolver"/> implementation. Applies
// the priority chain described in design.md "Algorithm" Mermaid:
//
//   Priority 1: ClientTenantRedirectUris rows for the client (DB-backed).
//               When `ClientDto.TenantRedirectPairs` is non-empty (which is
//               how `IClientService.GetClientAsync` projects the DB rows
//               whenever they exist), each pair's TenantKey contributes.
//
//   Priority 2: parse `Properties[skoruba_tenant_redirect_pairs]` JSON via
//               the BusinessLogic helper. Only applied when priority 1 is
//               empty. This handles synthetic ClientDtos that were not
//               sourced through GetClientAsync (where the property is
//               normally stripped after population) — and matches the STS
//               `ClientTenantRedirectResolver` legacy fallback.
//
//   Priority 3: empty list — the client is shared/global and not scoped to
//               any tenant.
//
// All outputs are normalized (`Trim().ToLowerInvariant()`),
// case-insensitively distinct, sorted lexicographically ascending, and
// returned as an immutable `IReadOnlyList<string>` (R11.3, R11.4).
//
// Validates: Requirements 11.1, 11.2, 11.3, 11.4, 11.5, 11.6, 11.7

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Configuration;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Helpers;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Services.Interfaces;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Shared.ExceptionHandling;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

internal sealed class ClientTenantScopeResolver : IClientTenantScopeResolver
{
    // Why we depend on `IClientService` (BusinessLogic) and NOT `IClientRepository`
    // / `IClientStore`:
    //   - AGENTS.md hard rule forbids the UI.Api layer reaching into the
    //     EF repository or DbContext directly.
    //   - R11.6 forbids consuming Duende's `IClientStore` (which would
    //     bypass tenant scoping entirely).
    //   - `IClientService.GetClientAsync` already calls
    //     `IClientRepository.GetClientTenantRedirectUrisAsync` and projects
    //     the result onto `ClientDto.TenantRedirectPairs`, so the per-client
    //     view is one DB roundtrip via the canonical service surface.
    //     Adding a new repository method is therefore unnecessary for this
    //     resolver — see the integer-overload comment below.
    private readonly IClientService _clientService;

    public ClientTenantScopeResolver(IClientService clientService)
    {
        _clientService = clientService ?? throw new ArgumentNullException(nameof(clientService));
    }

    public Task<IReadOnlyList<string>> ResolveTenantKeysAsync(
        ClientDto client,
        CancellationToken cancellationToken)
    {
        // R11.5: never throw on empty input. Null ClientDto is a degenerate
        // case the caller may surface (e.g. clone path before a re-read);
        // collapse to "no tenants" so the cache write becomes a no-op.
        if (client is null)
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        cancellationToken.ThrowIfCancellationRequested();

        var keys = ResolveFromDto(client);
        return Task.FromResult(keys);
    }

    public async Task<IReadOnlyList<string>> ResolveTenantKeysAsync(
        int clientPrimaryKey,
        CancellationToken cancellationToken)
    {
        // R11.5: empty / non-positive id ⇒ empty result, no DB hit.
        if (clientPrimaryKey <= 0)
        {
            return Array.Empty<string>();
        }

        cancellationToken.ThrowIfCancellationRequested();

        ClientDto client;
        try
        {
            // `GetClientAsync` populates `TenantRedirectPairs` from the DB
            // (priority 1) AND, when the DB has no rows, from the legacy
            // property JSON (priority 2). After that call the `Properties`
            // collection no longer contains `skoruba_tenant_redirect_pairs`
            // (the helper strips it). That collapses our priority chain into
            // a single read of `client.TenantRedirectPairs` for the integer
            // overload — both DB-sourced and JSON-sourced pairs flow through
            // the same field.
            client = await _clientService.GetClientAsync(clientPrimaryKey).ConfigureAwait(false);
        }
        catch (UserFriendlyErrorPageException)
        {
            // Background sweeps may race with deletes — treat "client not
            // found" as a graceful empty result (R11.5). The caller logs
            // sweep summary; we don't emit anything from the resolver itself.
            return Array.Empty<string>();
        }

        return ResolveFromDto(client);
    }

    /// <summary>
    /// Apply the priority chain to a fully-formed <see cref="ClientDto"/>.
    /// </summary>
    private static IReadOnlyList<string> ResolveFromDto(ClientDto client)
    {
        // ----- Priority 1 ------------------------------------------------
        // ClientTenantRedirectUris (DB) projected onto ClientDto.TenantRedirectPairs
        // by IClientService.GetClientAsync.
        var fromDb = client.TenantRedirectPairs?
            .Where(p => p is not null)
            .Select(p => p!.TenantKey)
            .ToArray()
            ?? Array.Empty<string>();

        if (fromDb.Length > 0)
        {
            return Normalize(fromDb);
        }

        // ----- Priority 2 ------------------------------------------------
        // Legacy `Properties[skoruba_tenant_redirect_pairs]` JSON. Only
        // reachable when:
        //   (a) `Properties` still contains the entry (i.e. the caller did
        //       NOT route through `GetClientAsync`, e.g. a synthetic test
        //       DTO or a controller pre-write capture), AND
        //   (b) priority 1 above produced zero rows.
        var rawJson = client.Properties?
            .FirstOrDefault(p => p is not null
                                 && string.Equals(
                                     p.Key,
                                     ClientTenantRedirectPairsHelper.PropertyKey,
                                     StringComparison.Ordinal))
            ?.Value;

        if (!string.IsNullOrWhiteSpace(rawJson))
        {
            // TryParsePairs swallows JsonException internally and returns
            // false with an empty out-list. Either way ⇒ never throws here.
            ClientTenantRedirectPairsHelper.TryParsePairs(rawJson, out var legacyPairs);
            if (legacyPairs.Count > 0)
            {
                return Normalize(legacyPairs.Select(p => p.TenantKey));
            }
        }

        // ----- Priority 3 ------------------------------------------------
        return Array.Empty<string>();
    }

    /// <summary>
    /// Trim + lower-invariant + drop blanks + distinct (case-insensitive)
    /// + lexicographic ascending.
    /// </summary>
    private static IReadOnlyList<string> Normalize(IEnumerable<string?> tenantKeys)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        foreach (var raw in tenantKeys)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var normalized = raw.Trim().ToLowerInvariant();
            if (seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }
}
