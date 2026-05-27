# Requirements Document

Tenant Client Cache Expansion

## Introduction

Mục tiêu của feature này là **mở rộng phạm vi cache phía Admin UI API** từ chỗ chỉ lưu một field duy nhất (`AllowedScopes` trong `IClientScopeCacheService`) thành lưu một **public-safe snapshot** chứa toàn bộ Client config cần thiết cho mobile/SPA bootstrap (RedirectUris, GrantTypes, ClientName, PostLogoutRedirectUris, AllowedCorsOrigins, RequirePkce, AllowOfflineAccess, ...). Snapshot được lưu xuống Redis distributed cache, scope theo tenant, **invalidate ngay khi Admin UI API thực hiện CRUD** (Add/Update/Delete/Clone) trên Duende Client, và refresh định kỳ qua background service hợp nhất với (hoặc chạy song song) `TenantRegistryCacheRefreshService` đã có (`TenantInfrastructureOptions.TenantCacheRefreshInterval`, default 1h).

Bối cảnh và ranh giới:

- **Source of truth**: Duende `Client` table (qua `IClientRepository` / `IClientService`). Cache là performance optimization — fail-soft khi Redis down, không được block CRUD.
- **Tenant scoping**: Duende `Client` table KHÔNG có cột `TenantKey` trực tiếp. Quan hệ tenant ↔ client được suy ra từ một trong hai nguồn: (a) `ClientTenantRedirectUris` table (`x.Client.ClientId == clientId AND x.TenantKey == tenantKey`) cho client multi-tenant đã được map redirect pair, hoặc (b) `Client.Properties[skoruba_client_type]` / property fallback cho client legacy. Cơ chế này đã hiện diện trong `ClientTenantRedirectResolver` của STS và phải được tái sử dụng — KHÔNG schema migration ở phase này.
- **Backend cache**: tái sử dụng `IDistributedCache` (Redis) đã wire qua `TenantInfrastructure` với prefix `tenant-registry:`. Cache key MỚI nằm dưới namespace `tenant-registry:{tenantKey}:clients:*` để không đụng namespace tenant hiện hữu (`tenant-registry:tenant:{tenantKey}`, `tenant-registry:public-tenant-names`, `tenant-registry:secret:{tenantKey}:{serviceName}`).
- **Public-safe whitelist**: cache snapshot tuyệt đối KHÔNG chứa `ClientSecrets` (kể cả hashed value), `Claims`, `Properties`, `IdentityProviderRestrictions`, internal numeric `Id`, `PairWiseSubjectSalt`, hay bất kỳ field nào có thể chứa secret. Whitelist được khoá ở phần Glossary (`Public_Safe_Fields`) và Requirement 2.
- **Backward compatibility**: `IClientScopeCacheService` hiện tại (key = raw `clientId.Trim()`, value = space-separated AllowedScopes) đã được wire vào `ClientsController.Post/Put/Delete`. Feature này KHÔNG được break consumer hiện tại. Hai chiến lược hợp lệ: (a) giữ `IClientScopeCacheService` nguyên trạng và đẩy `IClientCacheSnapshotService` mới song song; (b) deprecate `IClientScopeCacheService` và để `IClientCacheSnapshotService` ghi đồng thời cả AllowedScopes-only key (legacy) lẫn snapshot key (mới). Quyết định cụ thể giữa (a) và (b) thuộc phase Design — yêu cầu chỉ buộc behaviour không thay đổi từ góc nhìn consumer hiện hữu.
- **Audit & redaction**: mọi thao tác cache write/invalidate phải emit structured Serilog event `TenantClientCacheWrite` / `TenantClientCacheInvalidate` với `TenantKey`, `ClientId`, `Outcome`, `CorrelationId` (nếu có); KHÔNG được log raw secret, raw `Properties` value, hoặc snapshot JSON full body.

Out-of-scope (sẽ KHÔNG làm trong feature này):

- KHÔNG thêm public endpoint (mobile/SPA gọi Redis trực tiếp hoặc qua một read-only endpoint mới ở STS / Admin UI API). Feature này chỉ thiết lập write side + invalidation; consumer side sẽ là feature riêng.
- KHÔNG thêm Redis pub/sub invalidation broadcast (sẽ là feature riêng nếu cần).
- KHÔNG thay thế `IClientStore` của Duende IdentityServer (cache layer feature này là độc lập, không decorate Duende `IClientStore`).
- KHÔNG đổi schema Duende `Client` table, KHÔNG thêm cột `TenantKey` mới, KHÔNG migration EF.
- KHÔNG cache `ClientSecrets`, `Claims`, `Properties`, `IdentityProviderRestrictions` (Public_Safe_Fields whitelist là cứng).
- KHÔNG mã hoá-at-rest tự cài thêm bên ngoài cơ chế Redis-native (TLS in-transit + Redis ACL được coi là đủ ở phase này).
- KHÔNG đổi UI Admin client side — feature này thuần backend.

## Glossary

