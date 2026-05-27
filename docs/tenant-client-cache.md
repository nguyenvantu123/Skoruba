# Tenant Client Cache — Operator Runbook

Spec: [`tenant-client-cache-expansion`](../.kiro/specs/tenant-client-cache-expansion/)
Audience: site reliability and platform engineers operating the Skoruba Admin host.

## 1. Overview

`TenantClientCache` is a per-tenant Redis snapshot of Duende `Client` configuration that the Admin host writes after every successful CRUD against `/api/Clients`. It exists so downstream public-facing tenant services can read a public-safe view of a client without paying the cost of joining the master Identity database on every request.

The cache is keyed by tenant. A single client that maps to multiple tenants gets one snapshot per tenant.

### What is cached

A whitelist projection of `ClientDto`. Only the 38 fields listed in the spec glossary `Public_Safe_Fields` (R2.1) are serialized — for example `ClientId`, `RedirectUris`, `AllowedScopes`, lifetime ints, and the front/back-channel logout URIs.

### What is NOT cached

The following fields are intentionally excluded and the snapshot mapper has a defensive reflection guard that throws when a future refactor adds a new property whose name matches `(?i).*secret.*` to `ClientDto` (R2.5):

- `ClientSecrets`
- `Claims`
- `Properties`
- `IdentityProviderRestrictions`
- `Id` (database primary key)
- `PairWiseSubjectSalt`

`*Items` view-helpers (e.g. `AllowedGrantTypesItems`), `AccessTokenTypes`, `RefreshTokenExpirations`, `RefreshTokenUsages`, `ProtocolTypes`, `DPoPValidationModes`, and the raw `TenantRedirectPairs` payload are also excluded.

## 2. Configuration

Bind the `TenantClientCache` section in the Admin host `appsettings.json` (or override via `TenantClientCache__*` environment variables). Defaults are taken verbatim from `TenantClientCacheOptions`.

| Key                     | Default       | Valid range                          | Notes                                                                 |
|-------------------------|---------------|--------------------------------------|-----------------------------------------------------------------------|
| `Enabled`               | `true`        | `true` or `false`                    | Master toggle. When `false`, every read/write/invalidate is a no-op and the background refresh hosted service is not registered. |
| `AbsoluteTtl`           | `01:00:00`    | `[00:05:00, 24:00:00]`               | Applied as `DistributedCacheEntryOptions.AbsoluteExpirationRelativeToNow` on every write. |
| `SlidingTtl`            | `null`        | `null` or `[00:01:00, AbsoluteTtl]`  | When `null`, sliding expiration is disabled.                          |
| `RefreshInterval`       | `01:00:00`    | `[00:05:00, 24:00:00]`               | Period between background sweeps that rebuild the cache from the database. |
| `WriteTimeoutMs`        | `2000`        | `[100, 10000]`                       | Per-operation wall-clock cap on `IDistributedCache` calls. Enforced via a linked `CancellationTokenSource`. |
| `MaxClientsPerTenant`   | `5000`        | `[1, 50000]`                         | Safety cap on how many clients the background sweep will materialize per tenant per cycle. |

`TenantClientCacheOptionsValidator` runs at host startup (`ValidateOnStart`) and fails fast with a message naming the offending key path and observed value when any value is out of range or `SlidingTtl > AbsoluteTtl`.

### Sample configuration

```json
{
  "TenantClientCache": {
    "Enabled": true,
    "AbsoluteTtl": "01:00:00",
    "SlidingTtl": null,
    "RefreshInterval": "01:00:00",
    "WriteTimeoutMs": 2000,
    "MaxClientsPerTenant": 5000
  }
}
```

## 3. Rollout checklist

Roll out one environment at a time. Each step gates on telemetry from the previous one.