- **Admin_Api_Host**: Tiến trình `Skoruba.Duende.IdentityServer.Admin.UI.Api` (ASP.NET Core REST API host). Là nơi `ClientsController` cư trú và là nơi cache write + invalidate xảy ra trong feature này.
- **Sts_Host**: Tiến trình `Skoruba.Duende.IdentityServer.STS.Identity`. Hiện đã host `TenantRegistryController`, `TenantRegistryCacheRefreshService` và Redis cache wiring. Feature này KHÔNG bắt buộc thêm endpoint mới ở Sts_Host (out-of-scope).
- **Tenant_Infrastructure**: Project `Skoruba.Duende.IdentityServer.TenantInfrastructure` chứa `ITenantRegistryCache`, `TenantInfrastructureOptions`, `TenantRegistryCacheRefreshService`, Redis wiring với prefix `tenant-registry:`.
- **Distributed_Cache**: Implementation `IDistributedCache` được wire bởi `Tenant_Infrastructure` (Redis trong production, `MemoryDistributedCache` trong test). KHÔNG được swap implementation theo feature này.
- **Tenant_Client_Cache**: Service mới `ITenantClientCacheService` (tên symbol có thể tinh chỉnh ở Design) chịu trách nhiệm read/write/invalidate Client_Cache_Snapshot xuống Distributed_Cache. Là điểm tích hợp duy nhất của feature này từ phía Admin UI API.
- **Client_Cache_Snapshot**: Bản ghi JSON public-safe được serialize và lưu xuống Distributed_Cache. Chỉ chứa Public_Safe_Fields. Định nghĩa cứng tại R2.
- **Public_Safe_Fields**: Tập field cố định: `ClientId`, `ClientName`, `ClientUri`, `LogoUri`, `Description`, `Enabled`, `ProtocolType`, `RedirectUris`, `PostLogoutRedirectUris`, `AllowedCorsOrigins`, `AllowedGrantTypes`, `AllowedScopes`, `AllowedIdentityTokenSigningAlgorithms`, `RequirePkce`, `AllowPlainTextPkce`, `RequireClientSecret`, `RequireConsent`, `AllowOfflineAccess`, `AllowAccessTokensViaBrowser`, `AlwaysIncludeUserClaimsInIdToken`, `FrontChannelLogoutUri`, `FrontChannelLogoutSessionRequired`, `BackChannelLogoutUri`, `BackChannelLogoutSessionRequired`, `AccessTokenLifetime`, `IdentityTokenLifetime`, `AuthorizationCodeLifetime`, `AbsoluteRefreshTokenLifetime`, `SlidingRefreshTokenLifetime`, `RefreshTokenExpiration`, `RefreshTokenUsage`, `UpdateAccessTokenClaimsOnRefresh`, `EnableLocalLogin`, `RequirePushedAuthorization`, `RequireRequestObject`, `InitiateLoginUri`, `UseTenantRedirectPairs`, `LastWriteUtc` (timestamp do feature này gắn). Mọi field khác trên `ClientDto` KHÔNG được serialize vào Client_Cache_Snapshot, đặc biệt `ClientSecrets`, `Claims`, `Properties`, `IdentityProviderRestrictions`, `PairWiseSubjectSalt`, internal `Id`.
- **Tenant_Client_Cache_Key**: Format key cố định trong Distributed_Cache:
  - `tenant-registry:{tenantKey}:clients:{clientId}` cho per-client snapshot.
  - `tenant-registry:{tenantKey}:clients:list` cho danh sách `clientId` thuộc tenant (optional, dùng cho refresh sweep).
  - `tenantKey` được normalize bằng `tenantKey.Trim().ToLowerInvariant()` (giống pattern hiện hữu trong `Tenant_Infrastructure`).
  - `clientId` được normalize bằng `clientId.Trim()` (giữ nguyên case-sensitive theo hợp đồng Duende).
- **Tenant_Scope_Resolution**: Cơ chế xác định một Duende `Client` row thuộc tenant nào. Theo thứ tự ưu tiên: (1) tập `ClientTenantRedirectUris` rows có `Client.ClientId == clientId` → tenant set là `DISTINCT TenantKey`; (2) nếu rỗng AND `Client.Properties[skoruba_tenant_redirect_pairs]` parse được JSON → tenant set là `DISTINCT TenantKey` từ pairs; (3) nếu rỗng AND `Client.Properties[skoruba_client_type]` đánh dấu là "shared" / "central" → tenant set là rỗng (client không thuộc tenant nào → KHÔNG cache). Một client có thể thuộc nhiều tenant cùng lúc (multi-tenant client) → sinh nhiều snapshot, một per `(tenantKey, clientId)`.
- **Tenant_Client_Cache_Options**: Section cấu hình mới `TenantClientCache` đặt cùng cấp với `TenantInfrastructure` trong `appsettings.json`. Các key: `Enabled` (bool, default `true`), `AbsoluteTtl` (TimeSpan, default `01:00:00`, range `[00:05:00, 24:00:00]`), `SlidingTtl` (TimeSpan?, default `null` = không sliding, range nếu set: `[00:01:00, AbsoluteTtl]`), `RefreshInterval` (TimeSpan, default `01:00:00`, range `[00:05:00, 24:00:00]`), `WriteTimeoutMs` (int, default `2000`, range `[100, 10000]`), `MaxClientsPerTenant` (int, default `5000`, range `[1, 50000]`).
- **Background_Refresh**: BackgroundService chịu trách nhiệm sweep và refresh Client_Cache_Snapshot cho mỗi `(tenantKey, clientId)` đang active. Có thể là extension của `TenantRegistryCacheRefreshService` hoặc một `TenantClientCacheRefreshService` riêng — quyết định thuộc Design phase. Hợp đồng (R8) là yêu cầu chức năng, không bind vào lựa chọn class cụ thể.
- **Invalidate_On_Crud**: Hành vi của Admin_Api_Host `ClientsController` khi xử lý Add / Update / Delete / Clone request: gọi Tenant_Client_Cache để write fresh snapshot (Add/Update/Clone) hoặc remove snapshot (Delete) cho từng `(tenantKey, clientId)` ngay lập tức trong cùng request, KHÔNG đợi background refresh.
- **Cache_Outcome**: enum `Hit | Miss | Stale | WriteSucceeded | WriteSkippedDisabled | WriteFailedTransient | InvalidateSucceeded | InvalidateFailedTransient`. Dùng cho structured log + metric. KHÔNG được leak raw Redis exception ra response.
- **Audit_Event**: structured Serilog event với `EventType ∈ {"TenantClientCacheWrite", "TenantClientCacheInvalidate", "TenantClientCacheRefresh"}`, `TenantKey`, `ClientId`, `Outcome` ∈ Cache_Outcome, `DurationMs`, `CorrelationId`, `SnapshotVersion`. KHÔNG chứa snapshot body, raw secret, raw `Properties`.
- **Snapshot_Version**: Integer đặt trong Client_Cache_Snapshot envelope (`{ "version": 1, "data": {...} }`). Version 1 là baseline. Một consumer KHÔNG được phép deserialize snapshot có version > version mà consumer hiểu.
- **Last_Write_Utc**: Timestamp UTC do Tenant_Client_Cache gắn vào snapshot tại thời điểm write. Dùng để consumer detect stale + để Background_Refresh lựa chọn skip / overwrite logic.
- **Fail_Soft**: hành vi của Tenant_Client_Cache khi Distributed_Cache trả về exception (Redis down, timeout, connection reset): emit Audit_Event với `Outcome=WriteFailedTransient | InvalidateFailedTransient`, log Warning level kèm exception, KHÔNG re-throw, KHÔNG fail HTTP request đang serve. Source of truth là DB.
- **Client_Service**: Service `Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Services.ClientService` (`IClientService`). Duy nhất tier business logic được phép chạm vào client persistence và là tier mà feature này có thể consume để load Client_Cache_Snapshot dữ liệu (qua method công khai như `GetClientAsync(int)`).
- **Client_Repository**: Repository `Skoruba.Duende.IdentityServer.Admin.EntityFramework.Repositories.ClientRepository` (`IClientRepository`). KHÔNG được consume trực tiếp từ controller; nếu feature này cần mở rộng query (ví dụ batch load by tenant) thì phải bổ sung qua repository + service abstraction.
- **Client_Tenant_Redirect_Uris**: Bảng EF `ClientTenantRedirectUris` (xem `IAdminConfigurationDbContext.ClientTenantRedirectUris`) — nguồn truth chính cho Tenant_Scope_Resolution.