1. Merge with `Enabled=false` set in production `appsettings.json`. No behaviour change is expected; the hosted service is not registered when disabled.
2. Enable in dev via env var `TenantClientCache__Enabled=true`. Restart the Admin host.
3. Smoke-test the write path: `POST /api/Clients` with a tenant-scoped client, then inspect Redis for key `tenant-registry:{tenantKey}:clients:{clientId}` and confirm the envelope JSON deserializes to a `ClientCacheSnapshotEnvelope` with `version=1`.
4. Enable in staging. Observe at least one `RefreshInterval` cycle. Confirm the `TenantClientCacheRefreshCompleted` log lands once per cycle and `tenant_client_cache.refresh.last_completed_at` advances.
5. Enable in production after one week of clean staging telemetry (no sustained `WriteFailedTransient`/`InvalidateFailedTransient` rate above the existing Redis error budget).

Disabling in production is a single env-var flip (`TenantClientCache__Enabled=false`) followed by host restart. Existing Redis entries expire at `AbsoluteTtl`; no manual cleanup needed.

## 4. Telemetry

Structured Serilog events emitted by the cache service and the background sweep:

| Event name                            | Source                                   | Levels in use                                                                 |
|---------------------------------------|------------------------------------------|-------------------------------------------------------------------------------|
| `TenantClientCacheRead`               | `TenantClientCacheService.ReadSnapshotAsync` | `Debug` for `Hit` / `Miss` / `Stale`; `Warning` for transient failure         |
| `TenantClientCacheWrite`              | `TenantClientCacheService.WriteSnapshotAsync` | `Debug` for `WriteSkippedDisabled`; `Information` for `WriteSucceeded`; `Warning` for `WriteFailedTransient` (incl. `Oversize`) |
| `TenantClientCacheInvalidate`         | `TenantClientCacheService.InvalidateSnapshotAsync` | `Debug` for `WriteSkippedDisabled`; `Information` for `InvalidateSucceeded`; `Warning` for `InvalidateFailedTransient` |
| `TenantClientCacheRefresh`            | `TenantClientCacheRefreshService` per-tenant errors | `Warning` for per-tenant transient failure, `MaxClientsPerTenantExceeded`, `RefreshSweepTooLong` |
| `TenantClientCacheRefreshCompleted`   | `TenantClientCacheRefreshService` per-cycle summary | `Information` once per sweep                                                  |
| `TenantClientCacheRefreshServiceStarted` | `TenantClientCacheRefreshService.ExecuteAsync` | `Information` once at startup with bound options + resolved `IDistributedCache` impl type |

Every event carries the structured fields `EventType`, `TenantKey`, `ClientId`, `Outcome`, `DurationMs`, `SnapshotVersion`, and `CorrelationId` (Activity TraceId, when present). Exception messages are sanitized via `LogRedaction.SanitizeExceptionMessage` — truncated to 256 chars with `password=`, `auth=`, and similar tokens replaced by `***`.

Snapshot bodies, raw cache keys, and any value of a forbidden field are never logged.

## 5. Metrics

`Meter("TenantClientCache", "1.0")` exposes the following instruments. Tag set is locked to `{tenantKey, outcome}` only (R16.3) — `clientId` is intentionally excluded to keep cardinality bounded.

| Instrument                                  | Kind                  | Tag set            |
|---------------------------------------------|-----------------------|--------------------|
| `tenant_client_cache.read.hit`              | Counter               | `tenantKey, outcome` |
| `tenant_client_cache.read.miss`             | Counter               | `tenantKey, outcome` |
| `tenant_client_cache.write.success`         | Counter               | `tenantKey, outcome` |
| `tenant_client_cache.write.failure`         | Counter               | `tenantKey, outcome` |
| `tenant_client_cache.invalidate.success`    | Counter               | `tenantKey, outcome` |
| `tenant_client_cache.invalidate.failure`    | Counter               | `tenantKey, outcome` |
| `tenant_client_cache.refresh.sweep.duration_ms` | Histogram         | `outcome`          |
| `tenant_client_cache.refresh.last_completed_at` | Observable gauge | (none)             |