## Requirements

### Requirement 1: Cấu hình bật/tắt và validate phạm vi giá trị

**User Story:** As an operator running multi-tenant IdentityServer, I want a single configuration section that turns the tenant client cache on/off and validates TTL bounds at startup, so that I cannot accidentally deploy a misconfigured cache that breaks production.

#### Acceptance Criteria

1. THE Admin_Api_Host SHALL read configuration section `TenantClientCache` from `appsettings.json` AND environment variables on startup AND SHALL bind to a strongly typed `Tenant_Client_Cache_Options` POCO.
2. THE Admin_Api_Host SHALL apply default values `Enabled = true`, `AbsoluteTtl = 01:00:00`, `SlidingTtl = null`, `RefreshInterval = 01:00:00`, `WriteTimeoutMs = 2000`, `MaxClientsPerTenant = 5000` WHEN the corresponding configuration keys are absent.
3. IF `TenantClientCache:AbsoluteTtl` is configured outside the inclusive range `[00:05:00, 24:00:00]`, THEN THE Admin_Api_Host SHALL fail-fast at startup with an exception naming the configuration key AND its observed value.
4. IF `TenantClientCache:SlidingTtl` is configured AND falls outside the inclusive range `[00:01:00, TenantClientCache:AbsoluteTtl]`, THEN THE Admin_Api_Host SHALL fail-fast at startup with an exception naming the configuration key AND its observed value.
5. IF `TenantClientCache:RefreshInterval` is configured outside the inclusive range `[00:05:00, 24:00:00]`, THEN THE Admin_Api_Host SHALL fail-fast at startup with an exception naming the configuration key.
6. IF `TenantClientCache:WriteTimeoutMs` is configured outside the inclusive range `[100, 10000]` OR `TenantClientCache:MaxClientsPerTenant` is configured outside the inclusive range `[1, 50000]`, THEN THE Admin_Api_Host SHALL fail-fast at startup with an exception naming the configuration key.
7. WHERE `TenantClientCache:Enabled = false`, THE Tenant_Client_Cache SHALL be a no-op for read, write, AND invalidate operations AND SHALL emit Audit_Event with `Outcome=WriteSkippedDisabled` for each invocation at level Debug.
8. WHERE `TenantClientCache:Enabled = false`, THE Background_Refresh SHALL NOT be registered as a hosted service AND SHALL NOT execute.
9. THE Sts_Host SHALL NOT be required to read `TenantClientCache` section in this feature; if Sts_Host adds Tenant_Client_Cache consumer code in a future feature, the binding rules SHALL be replicated there.
10. THE Admin_Api_Host SHALL emit a single Information-level log entry on startup containing the bound `Tenant_Client_Cache_Options` values (excluding any future secret-bearing field) AND the resolved `Distributed_Cache` implementation type.

### Requirement 2: Định nghĩa Client_Cache_Snapshot DTO + whitelist field

**User Story:** As a security reviewer, I want a closed, explicit, public-safe field whitelist for the cached client snapshot, so that confidential fields like `ClientSecrets`, `Claims`, `Properties` cannot leak into Redis.

#### Acceptance Criteria

1. THE Tenant_Client_Cache SHALL define a DTO type (working name `ClientCacheSnapshotDto`, final name decided in Design) whose JSON serialization contains exactly the fields enumerated in Public_Safe_Fields AND no other field from `ClientDto` / `Client` entity.
2. THE Client_Cache_Snapshot SHALL NOT contain `ClientSecrets`, `Claims`, `Properties`, `IdentityProviderRestrictions`, `PairWiseSubjectSalt`, internal numeric `Client.Id`, raw `TenantRedirectPairs` payload, or any field whose name matches `*Secret*` (case-insensitive).
3. THE Client_Cache_Snapshot SHALL include a top-level envelope of shape `{ "version": <int>, "tenantKey": <string>, "clientId": <string>, "lastWriteUtc": <ISO8601 UTC>, "data": { ...Public_Safe_Fields... } }` AND SHALL set `version = 1` for this feature.
4. THE Client_Cache_Snapshot serializer SHALL use `System.Text.Json` with camelCase property naming AND SHALL NOT include null collection elements (e.g. an empty `RedirectUris` SHALL serialize as `[]` not as omitted property).
5. THE Tenant_Client_Cache SHALL reject (throw `InvalidOperationException`) at write time IF the input source object exposes any field outside Public_Safe_Fields whose value is non-null AND non-default AND whose presence in the snapshot would have leaked secret data; this is a defensive check against a future `ClientDto` refactor accidentally adding a secret-bearing field. The rejection message SHALL name the offending field BUT SHALL NOT log the field's value.
6. THE Client_Cache_Snapshot serialized payload SHALL be ≤ 256 KiB; IF a serialized snapshot exceeds this size, THEN THE Tenant_Client_Cache SHALL refuse the write, emit Audit_Event with `Outcome=WriteFailedTransient` (subreason `Oversize`), and continue (Fail_Soft).
7. THE Client_Cache_Snapshot SHALL NOT contain pretty-printed whitespace; `JsonSerializerOptions.WriteIndented` SHALL be `false`.
8. WHEN deserializing a Client_Cache_Snapshot retrieved from Distributed_Cache, THE Tenant_Client_Cache SHALL treat any unknown property as ignored AND SHALL treat `version > 1` as a Cache_Outcome `Stale` (consumer-side reaction is out-of-scope).
9. THE Client_Cache_Snapshot SHALL NOT contain the user-friendly view-helper fields of `ClientDto` such as `RedirectUrisItems`, `AllowedScopesItems`, `AllowedGrantTypesItems`, `PostLogoutRedirectUrisItems`, `AllowedCorsOriginsItems`, `IdentityProviderRestrictionsItems`, `AllowedIdentityTokenSigningAlgorithmsItems`, `AccessTokenTypes`, `RefreshTokenExpirations`, `RefreshTokenUsages`, `ProtocolTypes`, `DPoPValidationModes`, since these are MVC select-list constants, not client config.

### Requirement 3: Cache key format + tenant scoping

**User Story:** As an operator, I want every cached client snapshot key to be unambiguously scoped to a tenant, so that one tenant cannot read another tenant's client config from Redis even if they share a logical clientId.

#### Acceptance Criteria

1. THE Tenant_Client_Cache SHALL compute the per-client cache key as `"tenant-registry:" + normalize(tenantKey) + ":clients:" + normalize(clientId)`, where `normalize(tenantKey) = tenantKey.Trim().ToLowerInvariant()` AND `normalize(clientId) = clientId.Trim()`.
2. THE Tenant_Client_Cache SHALL compute the per-tenant client list cache key as `"tenant-registry:" + normalize(tenantKey) + ":clients:list"`.
3. THE Tenant_Client_Cache SHALL reject (throw `ArgumentException`) any read / write / invalidate call where `tenantKey` is null, empty, or whitespace AND SHALL NOT touch Distributed_Cache for such calls.
4. THE Tenant_Client_Cache SHALL reject (throw `ArgumentException`) any read / write / invalidate call where `clientId` is null, empty, or whitespace AND SHALL NOT touch Distributed_Cache for such calls.
5. THE Tenant_Client_Cache SHALL NOT use the bare `clientId.Trim()` key format used by the legacy `IClientScopeCacheService`; the legacy key namespace is owned by the existing `IClientScopeCacheService` AND remains untouched per Requirement 12.
6. WHEN a Duende `Client` row resolves to multiple tenant keys via Tenant_Scope_Resolution, THE Tenant_Client_Cache SHALL produce one snapshot per `(tenantKey, clientId)` tuple AND SHALL write all of them in the same Add / Update / Clone batch.
7. WHEN a Duende `Client` row resolves to zero tenant keys via Tenant_Scope_Resolution, THE Tenant_Client_Cache SHALL emit Audit_Event with `EventType="TenantClientCacheWrite"`, `Outcome="WriteSkippedDisabled"`, subreason `NoTenantScope` AND SHALL NOT write any snapshot.
8. THE Tenant_Client_Cache key namespace `tenant-registry:{tenantKey}:clients:*` SHALL NOT collide with existing keys produced by `Tenant_Infrastructure` (`tenant-registry:tenant:{tenantKey}`, `tenant-registry:public-tenant-names`, `tenant-registry:secret:{tenantKey}:{serviceName}`); the Design phase MUST verify no collision.

### Requirement 4: Write cache khi Admin UI API Add Client

**User Story:** As an Admin UI operator, I want a freshly created client to be available in the tenant cache before the HTTP 201 response returns, so that downstream callers do not miss the client until the next background refresh.

#### Acceptance Criteria

1. WHEN the Admin_Api_Host `ClientsController.Post` action successfully invokes `IClientService.AddClientAsync` AND obtains a non-zero `id`, THE controller SHALL invoke Tenant_Client_Cache to write a Client_Cache_Snapshot for every `(tenantKey, clientId)` resolved via Tenant_Scope_Resolution before returning HTTP 201.
2. WHEN Tenant_Scope_Resolution returns zero tenant keys for the new client, THE controller SHALL skip the write AND SHALL still return HTTP 201 (a non-tenant-scoped client is a valid Duende configuration).
3. WHEN the cache write succeeds for at least one `(tenantKey, clientId)`, THE Tenant_Client_Cache SHALL emit one Audit_Event per tuple with `Outcome=WriteSucceeded` AND the per-tuple `DurationMs`.
4. WHEN the cache write fails (Redis down, timeout, serialization error, oversize) for one or more tuples, THE Tenant_Client_Cache SHALL emit Audit_Event with `Outcome=WriteFailedTransient` per failing tuple at level Warning AND SHALL NOT throw; the controller SHALL still return HTTP 201 (Fail_Soft).
5. THE Admin_Api_Host SHALL apply a per-write timeout of `WriteTimeoutMs` against `Distributed_Cache.SetAsync`; IF the timeout is exceeded, THEN THE write SHALL be cancelled AND counted as `WriteFailedTransient`.
6. THE Admin_Api_Host SHALL also update the Tenant_Client_Cache_Key list key (`tenant-registry:{tenantKey}:clients:list`) for each tenant the new client belongs to, ensuring future Background_Refresh can enumerate per-tenant client ids; failure to update the list key SHALL be Fail_Soft.
7. THE Admin_Api_Host SHALL preserve the existing call to `IClientScopeCacheService.SaveAllowedScopesAsync` per Requirement 12 (backward compatibility).
8. THE controller SHALL NOT log the Client_Cache_Snapshot body in the request log; only Audit_Event metadata SHALL be logged.
9. THE controller SHALL pass `HttpContext.RequestAborted` to the Tenant_Client_Cache write call so that a client disconnect cancels the cache write without leaving the cache in a partial state for that request.

### Requirement 5: Write cache khi Admin UI API Update Client

**User Story:** As an Admin UI operator, I want every client edit to immediately replace the cached snapshot, so that mobile/SPA consumers do not see stale config until TTL expires.

#### Acceptance Criteria

1. WHEN the Admin_Api_Host `ClientsController.Put` action successfully invokes `IClientService.UpdateClientAsync`, THE controller SHALL invoke Tenant_Client_Cache to write a fresh Client_Cache_Snapshot for every `(tenantKey, clientId)` resolved by Tenant_Scope_Resolution at the moment of write before returning HTTP 204.
2. WHEN the updated client's tenant set differs from the pre-update tenant set (because `ClientTenantRedirectUris` rows were added or removed), THE controller SHALL invoke Tenant_Client_Cache to invalidate the snapshots for tenants that no longer apply AND write fresh snapshots for tenants that newly apply, all in the same request.
3. WHEN the cache write fails for any subset of `(tenantKey, clientId)` tuples, THE Tenant_Client_Cache SHALL emit Audit_Event with `Outcome=WriteFailedTransient` per failing tuple AND SHALL NOT throw; the controller SHALL still return HTTP 204 (Fail_Soft).
4. THE Tenant_Client_Cache SHALL write the snapshot using `Distributed_Cache.SetAsync` with `AbsoluteExpirationRelativeToNow = TenantClientCache:AbsoluteTtl`; if `TenantClientCache:SlidingTtl` is non-null, THE write SHALL also set `SlidingExpiration = TenantClientCache:SlidingTtl`.
5. THE Tenant_Client_Cache write SHALL be idempotent: writing the same `(tenantKey, clientId)` with the same payload twice SHALL produce the same observable cache state AND SHALL NOT create duplicate keys.
6. THE Admin_Api_Host SHALL preserve the existing call to `IClientScopeCacheService.SaveAllowedScopesAsync` per Requirement 12.
7. WHEN the update changes the `clientId` itself (renaming a client), THE controller SHALL invalidate the snapshot at the OLD `(tenantKey, oldClientId)` AND write the snapshot at the NEW `(tenantKey, newClientId)` for every tenant the client belongs to. (Note: renaming `Client.ClientId` is unusual but supported by `IClientService.UpdateClientAsync`.)
8. THE controller SHALL pass `HttpContext.RequestAborted` to the Tenant_Client_Cache write call.