The background sweep updates `refresh.last_completed_at` to the unix-second timestamp of the last successful (or partial-failure) sweep completion (R16.4). A flat or stale gauge in production is the canonical "the sweep stopped running" signal.

## 6. Risk notes

These mirror the design.md "Risks and Mitigations" table.

- **Redis down at request time.** Each write is bound by `WriteTimeoutMs`. Worst-case added per-request latency is `WriteTimeoutMs × |tenantKeys|` (≤ 2 s × 50 = 100 s with default caps). Mitigated by the `MaxClientsPerTenant` resolver cap and by the background refresh self-healing the cache once Redis recovers. CRUD HTTP endpoints stay 201/204; failure is fail-soft (R10.1).
- **Snapshot drift window.** The maximum age of a stale snapshot after a Redis write fails is bounded by `RefreshInterval`. Operators tune the trade-off between cache freshness and database load by changing `RefreshInterval`.
- **Reserved tenant key `public` collision.** The tenant infrastructure already uses `tenant:public:names` as a separate namespace. Because cache keys take the form `{tenantKey}:clients:{clientId}`, there is no shared prefix with `tenant:public:*`. Operators must still treat `public` as a reserved tenant key per existing policy and avoid creating it via the tenant admin API.
- **Defensive `clientId == "__list__"` guard.** The list-key suffix `__list__` (chosen over `:list` to avoid collision with a real client whose `ClientId == "list"`) is rejected at write/invalidate time with `ArgumentException`. As a defense-in-depth measure operators should also reject `__list__` at tenant creation / client creation time (tracked as a follow-up — out of scope for this spec).

## 7. Failure modes

- **Redis outage.** All operations log `Outcome=WriteFailedTransient` (or the read/invalidate equivalent) at `Warning` and increment `tenant_client_cache.write.failure`. CRUD HTTP returns success. Background sweep continues to attempt writes per tenant; a single failing tenant does not abort the cycle (R8.5).
- **JSON parse error on read.** Read returns `null` and emits `Outcome=Miss subreason=CorruptPayload` at `Debug`. Caller treats this as a cold cache.
- **`Version > 1` envelope.** Forward-compatibility path: read returns `null` and emits `Outcome=Stale subreason=FutureVersion`. Reader-side never deserializes a future-shape envelope (R2.8).
- **Oversize snapshot (> 256 KiB serialized).** Write is rejected with `Outcome=WriteFailedTransient subreason=Oversize` at `Warning`. No partial write is persisted (R2.6, R14.5).
- **Background sweep exceeds half of `RefreshInterval`.** Logs `Outcome=WriteFailedTransient subreason=RefreshSweepTooLong` at `Warning` (R14.4). Operators should consider raising `RefreshInterval` or lowering `MaxClientsPerTenant`.
- **Single client load fails during sweep.** Per-client try/catch logs `WriteFailedTransient`; no partial or null snapshot is written (R15.5). The next sweep retries.

## 8. Security review checklist

Reviewer signs after `dotnet test` passes for all 11 prior tasks. Each item points to the test that proves the property (mirrors design.md "Security Review Checkpoint", 10 items).