### Requirement 6: Invalidate cache khi Admin UI API Delete Client

**User Story:** As an Admin UI operator, I want a deleted client to disappear from the tenant cache immediately, so that consumers cannot continue to authenticate against a removed client until TTL expires.

#### Acceptance Criteria

1. WHEN the Admin_Api_Host `ClientsController.Delete` action successfully invokes `IClientService.RemoveClientAsync`, THE controller SHALL invoke Tenant_Client_Cache to remove the snapshot for every `(tenantKey, clientId)` resolved by Tenant_Scope_Resolution from the pre-delete client view BEFORE returning HTTP 204.
2. WHEN multiple tenants reference the deleted client, THE controller SHALL invalidate every `(tenantKey, clientId)` key AND remove the `clientId` entry from each `tenant-registry:{tenantKey}:clients:list`.
3. WHEN the cache invalidate fails for one or more tuples (Redis down, timeout), THE Tenant_Client_Cache SHALL emit Audit_Event with `Outcome=InvalidateFailedTransient` per failing tuple AND SHALL NOT throw; the controller SHALL still return HTTP 204 (Fail_Soft).
4. WHEN the cache invalidate succeeds, THE Tenant_Client_Cache SHALL emit one Audit_Event per tuple with `Outcome=InvalidateSucceeded` AND the per-tuple `DurationMs`.
5. THE Admin_Api_Host SHALL preserve the existing call to `IClientScopeCacheService.RemoveAllowedScopesAsync` per Requirement 12.
6. THE controller SHALL pass `HttpContext.RequestAborted` to the Tenant_Client_Cache invalidate call.
7. THE Tenant_Client_Cache invalidate SHALL be safe to call for a `(tenantKey, clientId)` that was never written; the call SHALL be a no-op AND SHALL emit Audit_Event with `Outcome=InvalidateSucceeded` (no distinction between "key did not exist" and "key existed and was removed", since `IDistributedCache.RemoveAsync` does not surface that distinction).

### Requirement 7: Invalidate + write cache khi Admin UI API Clone Client

**User Story:** As an Admin UI operator, I want cloned clients to be cached as new entries with their own snapshot, while the source client's snapshot remains intact, so that clone-and-edit workflows do not pollute the source.

#### Acceptance Criteria

1. WHEN the Admin_Api_Host `ClientsController.PostClientClone` action successfully invokes `IClientService.CloneClientAsync` AND obtains a non-zero clone `id`, THE controller SHALL invoke Tenant_Client_Cache to write a Client_Cache_Snapshot for every `(tenantKey, newClientId)` resolved by Tenant_Scope_Resolution against the cloned client BEFORE returning HTTP 201.
2. WHEN the cloned client's tenant set is non-empty AND the source client had snapshots in Distributed_Cache, THE controller SHALL NOT invalidate the source client's snapshots; the source's cached state SHALL remain intact.
3. WHEN Tenant_Scope_Resolution returns zero tenant keys for the cloned client (e.g. clone preserved zero ClientTenantRedirectUris because `CloneClientRedirectUris = false`), THE controller SHALL skip the write AND SHALL still return HTTP 201.
4. WHEN the cache write fails for any subset of `(tenantKey, clientId)` tuples on the cloned client, THE Tenant_Client_Cache SHALL emit Audit_Event with `Outcome=WriteFailedTransient` per failing tuple AND SHALL NOT throw; the controller SHALL still return HTTP 201 (Fail_Soft).
5. THE controller SHALL pass `HttpContext.RequestAborted` to the Tenant_Client_Cache write call.

### Requirement 8: Background refresh of Client_Cache_Snapshot

**User Story:** As an operator, I want a periodic refresh that rebuilds the tenant client cache from the database, so that drift caused by missed CRUD invalidations (Redis down during write, manual DB edits) self-heals without operator action.

#### Acceptance Criteria

1. WHERE `TenantClientCache:Enabled = true`, THE Admin_Api_Host SHALL register a Background_Refresh hosted service that runs Tenant_Client_Cache refresh sweeps at intervals of `TenantClientCache:RefreshInterval`.
2. THE Background_Refresh SHALL execute a single immediate sweep on host startup BEFORE entering its periodic loop.
3. ON each sweep, THE Background_Refresh SHALL enumerate the active tenants from `Tenant_Infrastructure` (`ITenantRepository.GetTenantsAsync`) AND, for each active tenant, enumerate client ids belonging to that tenant via Tenant_Scope_Resolution AND write a fresh Client_Cache_Snapshot per `(tenantKey, clientId)` tuple.
4. THE Background_Refresh SHALL respect `TenantClientCache:MaxClientsPerTenant`; if a tenant resolves to more client ids than the limit, THE Background_Refresh SHALL log Warning with `EventType="TenantClientCacheRefresh"`, `Outcome="WriteSkippedDisabled"`, subreason `MaxClientsPerTenantExceeded`, the observed count, the configured limit AND SHALL still write the first `MaxClientsPerTenant` snapshots in the deterministic order returned by the repository.
5. WHEN the Background_Refresh observes a Redis exception during sweep, THE service SHALL log Warning AND SHALL continue to the next tenant; it SHALL NOT crash the host (Fail_Soft).
6. THE Background_Refresh SHALL emit a single Information-level summary log per sweep containing `EventType="TenantClientCacheRefreshCompleted"`, `TenantsSwept`, `ClientsWritten`, `WriteFailures`, `DurationMs`.
7. THE Background_Refresh SHALL be cancelable via the host's `IHostApplicationLifetime.ApplicationStopping` token; on cancellation it SHALL exit cleanly within `WriteTimeoutMs` of any in-flight write.
8. THE Background_Refresh implementation MAY be an extension of the existing `TenantRegistryCacheRefreshService` OR a new standalone hosted service; the choice is a Design-phase decision AND does not change the functional contract above.
9. THE Background_Refresh SHALL NOT alter the existing `TenantRegistryCacheRefreshService` semantics (`SetTenant`, `SetPublicTenantNames`); per-tenant snapshot of `TenantInfo` SHALL continue to be refreshed as today, regardless of `TenantClientCache:Enabled`.

### Requirement 9: TTL absolute + sliding behavior

**User Story:** As an operator, I want predictable cache eviction behaviour where every entry expires within the configured absolute TTL, with optional sliding TTL for high-traffic tenants, so that stale config has a hard cap on lifetime.

#### Acceptance Criteria

1. THE Tenant_Client_Cache SHALL set `DistributedCacheEntryOptions.AbsoluteExpirationRelativeToNow = TenantClientCache:AbsoluteTtl` on every Client_Cache_Snapshot write.
2. WHERE `TenantClientCache:SlidingTtl` is non-null, THE Tenant_Client_Cache SHALL also set `DistributedCacheEntryOptions.SlidingExpiration = TenantClientCache:SlidingTtl` on every Client_Cache_Snapshot write.
3. WHERE `TenantClientCache:SlidingTtl` is null, THE Tenant_Client_Cache SHALL NOT set `SlidingExpiration` (i.e. the entry expires solely based on absolute TTL).
4. THE Tenant_Client_Cache SHALL NOT mutate the cached entry's TTL on read.
5. THE Tenant_Client_Cache SHALL NOT extend the absolute TTL beyond `AbsoluteTtl` on subsequent writes; each write SHALL re-arm a fresh `AbsoluteTtl` window.
6. IF `Distributed_Cache.SetAsync` rejects the supplied `DistributedCacheEntryOptions`, THEN THE Tenant_Client_Cache SHALL emit Audit_Event with `Outcome=WriteFailedTransient` AND SHALL NOT throw (Fail_Soft).
7. THE Background_Refresh SHALL re-arm the TTL on every refresh sweep (because each refresh issues a fresh write).

### Requirement 10: Fail-soft khi Redis down

**User Story:** As an operator, I want the cache layer to never block client CRUD operations when Redis is unhealthy, so that an outage of the cache does not become an outage of Admin UI.

#### Acceptance Criteria

1. WHEN the underlying `Distributed_Cache.SetAsync` / `RemoveAsync` / `GetAsync` throws any exception, THE Tenant_Client_Cache SHALL catch the exception AND log it at level Warning with `EventType ∈ {"TenantClientCacheWrite", "TenantClientCacheInvalidate", "TenantClientCacheRefresh"}`, `Outcome=WriteFailedTransient | InvalidateFailedTransient`, the exception type AND a sanitized message that SHALL NOT contain the snapshot body OR connection string credentials.
2. THE Tenant_Client_Cache SHALL NOT re-throw a Redis / `IDistributedCache` exception out of any Add / Update / Delete / Clone code path of Admin_Api_Host `ClientsController`.
3. THE Tenant_Client_Cache SHALL NOT introduce any blocking retry loop on transient failure; one attempt per logical operation, bounded by `WriteTimeoutMs`, is the contract.
4. WHEN Distributed_Cache returns a successful response with corrupt / unparseable payload (e.g. truncated bytes from a Redis eviction race), THE Tenant_Client_Cache SHALL treat the read as a cache miss AND SHALL emit Audit_Event with `Outcome=Miss`, subreason `CorruptPayload`.
5. THE Tenant_Client_Cache SHALL NOT panic or shut down the host if `Distributed_Cache` is unreachable for the entire request lifetime.
6. THE Background_Refresh SHALL NOT crash if Distributed_Cache is unreachable for an entire sweep cycle; it SHALL log Warning AND continue to the next scheduled cycle.

### Requirement 11: Tenant_Scope_Resolution

**User Story:** As a developer integrating the cache, I want a single, reusable, deterministic mechanism for mapping a Duende `Client` row to the set of tenants it belongs to, so that the cache writes are correct without each call site re-implementing the rule.

#### Acceptance Criteria