- [ ] 1. `ClientCacheSnapshotMapper` does not copy `ClientSecrets`, `Claims`, `Properties`, `IdentityProviderRestrictions`, `PairWiseSubjectSalt`, `Id`. Proof: `ClientCacheSnapshotMapperTests.Maps_Public_Safe_Fields_Verbatim` + `ClientCacheSnapshotNoLeakProperties.NoSecretLeak` (P1, R17.6).
- [ ] 2. Serialized JSON contains no property name matching `(?i).*secret.*` for any input. Proof: `ClientCacheSnapshotSerializerTests.Property01_WhitelistFields` + `ClientCacheSnapshotNoLeakProperties.NoSecretLeak` (P1).
- [ ] 3. Logging contains no snapshot body, raw exception, or secret-pattern token. Proof: `TenantClientCacheLoggingProperties` (P14, R13.1, R13.4).
- [ ] 4. No public HTTP endpoint returns a snapshot. Proof: `SecurityRegressionTests.No_Public_Endpoint_Exposes_Snapshot` (R15.3) plus a `dotnet run` + `curl /api/...` smoke check confirming no new route is registered.
- [ ] 5. No Duende `IClientStore` decoration. Proof: `git grep -nE 'IClientStore|FindClientByIdAsync' src/` shows zero new feature-diff hits; `dotnet build` clean.
- [ ] 6. Cache key namespace is isolated. Proof: `TenantClientCacheKeyProperties.Property04_KeyFormat` (P4) and the manual key-set diff in section 1.
- [ ] 7. TLS-in-transit + Redis ACL inherit from `TenantInfrastructure`. Proof: read `ServiceCollectionExtensions.cs` of `TenantInfrastructure`; this feature does not override the registered `IDistributedCache` instance.
- [ ] 8. Background sweep does not write a partial / null snapshot when DB load fails. Proof: `TenantClientCacheRefreshServiceTests.DbError_LoadingClient_DoesNotWrite_PartialSnapshot` (R15.5).
- [ ] 9. Whitelist guard rejects future `ClientDto` refactors that add a secret-bearing field. Proof: `ClientCacheSnapshotMapperTests.EnsureNoLeakedSecretField_Throws_When_Reflection_Sees_Future_SecretBearing_Property` (R2.5).
- [ ] 10. No EF migration is added by this feature. Proof: `git diff main..HEAD -- '**/Migrations/**' '**/*.Designer.cs' '**/*ModelSnapshot.cs'` returns empty; reviewer also runs `dotnet ef migrations list` before/after with identical output.

## 9. Security verification — feature diff audit

Run from the repository root after rebasing onto `main`. The findings recorded below correspond to the audit performed on the working tree against `main` for this feature branch.

```bash
# 1. Spot-check for accidentally committed credentials.
git diff main..HEAD -- '*.json' '*.cs' '*.md' \
  | grep -iE 'password|secret|connectionstring|api[_-]?key' || true

# 2. Confirm no NuGet package was added (only existing packages may move).
git diff main..HEAD -- '**/*.csproj' | grep '<PackageReference Include='

# 3. Confirm no EF migration was added.
git diff main..HEAD -- '**/Migrations/**' '**/*.Designer.cs' '**/*ModelSnapshot.cs'

# 4. Confirm no new IClientStore decoration.
git grep -nE 'IClientStore|FindClientByIdAsync' src/
```

Findings recorded for the current feature branch (working tree vs `main`):

- (1) **Credential scan — clean.** The single match is the pre-existing log-namespace literal `Skoruba.Duende.IdentityServer.STS.Identity.Services.StsIdentityDbConnectionStringResolver` in `serilog.Development.json`; no live credential is committed.
- (2) **Package additions — all reuse existing solution-wide versions.** The csproj diff adds `FsCheck.Xunit 3.0.0`, `Moq 4.20.72`, `FluentAssertions 6.12.1`, `Microsoft.NET.Test.Sdk 18.0.1`, `xunit 2.9.3`, `xunit.runner.visualstudio 3.1.5`, `Microsoft.Extensions.Caching.Memory 10.0.2`, `Microsoft.EntityFrameworkCore.InMemory 10.0.2`, `Microsoft.AspNetCore.Identity.EntityFrameworkCore 10.0.2`, and `Microsoft.AspNetCore.DataProtection 10.0.2`. Every one of those packages already exists at the same version in another `csproj` in the solution (verified via `grep -n` across `**/*.csproj`); no new NuGet package is introduced.
- (3) **EF migrations — none.** Diff is empty.
- (4) **Duende `IClientStore` references — none added.** The two pre-existing references in `src/Skoruba.Duende.IdentityServer.STS.Identity/Controllers/AccountController.cs` and `GrantsController.cs` are untouched by this feature; `git diff main -- src/ | grep -E 'IClientStore|FindClientByIdAsync'` returns no hits.

Re-run the four commands at PR review time and update the findings if the diff has changed.