1. THE Admin_Api_Host SHALL expose a service abstraction (working name `IClientTenantScopeResolver`, final name decided in Design) whose contract takes a `ClientDto` (or an internal projection of the same data, e.g. `ClientId` + `TenantRedirectPairs` + `Properties`) AND returns `IReadOnlyCollection<string> tenantKeys`.
2. THE `IClientTenantScopeResolver` SHALL apply Tenant_Scope_Resolution as defined in Glossary: priority (1) `ClientTenantRedirectUris` rows, priority (2) `Client.Properties[skoruba_tenant_redirect_pairs]` JSON, priority (3) zero tenants.
3. THE returned `tenantKeys` set SHALL be normalized via `tenantKey.Trim().ToLowerInvariant()` AND SHALL be de-duplicated case-insensitively.
4. THE returned `tenantKeys` set SHALL preserve a deterministic order (lexicographic ascending) so that batch writes are reproducible across runs.
5. THE `IClientTenantScopeResolver` SHALL NOT throw on a client that has zero tenant keys; it SHALL return an empty collection AND let the caller decide whether to skip the cache write.
6. THE `IClientTenantScopeResolver` SHALL NOT consume `IClientStore` from Duende (this would conflict with the Out-of-scope rule against decorating Duende's `IClientStore`); it SHALL consume `IClientService` / `IClientRepository` only.
7. THE `IClientTenantScopeResolver` SHALL be registered with `ServiceLifetime.Scoped` (matching `IClientService`).
8. WHERE the same logic is needed inside Background_Refresh (which runs in a hosted service scope), THE Background_Refresh SHALL create its own `IServiceScope` AND resolve `IClientTenantScopeResolver` from that scope, mirroring the existing `TenantRegistryCacheRefreshService` pattern.

### Requirement 12: Backward compatibility với IClientScopeCacheService

**User Story:** As the team owning the existing `IClientScopeCacheService`, I want the new tenant client cache to coexist with the existing scope cache without breaking its consumers, so that I do not need a coordinated rollout.

#### Acceptance Criteria

1. THE Admin_Api_Host SHALL preserve the existing `IClientScopeCacheService` interface AND its implementation `ClientScopeCacheService` (key = `clientId.Trim()`, value = space-separated `AllowedScopes`); the legacy contract MUST remain functionally unchanged.
2. THE Admin_Api_Host `ClientsController` SHALL continue to call `IClientScopeCacheService.SaveAllowedScopesAsync` in `Post` AND `Put` actions AND `RemoveAllowedScopesAsync` in `Delete` action, IN ADDITION TO the new Tenant_Client_Cache calls required by R4 / R5 / R6.
3. THE Admin_Api_Host SHALL NOT change the cache key format used by `IClientScopeCacheService` (bare `clientId.Trim()`).
4. THE Tenant_Client_Cache key namespace `tenant-registry:{tenantKey}:clients:*` SHALL NOT collide with the legacy `clientId.Trim()` key namespace; verification SHALL be performed in the Design phase.
5. WHERE a future feature decides to deprecate `IClientScopeCacheService`, the deprecation SHALL be a separate spec; this feature SHALL NOT remove or modify the legacy service.
6. THE Tenant_Client_Cache SHALL NOT re-use any state stored under the legacy key; if a consumer wants both the snapshot AND the legacy AllowedScopes value, the consumer SHALL read from both keys explicitly.

### Requirement 13: Audit log redaction

**User Story:** As a security auditor, I want every cache operation to produce a structured Serilog event with tenant + client identifiers but no secret content, so that I can audit cache traffic without risking secret exposure in log shipping.

#### Acceptance Criteria

1. THE Tenant_Client_Cache SHALL emit a Serilog event for every read-miss, write, invalidate, AND background refresh outcome with `EventType ∈ {"TenantClientCacheRead", "TenantClientCacheWrite", "TenantClientCacheInvalidate", "TenantClientCacheRefresh"}`, `TenantKey`, `ClientId`, `Outcome` ∈ Cache_Outcome, `DurationMs`, `SnapshotVersion`.
2. THE emitted event SHALL include `CorrelationId` from `Activity.Current?.TraceId` IF available; otherwise `null`.
3. THE emitted event SHALL NOT contain the Client_Cache_Snapshot body, `Client.ClientSecrets`, `Client.Claims`, `Client.Properties`, or any field whose name matches `*Secret*` (case-insensitive).
4. THE emitted event SHALL NOT contain the raw exception's `ToString()` for transient cache failures; only the exception type AND the first 256 chars of `ex.Message` SHALL be logged, AND any substring matching a Redis connection string pattern (e.g. `password=`, `,password=`, `auth=`) SHALL be replaced with `***`.
5. THE Tenant_Client_Cache SHALL NOT log at level Information for read-miss outcomes (those SHALL be Debug); writes, invalidates, AND refresh summaries SHALL be Information; transient failures SHALL be Warning; oversize snapshot rejections SHALL be Warning.
6. THE Tenant_Client_Cache SHALL NOT log the verbatim cache key for outcomes that contain `tenantKey`, `clientId` already as fields (avoids redundant info AND makes the structured event easier to query).
7. THE Background_Refresh sweep summary SHALL log `TenantsSwept`, `ClientsWritten`, `WriteFailures`, `DurationMs` AND SHALL NOT log per-tenant secret connection strings even on failure.

### Requirement 14: Performance budget

**User Story:** As a performance engineer, I want the cache to deliver predictable read/write latency, so that mobile/SPA bootstrap latency stays bounded and Admin CRUD is not noticeably slowed by cache writes.

#### Acceptance Criteria

1. THE Tenant_Client_Cache read path (cache hit) SHALL complete within p99 ≤ 5 ms in the in-process test bench against `MemoryDistributedCache` (the Redis-bound p99 in production is observability-tracked but not gated by this requirement; the unit test budget is the testable contract).
2. THE Tenant_Client_Cache write path SHALL complete within p99 ≤ 25 ms in the in-process test bench against `MemoryDistributedCache`.
3. THE Tenant_Client_Cache write call from `ClientsController.Post / Put / Delete / Clone` SHALL add no more than `WriteTimeoutMs` to the request latency budget per request.
4. THE Background_Refresh sweep SHALL not exceed `RefreshInterval / 2` of wall-clock time on a tenant directory of 1 000 tenants × 50 clients each in the in-process test bench; if it does, the implementation SHALL log Warning with `Outcome=WriteFailedTransient`, subreason `RefreshSweepTooLong`.
5. THE serialized Client_Cache_Snapshot SHALL be ≤ 256 KiB (matching R2.6).

### Requirement 15: Security controls

**User Story:** As a security reviewer, I want explicit assertions that the cache cannot become a leak channel for `ClientSecrets` or `Properties`, so that I can sign off without reading the entire implementation.

#### Acceptance Criteria

1. THE Tenant_Client_Cache SHALL NEVER read or write `ClientSecrets`, `Claims`, `Properties`, `IdentityProviderRestrictions`, `PairWiseSubjectSalt`, internal `Client.Id`, or any field whose name matches `*Secret*` (case-insensitive); this is repeated as a security requirement to be referenced in design review.
2. THE Tenant_Client_Cache SHALL operate over the `Distributed_Cache` instance configured by `Tenant_Infrastructure` (TLS in-transit + Redis ACL); it SHALL NOT add a self-managed encryption layer in this feature (out-of-scope).
3. THE Admin_Api_Host SHALL NOT expose any HTTP endpoint that returns Client_Cache_Snapshot content in this feature; consumer endpoints are out-of-scope.
4. THE Tenant_Client_Cache SHALL NOT include the raw `Client.Id` (numeric primary key) in the snapshot envelope, since it is a database-internal identifier irrelevant to clients.
5. WHERE the Background_Refresh fails to load a `ClientDto` due to a transient DB error, THE service SHALL emit Audit_Event with `Outcome=WriteFailedTransient` AND SHALL NOT write a partial / null snapshot to Distributed_Cache (avoids cache poisoning).

### Requirement 16: Observability

**User Story:** As an operator, I want metrics + structured logs for cache hit/miss, write outcomes, and refresh sweeps, so that I can monitor cache health and detect drift without ad-hoc Redis inspection.

#### Acceptance Criteria

1. THE Tenant_Client_Cache SHALL emit `Microsoft.Extensions.Logging` structured events conformant to R13.
2. WHERE the host has `System.Diagnostics.Metrics` configured, THE Tenant_Client_Cache SHALL emit counters `tenant_client_cache.read.hit`, `tenant_client_cache.read.miss`, `tenant_client_cache.write.success`, `tenant_client_cache.write.failure`, `tenant_client_cache.invalidate.success`, `tenant_client_cache.invalidate.failure`, `tenant_client_cache.refresh.sweep.duration_ms` (histogram), each tagged with `tenantKey` (lowercased) AND `outcome`.
3. THE metric tags SHALL NOT include raw `clientId` (high cardinality risk in metric backends); only `tenantKey` AND `outcome` SHALL be tags. Per-clientId visibility is provided via structured logs (R13).
4. THE Background_Refresh sweep SHALL update an `tenant_client_cache.refresh.last_completed_at` gauge (or equivalent observability primitive) so that operators can verify the sweep is running.

### Requirement 17: Testability

**User Story:** As a developer, I want to be able to write unit and integration tests for the tenant client cache against an in-memory `IDistributedCache`, so that I can validate behaviour without a live Redis instance.

#### Acceptance Criteria

1. THE Tenant_Client_Cache SHALL be unit-testable by substituting `Microsoft.Extensions.Caching.Distributed.IDistributedCache` with `MemoryDistributedCache` (the same pattern used by existing `Tenant_Infrastructure` tests).
2. THE Admin_Api_Host SHALL provide a test seam for `IClientTenantScopeResolver` so that tests can assert tenant-scope-resolution behaviour without spinning up `AdminConfigurationDbContext`.
3. THE Background_Refresh SHALL be testable end-to-end with an in-memory `IDistributedCache` AND a fake `IClientService` / `ITenantRepository`.
4. THE test bench SHALL include at minimum: (a) Add → snapshot present, (b) Update → snapshot replaced, (c) Delete → snapshot removed, (d) Clone → new snapshot, source intact, (e) Redis down (fake `IDistributedCache` throwing) → CRUD succeeds, Audit_Event `WriteFailedTransient` emitted, (f) snapshot oversize → write rejected with audit, (g) `Enabled = false` → no-op.
5. THE test bench SHALL include a property-style test for round-trip serialization of Client_Cache_Snapshot: for any `ClientDto` whose Public_Safe_Fields are within their value domains, `deserialize(serialize(snapshot)) == snapshot` must hold structurally; this is explicitly called out because parsers/serializers are tricky AND round-trip property testing catches whitelist drift.
6. THE test bench SHALL assert (negative test) that fields outside Public_Safe_Fields (`ClientSecrets`, `Claims`, `Properties`, `IdentityProviderRestrictions`, `Id`, `PairWiseSubjectSalt`) NEVER appear in the serialized JSON, regardless of input population.

## Non-functional Requirements

- **Performance**: see Requirement 14. p99 read ≤ 5 ms (in-process test bench), p99 write ≤ 25 ms, snapshot ≤ 256 KiB.
- **Security**: see Requirement 15. Public_Safe_Fields whitelist is hard. No self-managed encryption beyond Redis-native (TLS + ACL). No secret in logs (R13).
- **Reliability**: Fail_Soft contract per Requirement 10. Cache outage MUST NOT block Admin CRUD or Sts auth flow.
- **Observability**: structured Serilog events (R13) + metrics counters (R16). Background_Refresh emits a "last sweep completed at" signal.
- **Backward compatibility**: legacy `IClientScopeCacheService` untouched (R12). No DB schema migration. No Duende `IClientStore` decoration.
- **Multi-tenancy**: Tenant_Scope_Resolution (R11) is the single source of truth for `(tenantKey, clientId)` mapping. Snapshot keys are tenant-scoped (R3). Cross-tenant key collision is impossible by construction.
- **Maintainability**: Tenant_Client_Cache is a new service abstraction in Admin.UI.Api (cache write/invalidate side) with its DTO + serializer co-located. Background_Refresh either extends `TenantRegistryCacheRefreshService` (preferred if low-risk) or runs as a sibling hosted service.

## Out-of-scope

The following items are intentionally NOT covered by this spec and SHALL be addressed in separate specs if/when needed:

1. Public-facing read endpoint for Mobile/SPA to fetch Client_Cache_Snapshot (e.g. `GET /api/tenant-public/clients/{clientId}` on Sts_Host).
2. Redis pub/sub-based invalidation broadcast (cross-instance invalidation when one Admin_Api_Host instance writes/deletes).
3. Self-managed encryption-at-rest for snapshot content beyond Redis-native TLS / ACL.
4. Decoration of Duende `IClientStore` so that IdentityServer's authorize/token endpoint reads from this cache instead of the database (would change Duende's contract; large surface).
5. Schema migration to add a first-class `TenantKey` column on the Duende `Client` table (would invalidate Tenant_Scope_Resolution priority chain).
6. Caching of `ClientSecrets` (hashed or plaintext) in any form.
7. Caching of `Client.Properties` (may contain tenant-defined secret-bearing keys).
8. Admin UI client-side change to expose cache state to operators.
9. Deprecation / removal of the legacy `IClientScopeCacheService` (R12 mandates coexistence).

## Acceptance Criteria mapping

| Requirement | AC | Goal |
|---|---|---|
| R1 | 1.1–1.10 | Configuration + fail-fast validation + `Enabled=false` no-op |
| R2 | 2.1–2.9 | Public_Safe_Fields whitelist + envelope + serializer + size limit |
| R3 | 3.1–3.8 | Cache key format + tenant scoping + namespace isolation |
| R4 | 4.1–4.9 | Write on Add Client (Admin API) |
| R5 | 5.1–5.8 | Write on Update Client + handle tenant-set drift + rename |
| R6 | 6.1–6.7 | Invalidate on Delete Client |
| R7 | 7.1–7.5 | Write on Clone Client without disturbing source |
| R8 | 8.1–8.9 | Background_Refresh contract |
| R9 | 9.1–9.7 | TTL absolute + sliding behaviour |
| R10 | 10.1–10.6 | Fail_Soft on Redis down |
| R11 | 11.1–11.8 | Tenant_Scope_Resolution abstraction |
| R12 | 12.1–12.6 | Backward compatibility with legacy `IClientScopeCacheService` |
| R13 | 13.1–13.7 | Audit_Event redaction + log levels |
| R14 | 14.1–14.5 | Performance budget |
| R15 | 15.1–15.5 | Security controls (whitelist enforcement, no public endpoint) |
| R16 | 16.1–16.4 | Observability metrics + log + last-sweep gauge |
| R17 | 17.1–17.6 | Testability (in-memory cache, round-trip property test, negative whitelist test) |
