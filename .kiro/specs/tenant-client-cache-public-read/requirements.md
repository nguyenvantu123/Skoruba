# Requirements Document

Tenant Client Cache Public Read

## Introduction

Mục tiêu của feature này là **bổ sung mặt đọc public** cho snapshot Client cache đã được build ở spec `tenant-client-cache-expansion`. Cụ thể:

1. Thêm endpoint `GET /api/public/tenants/{tenantKey}/clients/{clientId}` trên `Skoruba.Duende.IdentityServer.Admin.UI.Api` (`Admin_Api_Host`) cho phép consumer (mobile native, SPA, downstream .NET service) đọc Public_Safe_Fields snapshot trực tiếp từ Redis distributed cache. Endpoint anonymous về mặt Duende auth, nhưng yêu cầu per-tenant API key trong header `X-Tenant-Api-Key` (so khớp SHA-256 hex lowercase với hash trong cấu hình).
2. Phát hành SDK NuGet `Skoruba.Duende.IdentityServer.TenantClientCache.Client` (project mới trong solution) gói consumer .NET với `IHttpClientFactory` + retry policy + in-memory cache theo `Cache-Control: max-age` của response, không phụ thuộc bất kỳ npm artifact nào.

Bối cảnh và ranh giới:

- **Source of truth của response payload**: Distributed_Cache (Redis) đã được spec trước build (`tenant-client-cache-expansion`). KHÔNG có DB fallback. Khi Redis miss → 404. KHÔNG có background warmup riêng cho feature này.
- **Tier truy cập**: Controller chỉ được gọi `ITenantClientCacheService.ReadSnapshotAsync(tenantKey, clientId, ct)` (đã định nghĩa trong spec trước). Public path KHÔNG được chạm vào `IClientService`, `IClientRepository`, hay bất cứ service tier nào có thể truy cập `ClientSecrets` / `Claims` / `Properties`.
- **Whitelist field**: Response chỉ chứa Public_Safe_Fields. Tham chiếu định nghĩa cứng tại spec `tenant-client-cache-expansion` Glossary entry `Public_Safe_Fields` (38 trường) AND Glossary entry `Tenant_Client_Cache_Key`. Feature này KHÔNG re-define lại whitelist; bất kỳ thay đổi whitelist nào MUST diễn ra ở spec `tenant-client-cache-expansion`.
- **Auth model**: Public_Read_Endpoint là anonymous từ góc nhìn Duende (không OAuth bearer), nhưng được gate bằng tenant-scoped API key. API_Key_Store là cấu hình `appsettings.json` section `TenantClientCachePublicRead:ApiKeys` mapping `tenantKey → sha256-hex(api_key)`. Hash algorithm cố định: SHA-256, encoding hex lowercase, không salt (chấp nhận tradeoff: mỗi tenant chỉ có một active key tại một thời điểm; rotate = thay value trong cấu hình).
- **Header vs URL**: `tenantKey` chỉ được lấy từ URL path. Header `X-Tenant-Api-Key` chỉ mang API key, không mang tenantKey. KHÔNG có cơ chế "cross-tenant lookup".
- **Rate limit**: ASP.NET Core 8 `AddRateLimiter` token bucket, partition key = `tenantKey` (sau khi normalize). Default 30 req/phút/tenant, configurable qua `TenantClientCachePublicRead:RateLimit:*`. Vượt → 429 + `Retry-After`.
- **CORS**: Allowlist explicit origin từ section `TenantClientCachePublicRead:Cors:AllowedOrigins` (mảng chuỗi). Default rỗng = không allow cross-origin (chỉ same-origin / native client). Endpoint này KHÔNG join CORS policy mặc định của Admin_Api_Host.
- **Caching headers**: Response trả `Cache-Control: public, max-age=60` AND `ETag: W/"<sha256-hex của payload bytes>"`. Khi consumer gửi `If-None-Match` khớp ETag → 304 Not Modified, không body.
- **SDK**: Project mới `Skoruba.Duende.IdentityServer.TenantClientCache.Client` (target `net8.0`), KHÔNG NuGet third-party mới ngoài `Microsoft.Extensions.Http` (đã có) và `Microsoft.Extensions.Caching.Memory` (đã có trong solution chain qua `Microsoft.Extensions.Caching.Abstractions`). Strongly-typed DTO `PublicClientSnapshot` mirror Public_Safe_Fields. Retry policy 3 lần backoff exponential, chỉ retry trên 5xx + transient HTTP exception (`HttpRequestException`, `TaskCanceledException` do timeout, `SocketException`). KHÔNG retry trên 4xx.
- **Constraints kế thừa từ spec `tenant-client-cache-expansion`**: KHÔNG NuGet third-party mới (ngoài project SDK mới trong solution); KHÔNG EF migration; KHÔNG thay đổi cache write side (R4/R5/R6/R7 của spec trước); KHÔNG cache `ClientSecrets` / `Claims` / `Properties` / `IdentityProviderRestrictions` / `PairWiseSubjectSalt`; legacy `IClientScopeCacheService` KHÔNG bị chạm.

Out-of-scope (sẽ KHÔNG làm trong feature này):

- KHÔNG public read endpoint trên `Sts_Host` (`Skoruba.Duende.IdentityServer.STS.Identity`); endpoint chỉ cư trú trên `Admin_Api_Host`.
- KHÔNG OAuth-based authentication (client_credentials / DCR) cho consumer; chỉ API key. Migration sang OAuth là spec khác.
- KHÔNG per-key revocation list (revoke = remove khỏi `appsettings.json` + reload). Persistent revocation store là spec khác.
- KHÔNG SDK cho npm / TypeScript / mobile native (Swift, Kotlin); chỉ .NET.
- KHÔNG GraphQL hay batch endpoint (`GET /api/public/tenants/{tenantKey}/clients` plural). Feature này chỉ một endpoint single-resource.
- KHÔNG tự build admin UI để quản lý API key; cấu hình thuần JSON.
- KHÔNG thêm DB fallback khi Redis miss.
- KHÔNG đổi format snapshot envelope (`{ version, tenantKey, clientId, lastWriteUtc, data }`) từ spec `tenant-client-cache-expansion`.
- KHÔNG broadcast invalidation tới SDK consumer (consumer dựa vào `max-age` của response).

## Glossary

- **Admin_Api_Host**: Tiến trình `Skoruba.Duende.IdentityServer.Admin.UI.Api` (ASP.NET Core REST API host). Là nơi `PublicReadEndpoint` cư trú.
- **Public_Read_Endpoint** / **PublicReadEndpoint**: Endpoint HTTP `GET /api/public/tenants/{tenantKey}/clients/{clientId}` được host bởi Admin_Api_Host. Thuộc một controller mới (working name `PublicTenantClientsController`, final name decided in Design). Anonymous từ góc nhìn Duende auth pipeline; gate bằng `TenantApiKey` validation middleware/filter.
- **Tenant_Client_Cache_Service** / **ITenantClientCacheService**: Service đã định nghĩa ở spec `tenant-client-cache-expansion`. Public_Read_Endpoint chỉ được phép gọi method `ReadSnapshotAsync(tenantKey, clientId, CancellationToken)` của service này. KHÔNG được chạm vào `IClientService` / `IClientRepository` từ public path.
- **Public_Safe_Fields**: Tập 38 field cố định, định nghĩa nguyên gốc tại spec `tenant-client-cache-expansion` Glossary entry `Public_Safe_Fields`. Feature này tham chiếu, KHÔNG re-define.
- **Tenant_Client_Cache_Key**: Format key Redis `tenant-registry:{tenantKey}:clients:{clientId}` định nghĩa nguyên gốc tại spec `tenant-client-cache-expansion` Glossary entry `Tenant_Client_Cache_Key`.
- **Tenant_Api_Key** / **TenantApiKey**: Chuỗi opaque string consumer gửi qua header `X-Tenant-Api-Key`. Server không lưu plaintext; chỉ lưu SHA-256 hex lowercase trong Api_Key_Store. Một tenant một active key tại một thời điểm.
- **Api_Key_Store** / **ApiKeyStore**: Section cấu hình `TenantClientCachePublicRead:ApiKeys` trong `appsettings.json` (cùng cấp với `TenantInfrastructure`, `TenantClientCache`). Shape: `{ "<tenantKey-lowercased>": "<sha256-hex-of-key>" }`. Bind qua `IOptionsMonitor<TenantClientCachePublicReadOptions>` để hỗ trợ reload nóng (`reloadOnChange = true` trong `IConfigurationBuilder` đã được host kế thừa). Store là source of truth cho feature này; KHÔNG có DB fallback.
- **Etag_Format** / **EtagFormat**: Weak ETag format `W/"<sha256-hex>"` trong đó `<sha256-hex>` là SHA-256 hex lowercase của UTF-8 bytes của response payload (response body sau serialize, trước GZip nếu có). Tính trên cùng payload với từng request (deterministic theo snapshot bytes lấy từ Redis).
- **Cache_Control_Header**: Header `Cache-Control: public, max-age=<seconds>` mà Public_Read_Endpoint gắn vào response. `<seconds>` lấy từ `TenantClientCachePublicRead:ResponseCache:MaxAgeSeconds` (default 60, range `[0, 3600]`).
- **RateLimiter_Partition** / **RateLimiterPartition**: Cơ chế ASP.NET Core 8 `AddRateLimiter` token bucket, partition key = `normalize(tenantKey)` lấy từ URL path. Configurable qua `TenantClientCachePublicRead:RateLimit:*` (xem R4).
- **Cors_Policy_Name**: Hằng `"TenantClientCachePublicRead"` đăng ký riêng qua `services.AddCors` policy, allowlist origins từ `TenantClientCachePublicRead:Cors:AllowedOrigins`. KHÔNG kế thừa default CORS policy của Admin_Api_Host.
- **Public_Client_Snapshot** / **PublicClientSnapshot**: DTO trong project SDK `Skoruba.Duende.IdentityServer.TenantClientCache.Client`. Mirror Public_Safe_Fields, dùng `System.Text.Json` camelCase. Nằm trong namespace `Skoruba.Duende.IdentityServer.TenantClientCache.Client.Models`. Là contract giữa Public_Read_Endpoint response body (`data` field của envelope) và SDK consumer.
- **Sdk_Cache_Outcome** / **Cache_Outcome**: Enum trong SDK với các giá trị `{ Hit, Miss, NotModified, NotFound, Unauthorized, RateLimited, ServiceUnavailable, TransientFailure }`. KHÁC với `Cache_Outcome` ở spec `tenant-client-cache-expansion` (server-side); enum này sống trong SDK namespace và phục vụ consumer instrumentation.
- **Sdk_Named_HttpClient**: HttpClient name `"TenantClientCachePublicRead"` đăng ký qua `IServiceCollection.AddHttpClient(...)`. Consumer gọi `httpClientFactory.CreateClient("TenantClientCachePublicRead")` để có pre-configured `BaseAddress`, `User-Agent`, retry policy.
- **Sdk_Retry_Policy**: Policy trong SDK với `MaxAttempts = 3` (1 initial + 2 retry), backoff `2^n * BaseDelay` jitterless trong scope của feature này (jitter là follow-up). `BaseDelay` configurable, default `200 ms`. Retry chỉ trên: HTTP status 5xx (500, 502, 503, 504), `HttpRequestException`, `TaskCanceledException` do timeout (`InnerException` is `TimeoutException` hoặc `request not yet started`). KHÔNG retry trên 4xx (401, 404, 429 → propagate).
- **Sdk_Memory_Cache**: `IMemoryCache` instance internal cho SDK client; entry TTL = `min(Cache_Control max-age của response, SdkOptions.MaxClientCacheTtl)`. Default `MaxClientCacheTtl = 300s`. Key = `(tenantKey-normalized, clientId-trimmed)`.
- **Snapshot_Pipeline_Disabled**: Trạng thái khi `TenantClientCache:Enabled = false` (cấu hình từ spec `tenant-client-cache-expansion`). Trong trạng thái này không có write side, do đó Public_Read_Endpoint KHÔNG thể trả snapshot có nghĩa và phải trả 503.
- **Audit_Event_Public_Read**: Structured Serilog event với `EventType ∈ {"TenantClientCachePublicRead.Hit", "TenantClientCachePublicRead.NotModified", "TenantClientCachePublicRead.Miss", "TenantClientCachePublicRead.Unauthorized", "TenantClientCachePublicRead.RateLimited", "TenantClientCachePublicRead.BadRequest", "TenantClientCachePublicRead.ServiceUnavailable"}`, `TenantKey`, `ClientId`, `Outcome`, `DurationMs`, `CorrelationId`, `RemoteIpHash` (SHA-256 hex của remote IP, để tránh log raw IP nguyên bản trong môi trường EU GDPR-sensitive — mặc định bật, có thể tắt qua cấu hình). KHÔNG log raw API key, KHÔNG log snapshot body.

## Requirements

### Requirement 1: Configuration AND API key store

**User Story:** As an operator running multi-tenant IdentityServer, I want a single configuration section that declares the public-read API keys (hashed) per tenant and validates the configuration at startup, so that I cannot accidentally deploy a configuration where a tenant has a plaintext key, an unsupported hash algorithm, or no key at all.

#### Acceptance Criteria

1. THE Admin_Api_Host SHALL read configuration section `TenantClientCachePublicRead` from `appsettings.json` AND environment variables on startup AND SHALL bind to a strongly typed `TenantClientCachePublicReadOptions` POCO.
2. THE TenantClientCachePublicReadOptions SHALL contain at minimum: `ApiKeys` (`IDictionary<string,string>`, key = tenantKey lowercased, value = SHA-256 hex lowercased), `RateLimit` sub-section, `Cors` sub-section, `ResponseCache` sub-section, `Audit` sub-section.
3. THE Admin_Api_Host SHALL apply the default value `ApiKeys = empty dictionary` WHEN the configuration key is absent.
4. IF any value in `TenantClientCachePublicRead:ApiKeys` is not a 64-character lowercased hexadecimal string, THEN THE Admin_Api_Host SHALL fail-fast at startup with an exception naming the offending tenant key BUT SHALL NOT include the offending value in the exception message.
5. IF any tenant key in `TenantClientCachePublicRead:ApiKeys` contains uppercase characters, leading whitespace, or trailing whitespace after `appsettings.json` JSON unescape, THEN THE Admin_Api_Host SHALL fail-fast at startup with an exception naming the offending tenant key.
6. THE Api_Key_Store SHALL support hot reload: WHEN the underlying `IConfiguration` reloads (via `reloadOnChange`), THE Public_Read_Endpoint SHALL pick up the new mapping on the next request without restarting the host.
7. WHERE `TenantClientCachePublicRead:ApiKeys` is empty, THE Public_Read_Endpoint SHALL still be reachable AND SHALL return HTTP 401 for every request (no tenant has a key configured); the host SHALL NOT fail-fast.
8. THE Admin_Api_Host SHALL emit a single Information-level log entry on startup containing: count of tenants with API keys configured, the configured `RateLimit`, `Cors`, `ResponseCache` values, AND SHALL NOT log any API key hash or plaintext.
9. THE TenantClientCachePublicReadOptions SHALL be registered with `IOptionsMonitor<TenantClientCachePublicReadOptions>` so that hot reload (R1.6) is observable to consumers.

### Requirement 2: Endpoint contract

**User Story:** As a consumer engineer, I want a single, well-defined HTTP endpoint that returns the public-safe client snapshot for a given tenant and client, so that mobile/SPA bootstrap is deterministic and matches the documented OpenAPI contract.

#### Acceptance Criteria

1. THE Admin_Api_Host SHALL expose `GET /api/public/tenants/{tenantKey}/clients/{clientId}` AND SHALL route the request to a controller action whose only collaborator (other than `HttpContext`-level helpers) is `ITenantClientCacheService.ReadSnapshotAsync(tenantKey, clientId, CancellationToken)`.
2. THE Public_Read_Endpoint SHALL accept `tenantKey` AND `clientId` exclusively from the URL path; the endpoint SHALL ignore any `tenantKey` or `clientId` value supplied through query string, request body, or any header other than `X-Tenant-Api-Key`.
3. THE Public_Read_Endpoint SHALL normalize the path-bound `tenantKey` via `tenantKey.Trim().ToLowerInvariant()` AND `clientId` via `clientId.Trim()` BEFORE invoking `ITenantClientCacheService.ReadSnapshotAsync`.
4. WHEN `ITenantClientCacheService.ReadSnapshotAsync` returns a Public_Safe_Fields snapshot, THE Public_Read_Endpoint SHALL serialize the snapshot envelope's `data` field (i.e. the Public_Safe_Fields object, not the outer envelope) as the HTTP response body using `System.Text.Json` with camelCase property naming AND `WriteIndented = false`.
5. THE Public_Read_Endpoint response body SHALL contain ONLY Public_Safe_Fields as defined in spec `tenant-client-cache-expansion` Glossary; the response body SHALL NOT include the envelope's `version`, `tenantKey`, `clientId`, `lastWriteUtc` directly in the body root (those are surfaced via response headers per R6).
6. THE Public_Read_Endpoint SHALL set response `Content-Type: application/json; charset=utf-8`.
7. THE Public_Read_Endpoint SHALL NOT consume `IClientService`, `IClientRepository`, `IAdminConfigurationDbContext`, or any service that has access to `Client.ClientSecrets` / `Client.Claims` / `Client.Properties` / `Client.IdentityProviderRestrictions`; static-analysis-style enforcement is captured in R12.
8. THE Public_Read_Endpoint SHALL pass `HttpContext.RequestAborted` to `ITenantClientCacheService.ReadSnapshotAsync` so that consumer disconnect cancels the Redis read.
9. THE Public_Read_Endpoint SHALL respond with HTTP 405 (Method Not Allowed) for any HTTP verb other than `GET` AND `HEAD` on the same route; `HEAD` SHALL return the same headers as `GET` with an empty body.

### Requirement 3: Authentication AND API key validation

**User Story:** As a security reviewer, I want every public-read request to be gated by a per-tenant API key whose plaintext is never persisted server-side, so that key compromise on one tenant does not affect another tenant and rotation is a single-key swap.

#### Acceptance Criteria

1. THE Public_Read_Endpoint SHALL require header `X-Tenant-Api-Key` on every request; IF the header is absent OR has an empty / whitespace-only value, THEN THE endpoint SHALL respond HTTP 401 with body `{ "error": "missing_api_key" }` AND SHALL NOT call `ITenantClientCacheService`.
2. THE Public_Read_Endpoint SHALL compute `sha256-hex-lowercase(headerValue)` AND SHALL compare the result with `Api_Key_Store[normalize(tenantKey)]` using a constant-time comparison (`CryptographicOperations.FixedTimeEquals`); IF the entry is absent OR comparison fails, THEN THE endpoint SHALL respond HTTP 401 with body `{ "error": "invalid_api_key" }` AND SHALL NOT call `ITenantClientCacheService`.
3. THE Public_Read_Endpoint SHALL NOT distinguish "tenant not registered" from "wrong key" in the HTTP response body or status code (both → 401 with `invalid_api_key`); rationale: avoid tenant enumeration by attacker observing different error codes.
4. THE Public_Read_Endpoint SHALL log Audit_Event_Public_Read with `Outcome="Unauthorized"` for every 401 response AND SHALL NOT log the raw header value, the SHA-256 hash, or the normalized `tenantKey` portion (rationale: avoid log poisoning by attacker spamming arbitrary tenantKey values; log only request-scoped `RemoteIpHash` AND `CorrelationId`).
5. THE Public_Read_Endpoint SHALL NOT cache the API key validation result across requests; every request SHALL re-validate against `IOptionsMonitor<TenantClientCachePublicReadOptions>.CurrentValue` so that hot-reload-driven revocation (R1.6) takes effect on the very next request.
6. WHERE `TenantClientCachePublicRead:Audit:LogIpHash = false`, THE Audit_Event_Public_Read SHALL omit the `RemoteIpHash` field; default value SHALL be `true`.
7. THE Public_Read_Endpoint SHALL NOT accept the API key from query string, cookie, or request body, regardless of header presence; only the `X-Tenant-Api-Key` request header is accepted.
8. THE Admin_Api_Host SHALL register API key validation as middleware OR as an ASP.NET Core authorization handler such that the validation runs BEFORE the rate limiter (R4); rationale: a missing key is HTTP 401 (cheap), and the rate limiter SHALL NOT consume tokens for unauthenticated requests.

### Requirement 4: Rate limiting

**User Story:** As an operator, I want every authenticated public-read request to be rate-limited per tenant, so that a misbehaving consumer of one tenant cannot DoS another tenant or the underlying Redis connection pool.

#### Acceptance Criteria

1. THE Admin_Api_Host SHALL register an ASP.NET Core 8 `AddRateLimiter` policy named `"TenantClientCachePublicRead"` using `RateLimitPartition.GetTokenBucketLimiter(partitionKey: normalize(tenantKey), ...)`.
2. THE rate limiter SHALL use defaults: `TokenLimit = 30`, `TokensPerPeriod = 30`, `ReplenishmentPeriod = 1 minute`, `QueueLimit = 0`, `AutoReplenishment = true`. All values SHALL be overridable via `TenantClientCachePublicRead:RateLimit:*`.
3. IF `TenantClientCachePublicRead:RateLimit:TokenLimit` is configured outside the inclusive range `[1, 10000]`, THEN THE Admin_Api_Host SHALL fail-fast at startup with an exception naming the configuration key.
4. IF `TenantClientCachePublicRead:RateLimit:ReplenishmentPeriod` is configured outside the inclusive range `[00:00:01, 01:00:00]`, THEN THE Admin_Api_Host SHALL fail-fast at startup with an exception.
5. WHEN the partition's tokens are exhausted, THE Public_Read_Endpoint SHALL respond HTTP 429 with body `{ "error": "rate_limit_exceeded" }` AND header `Retry-After: <seconds>` where `<seconds>` is `ceil(TimeUntilNextReplenishment.TotalSeconds)`; if `TimeUntilNextReplenishment` is unavailable from the limiter, the endpoint SHALL fall back to `Retry-After: 1`.
6. THE rate limiter partition key SHALL be `normalize(tenantKey)` extracted from the URL path; the partition key SHALL NOT include the API key, remote IP, or `clientId` (rationale: rate limit is per-tenant, not per-key, so a tenant rotating keys doesn't reset the budget; not per-IP, so a single tenant behind a NAT doesn't get unfairly throttled).
7. THE rate limiter SHALL run AFTER API key validation (R3.8); rationale: an unauthenticated request SHALL NOT consume tenant tokens.
8. WHEN a request is rejected by the rate limiter, THE Public_Read_Endpoint SHALL emit Audit_Event_Public_Read with `Outcome="RateLimited"` AND SHALL NOT call `ITenantClientCacheService`.
9. THE rate limiter SHALL NOT throw when `tenantKey` is malformed (R7.1 path); the rate limiter ordering MUST place malformed-input rejection (HTTP 400 from R7) BEFORE the rate limiter so that malformed requests never consume tokens.

### Requirement 5: CORS

**User Story:** As a security reviewer, I want CORS for the public-read endpoint to be a strict, explicit allowlist that defaults to empty, so that browser-based clients cannot fetch snapshots from arbitrary origins by default.

#### Acceptance Criteria

1. THE Admin_Api_Host SHALL register a CORS policy named `"TenantClientCachePublicRead"` whose allowed origins are loaded from `TenantClientCachePublicRead:Cors:AllowedOrigins` (string array).
2. THE Cors_Policy_Name policy SHALL allow only HTTP methods `GET, HEAD, OPTIONS` AND only headers `X-Tenant-Api-Key, If-None-Match, Accept`; the policy SHALL NOT allow `Cookie` or `Authorization`.
3. THE Cors_Policy_Name policy SHALL NOT allow credentials (`AllowCredentials = false`); rationale: API key in `X-Tenant-Api-Key` is sufficient and cookies are not used.
4. WHERE `TenantClientCachePublicRead:Cors:AllowedOrigins` is absent or empty, THE Cors_Policy_Name policy SHALL allow zero origins; cross-origin browser requests SHALL be rejected by the browser per CORS protocol.
5. THE Public_Read_Endpoint route SHALL be the ONLY route that uses Cors_Policy_Name policy; the policy SHALL NOT be applied as a default policy of the host.
6. IF an entry in `TenantClientCachePublicRead:Cors:AllowedOrigins` is not a valid absolute URL with scheme `https` (or `http` for `localhost` only), THEN THE Admin_Api_Host SHALL fail-fast at startup with an exception naming the offending entry.
7. THE Cors_Policy_Name policy SHALL set `Access-Control-Max-Age: 600` (10 minutes) for preflight caching; this is overridable via `TenantClientCachePublicRead:Cors:PreflightMaxAgeSeconds`, range `[0, 86400]`.
8. THE Cors_Policy_Name policy SHALL expose response headers `ETag, Cache-Control` to JavaScript callers via `Access-Control-Expose-Headers` so that browser SDKs can read ETag for `If-None-Match` follow-ups.

### Requirement 6: ETag AND cache headers

**User Story:** As a consumer engineer, I want every successful public-read response to include validators and freshness directives, so that the SDK can avoid re-downloading unchanged snapshots and respect a documented max-age.

#### Acceptance Criteria

1. WHEN `ITenantClientCacheService.ReadSnapshotAsync` returns a snapshot, THE Public_Read_Endpoint SHALL serialize the response body, compute `sha256-hex-lowercase(responseBodyBytes)`, AND set response header `ETag: W/"<sha256-hex>"`.
2. THE Public_Read_Endpoint SHALL set response header `Cache-Control: public, max-age=<seconds>` where `<seconds>` is `TenantClientCachePublicRead:ResponseCache:MaxAgeSeconds` (default `60`, range `[0, 3600]`).
3. THE Public_Read_Endpoint SHALL set response header `Vary: X-Tenant-Api-Key` so that intermediate caches do not serve a snapshot fetched with one API key to a request with a different key (defense-in-depth even though origin → tenant mapping is one-to-one).
4. WHEN the request includes header `If-None-Match: W/"<hex>"` (with or without surrounding whitespace, with or without the `W/` prefix per RFC 7232) AND the value matches the snapshot's computed ETag, THEN THE Public_Read_Endpoint SHALL respond HTTP 304 Not Modified with empty body AND SHALL include the `ETag`, `Cache-Control`, `Vary` headers identical to the would-be 200 response.
5. WHEN the request includes header `If-None-Match: *`, THEN THE Public_Read_Endpoint SHALL respond HTTP 304 Not Modified (RFC 7232 wildcard semantics for `GET`).
6. THE Public_Read_Endpoint SHALL also set response header `X-Snapshot-Last-Write-Utc: <ISO8601 UTC>` populated from the snapshot envelope's `lastWriteUtc` field (defined in spec `tenant-client-cache-expansion` R2.3), so that consumers can observe staleness independent of server clock.
7. THE Public_Read_Endpoint SHALL set response header `X-Snapshot-Version: <int>` populated from the snapshot envelope's `version` field; consumers SHALL treat any version greater than the SDK's known maximum as a Sdk_Cache_Outcome `TransientFailure` (R11).
8. THE ETag computation SHALL be deterministic: serializing the same Public_Safe_Fields snapshot twice MUST produce the same byte sequence (System.Text.Json with fixed property order via DTO declaration order, no random whitespace, no varying culture).
9. THE Public_Read_Endpoint SHALL NOT include `Last-Modified` header (rationale: `lastWriteUtc` is exposed via custom header, and ETag is the canonical validator; `Last-Modified` adds redundancy with second-level granularity).

### Requirement 7: Failure responses

**User Story:** As a consumer engineer, I want a documented, exhaustive set of failure responses with stable status codes and machine-readable error strings, so that my SDK can handle every failure mode deterministically.

#### Acceptance Criteria

1. IF the URL path-bound `tenantKey` is null, empty, whitespace-only, longer than 128 characters, OR contains any character not matching the regex `^[a-z0-9_-]+$` (after `Trim().ToLowerInvariant()`), THEN THE Public_Read_Endpoint SHALL respond HTTP 400 with body `{ "error": "invalid_tenant_key" }` AND SHALL NOT call `ITenantClientCacheService`.
2. IF the URL path-bound `clientId` is null, empty, whitespace-only, OR longer than 200 characters (after `Trim()`), THEN THE Public_Read_Endpoint SHALL respond HTTP 400 with body `{ "error": "invalid_client_id" }` AND SHALL NOT call `ITenantClientCacheService`.
3. WHEN `ITenantClientCacheService.ReadSnapshotAsync` returns "no snapshot found" (Redis cache miss), THE Public_Read_Endpoint SHALL respond HTTP 404 with body `{ "error": "snapshot_not_found" }`; the response SHALL NOT distinguish "snapshot never written" from "snapshot expired" (both → 404 with the same body).
4. WHEN `ITenantClientCacheService.ReadSnapshotAsync` reports that the snapshot pipeline is disabled (`TenantClientCache:Enabled = false`, Snapshot_Pipeline_Disabled state from spec `tenant-client-cache-expansion`), THE Public_Read_Endpoint SHALL respond HTTP 503 with body `{ "error": "snapshot_pipeline_disabled" }` AND header `Retry-After: 60`.
5. WHEN `ITenantClientCacheService.ReadSnapshotAsync` throws (Redis transient failure, parse error from corrupt payload), THE Public_Read_Endpoint SHALL respond HTTP 503 with body `{ "error": "snapshot_unavailable" }` AND header `Retry-After: 5`; the endpoint SHALL emit Audit_Event_Public_Read with `Outcome="ServiceUnavailable"` AND SHALL NOT include the underlying exception type or message in the response.
6. THE error response body shape SHALL be exactly `{ "error": "<machine_readable_string>" }` with no additional fields; the schema is closed (consumers SHALL NOT depend on additional fields existing).
7. THE Public_Read_Endpoint SHALL NOT redirect (3xx) under any failure; all failures SHALL be 4xx or 5xx.
8. THE Public_Read_Endpoint SHALL NOT return HTTP 500 under any documented failure mode; an unhandled `Exception` falling through MUST be transformed into HTTP 503 with body `{ "error": "snapshot_unavailable" }` by a feature-scoped exception filter.
9. WHERE the request matches an unrecognized route segment under `/api/public/tenants/...` (e.g. typo in `clientId`), THE Admin_Api_Host SHALL respond with the framework default 404 (no JSON body required); rationale: unmapped routes are out-of-scope for the contract above.

### Requirement 8: Observability

**User Story:** As an operator, I want every public-read request and every failure mode to emit a structured log AND a metric, so that I can monitor consumer adoption, error rates, and per-tenant throughput without ad-hoc Redis inspection.

#### Acceptance Criteria

1. THE Public_Read_Endpoint SHALL emit Audit_Event_Public_Read for every terminal request outcome (200, 304, 400, 401, 404, 405, 429, 503) with the fields enumerated in Glossary entry `Audit_Event_Public_Read`.
2. THE Audit_Event_Public_Read SHALL log at level Information for `Outcome ∈ {"Hit", "NotModified"}`, level Debug for `Outcome="Miss"` (404), level Warning for `Outcome ∈ {"Unauthorized", "RateLimited", "BadRequest"}`, level Error for `Outcome="ServiceUnavailable"` (503).
3. THE Public_Read_Endpoint SHALL emit metrics via `System.Diagnostics.Metrics.Meter` named `"TenantClientCache"` (the same Meter used by spec `tenant-client-cache-expansion` R16; a new Meter SHALL NOT be introduced) with the following NEW counters: `tenant_client_cache.public_read.hit`, `tenant_client_cache.public_read.not_modified`, `tenant_client_cache.public_read.miss`, `tenant_client_cache.public_read.unauthorized`, `tenant_client_cache.public_read.rate_limited`, `tenant_client_cache.public_read.bad_request`, `tenant_client_cache.public_read.service_unavailable`.
4. EACH counter from R8.3 SHALL be tagged with `tenantKey` (lowercased) for `Hit / NotModified / Miss / RateLimited / ServiceUnavailable` outcomes; for `Unauthorized / BadRequest` outcomes, the `tenantKey` tag SHALL be omitted (rationale: prevent cardinality explosion + tenant-key enumeration via metrics, mirroring R3.4).
5. THE Public_Read_Endpoint SHALL emit a histogram `tenant_client_cache.public_read.duration_ms` tagged with `outcome` AND (where applicable per R8.4) `tenantKey`.
6. THE Audit_Event_Public_Read SHALL include `CorrelationId` from `Activity.Current?.TraceId` IF available; otherwise `null`.
7. THE Audit_Event_Public_Read SHALL NOT contain the raw `X-Tenant-Api-Key` value, the SHA-256 hash of the key, the response body, the snapshot envelope, or any field whose name matches `*Secret*` (case-insensitive).
8. THE Public_Read_Endpoint SHALL produce no per-request log entry above level Debug for the snapshot bytes themselves (the body SHALL never appear in any log).

### Requirement 9: Threat model

**User Story:** As a security reviewer, I want explicit assertions about how the public-read endpoint resists DoS, key leak, and tenant enumeration attacks, so that I can sign off without re-deriving the threat model from the implementation.

#### Acceptance Criteria

1. THE Public_Read_Endpoint SHALL resist tenant enumeration: HTTP 401 (missing or invalid API key) SHALL be returned regardless of whether `tenantKey` is registered in `Api_Key_Store`; both cases SHALL produce identical response status, body, and (within constant-time comparison constraints) timing characteristics, per R3.2 AND R3.3.
2. THE Public_Read_Endpoint SHALL resist API-key-mass-enumeration via brute-force: per-tenant rate limit (R4, default 30 req/min) AND per-IP additional defense via host-level upstream (out-of-scope of this spec but explicitly delegated to operator's reverse proxy / WAF) SHALL be the documented mitigation; this requirement records the assumption.
3. THE Public_Read_Endpoint SHALL resist log-poisoning via attacker-controlled `tenantKey` AND `X-Tenant-Api-Key` values: 401 / 400 logs SHALL NOT contain raw `tenantKey` (R3.4 + R7.1) AND SHALL NOT contain raw header values (R3.4 + R8.7).
4. THE Public_Read_Endpoint SHALL NOT allow snapshot scraping by a single API key against arbitrary `clientId` values beyond the rate limit; at default 30 req/min, harvesting a 1000-client tenant would take ~33 minutes per attacker IP, which combined with the operator's reverse-proxy IP rate limit is the documented threshold.
5. THE Public_Read_Endpoint SHALL NOT include any field in the response that, by combination, reveals client secret material; this is guaranteed structurally because the response body is the `data` field of the snapshot envelope, AND the snapshot envelope itself is gated by Public_Safe_Fields (spec `tenant-client-cache-expansion` R2 AND R15).
6. THE Public_Read_Endpoint SHALL NOT log raw remote IP by default; instead `RemoteIpHash = sha256-hex(remoteIp + per-host salt)` SHALL be logged where the salt is `TenantClientCachePublicRead:Audit:RemoteIpSalt` (default value MUST be a non-empty random string generated AND persisted per host on first run, NOT a constant default).
7. IF the Public_Read_Endpoint is called over plain HTTP (not HTTPS) AND the host is not bound to a `localhost` address, THEN THE Admin_Api_Host SHALL respond HTTP 400 with body `{ "error": "https_required" }` BEFORE running API key validation (rationale: an API key sent over HTTP is already compromised; refuse early).
8. THE Public_Read_Endpoint SHALL set response header `X-Content-Type-Options: nosniff` AND `Cache-Control: ..., no-transform` to prevent intermediate transformations from altering the snapshot bytes (which would invalidate the ETag).

### Requirement 10: SDK contract

**User Story:** As a consumer engineer using .NET, I want a typed, dependency-injected SDK that wraps the public-read endpoint with `IHttpClientFactory`, so that I can call the endpoint without re-implementing HTTP plumbing, ETag handling, or retry policy in every consumer service.

#### Acceptance Criteria

1. THE solution SHALL contain a NEW project `Skoruba.Duende.IdentityServer.TenantClientCache.Client` targeting `net8.0` packed as a NuGet package; the project SHALL reference ONLY `Microsoft.Extensions.Http`, `Microsoft.Extensions.Caching.Memory`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions`, AND `System.Text.Json` (all packages already present in the solution's transitive set; no new third-party NuGet introduced).
2. THE SDK SHALL expose an extension method `IServiceCollection AddTenantClientCacheClient(this IServiceCollection services, Action<TenantClientCacheClientOptions> configure)` that registers Sdk_Named_HttpClient (`"TenantClientCachePublicRead"`), Sdk_Memory_Cache, Sdk_Retry_Policy, AND a public interface `ITenantClientCacheClient`.
3. THE SDK SHALL expose interface `ITenantClientCacheClient` with method `Task<TenantClientSnapshotResult> GetClientAsync(string tenantKey, string clientId, CancellationToken ct = default)` AND method `Task<TenantClientSnapshotResult> GetClientAsync(string tenantKey, string clientId, string? ifNoneMatch, CancellationToken ct = default)`.
4. THE return type `TenantClientSnapshotResult` SHALL be a sealed record with fields: `PublicClientSnapshot? Snapshot` (null when `Outcome ∈ {NotFound, Unauthorized, RateLimited, ServiceUnavailable, NotModified, TransientFailure}`), `string? Etag`, `DateTimeOffset? LastWriteUtc`, `int? Version`, `Sdk_Cache_Outcome Outcome`, `TimeSpan? RetryAfter` (set for `RateLimited`, `ServiceUnavailable`).
5. THE `PublicClientSnapshot` DTO SHALL contain exactly the Public_Safe_Fields properties as defined in spec `tenant-client-cache-expansion` Glossary; the DTO SHALL use `System.Text.Json` source-generation OR reflection-based deserialization (decision deferred to Design phase) AND SHALL use camelCase `[JsonPropertyName]` mappings.
6. THE SDK SHALL inject `IHttpClientFactory` AND retrieve the named client `"TenantClientCachePublicRead"`; the SDK SHALL NOT instantiate `HttpClient` directly.
7. THE SDK options `TenantClientCacheClientOptions` SHALL contain at minimum: `Uri BaseAddress` (required, validated as absolute https URL except when `BaseAddress.Host` is `localhost`), `string ApiKey` (required, plaintext, sent as `X-Tenant-Api-Key`), `TimeSpan HttpTimeout` (default `5s`, range `[1s, 60s]`), `int MaxRetryAttempts` (default `2` retries i.e. 3 total attempts, range `[0, 5]`), `TimeSpan RetryBaseDelay` (default `200ms`, range `[10ms, 5s]`), `TimeSpan MaxClientCacheTtl` (default `300s`, range `[0s, 3600s]`), `bool EnableInMemoryCaching` (default `true`).
8. THE SDK SHALL throw `ArgumentException` from `AddTenantClientCacheClient` IF `BaseAddress` is null, not absolute, OR uses a scheme other than `https` while `Host` is not `localhost`.
9. THE SDK SHALL set HTTP request header `User-Agent: Skoruba.Duende.IdentityServer.TenantClientCache.Client/<assembly-version>` on every request.
10. THE SDK SHALL NOT log the API key, the response body, or the SHA-256 hash of the API key under any log level; SDK structured logs SHALL include only `tenantKey`, `clientId`, `Sdk_Cache_Outcome`, `DurationMs`, optional HTTP status code, AND retry attempt number.
11. THE SDK SHALL NOT introduce any global static state (`HttpClient` singleton, `MemoryCache` singleton); all collaborators SHALL be obtained through DI so that consumer applications can compose multiple `ITenantClientCacheClient` instances pointing at different `BaseAddress` (multi-region deployments).

### Requirement 11: SDK retry, backoff, AND in-memory caching

**User Story:** As a consumer engineer, I want the SDK to transparently retry transient failures, respect server-supplied freshness directives, and coalesce repeated lookups, so that my consumer service is resilient and Redis read load on the server remains predictable.

#### Acceptance Criteria

1. THE SDK SHALL retry the HTTP request up to `MaxRetryAttempts` (default 2 retries, 3 total attempts) WHEN the response status is `5xx ∈ {500, 502, 503, 504}` OR an `HttpRequestException` is thrown OR a `TaskCanceledException` is thrown due to `HttpClient` timeout (NOT due to caller-supplied `CancellationToken` cancellation).
2. THE SDK SHALL NOT retry on HTTP status `4xx ∈ {400, 401, 403, 404, 405, 429}`; these statuses SHALL be surfaced to the caller immediately as the corresponding `Sdk_Cache_Outcome`.
3. THE SDK retry backoff SHALL be `RetryBaseDelay * 2^(attempt - 1)` capped at `min(60 seconds, RetryBaseDelay * 2^MaxRetryAttempts)`; jitter SHALL NOT be added in this version (deferred to follow-up to keep determinism in tests).
4. WHEN the response includes header `Retry-After: <seconds>` on HTTP 429 OR 503, THE SDK SHALL surface that value as `TenantClientSnapshotResult.RetryAfter`; the SDK SHALL NOT automatically wait `Retry-After` AND retry (the caller decides; rationale: a misconfigured server returning `Retry-After: 600` would otherwise hang the SDK call).
5. WHEN the caller-supplied `CancellationToken` is cancelled mid-retry, THE SDK SHALL stop retrying AND propagate `OperationCanceledException` (NOT wrap it in `Sdk_Cache_Outcome.TransientFailure`).
6. WHERE `EnableInMemoryCaching = true`, THE SDK SHALL cache successful (200 OK) responses keyed by `(normalize(tenantKey), trim(clientId))` for `min(Cache-Control max-age of response, MaxClientCacheTtl)`; cache TTL of `0` SHALL behave as no-cache.
7. WHEN a cached entry exists AND has not expired, THE `GetClientAsync` call SHALL return `Sdk_Cache_Outcome.Hit` from the SDK cache without issuing an HTTP request; the SDK SHALL still emit a log event with `Outcome="Hit"` AND `Source="local"`.
8. WHEN a cached entry exists AND has not expired AND the caller passes an explicit `ifNoneMatch` argument, THE SDK SHALL bypass the local cache for that call AND issue the HTTP request with the supplied `If-None-Match` header (rationale: caller knows local cache might be stale and is invoking explicit revalidation).
9. WHERE the SDK has a cached entry with an ETag AND `EnableInMemoryCaching = true`, on cache expiry THE SDK SHALL issue a revalidation request with `If-None-Match: <cached-etag>`; on HTTP 304 the SDK SHALL extend the cache entry's TTL by the new response's `max-age` AND return `Sdk_Cache_Outcome.NotModified` (with `Snapshot = <previously cached snapshot>` so caller still has data).
10. THE SDK SHALL NOT mix snapshots across `(tenantKey, clientId)` pairs in its in-memory cache; cache key collision SHALL be impossible by construction.
11. THE SDK SHALL emit metrics via `System.Diagnostics.Metrics.Meter` named `"Skoruba.Duende.IdentityServer.TenantClientCache.Client"` with counters `client.read.hit_local`, `client.read.hit_remote`, `client.read.not_modified`, `client.read.miss`, `client.read.unauthorized`, `client.read.rate_limited`, `client.read.service_unavailable`, `client.read.transient_failure`, `client.read.retry_attempted` AND histogram `client.read.duration_ms` tagged with `outcome` (NOT tagged with `tenantKey` to avoid cardinality explosion in consumer telemetry; consumers wanting per-tenant breakdown can dimension via structured logs).
12. THE SDK SHALL set `HttpClient.Timeout = HttpTimeout` on the named HttpClient; longer-running operations SHALL be controlled by the caller's `CancellationToken` (not by mutating per-request `HttpClient.Timeout`, which is illegal on a shared `HttpClient`).

### Requirement 12: Backward compatibility AND non-impact

**User Story:** As an operator running the existing Admin_Api_Host workload, I want this feature to add the public-read endpoint without changing any existing CRUD behaviour, write-side semantics, or legacy `IClientScopeCacheService` usage, so that I can roll the upgrade out without coordinating with downstream Admin UI / CRUD clients.

#### Acceptance Criteria

1. THE Public_Read_Endpoint feature SHALL NOT modify any existing controller in Admin_Api_Host (`ClientsController`, `IdentityResourcesController`, `ApiResourcesController`, `ApiScopesController`, `KeysController`, etc.); the feature is purely additive (new controller + new infrastructure registration).
2. THE Public_Read_Endpoint feature SHALL NOT modify the cache write side defined in spec `tenant-client-cache-expansion` (R4–R7); no changes to `ClientsController.Post / Put / Delete / PostClientClone` invocation of `Tenant_Client_Cache`.
3. THE Public_Read_Endpoint feature SHALL NOT modify `ITenantClientCacheService.WriteSnapshotAsync` / `InvalidateSnapshotAsync`; only `ReadSnapshotAsync` (read path) is consumed.
4. THE Public_Read_Endpoint feature SHALL NOT modify or remove the legacy `IClientScopeCacheService`; the legacy contract SHALL continue to coexist per spec `tenant-client-cache-expansion` R12.
5. THE Public_Read_Endpoint feature SHALL NOT introduce an EF Core migration; `Api_Key_Store` is configuration-only.
6. THE Public_Read_Endpoint feature SHALL NOT introduce a new third-party NuGet package in Admin_Api_Host beyond what is already referenced; the SDK project (R10.1) is in-solution and uses only already-referenced packages.
7. THE Public_Read_Endpoint feature SHALL NOT extend the Public_Safe_Fields whitelist; if a consumer requires an additional field, the change SHALL be made in spec `tenant-client-cache-expansion` first, AND only after the snapshot envelope `version` is bumped and write side is updated.
8. THE Public_Read_Endpoint feature SHALL NOT change Snapshot_Pipeline behaviour when `TenantClientCache:Enabled = false`; the Public_Read_Endpoint SHALL respond 503 (R7.4) AND SHALL NOT alter the write side.
9. THE Public_Read_Endpoint feature SHALL NOT change OpenAPI surface for any existing endpoint; the new endpoint SHALL appear under a separate tag `"PublicTenantClients"` in the generated OpenAPI document AND SHALL NOT be merged into existing `"Clients"` tag.
10. THE Public_Read_Endpoint feature SHALL NOT alter any existing authentication / authorization policy of Admin_Api_Host; the Public_Read_Endpoint route SHALL be opted out of the host's default authentication policy via `AllowAnonymous` AND SHALL be gated solely by the API key middleware (R3) AND rate limiter (R4).

## Non-functional Requirements

- **Performance**: p99 read latency (cache hit) of Public_Read_Endpoint SHALL match the upstream `ITenantClientCacheService.ReadSnapshotAsync` latency budget (spec `tenant-client-cache-expansion` R14.1 + Redis network) plus ≤ 5 ms for ETag computation AND header writing in the in-process test bench. SDK retry budget SHALL keep p99 wall-clock ≤ `HttpTimeout * (1 + 2)` per call when transient failures occur (3 attempts).
- **Security**: API key plaintext SHALL never persist server-side (R1); SHA-256 hex lowercase only (R3.2). Constant-time comparison (R3.2). No tenant enumeration via 401 differentiation (R3.3). HTTPS required (R9.7). No raw IP / API key in logs (R3.4, R9.6, R10.10). Public_Safe_Fields whitelist is inherited and immutable in this feature (R12.7).
- **Reliability**: Fail_Soft of write side (spec `tenant-client-cache-expansion` R10) MUST NOT block this read side; conversely, Public_Read_Endpoint failures MUST NOT block CRUD / write side (R12.1, R12.2). 503 with `Retry-After` (R7.4, R7.5) is the contract for upstream unavailability.
- **Observability**: Structured Serilog event per outcome (R8.1, R8.2). Metrics counters reuse existing Meter `"TenantClientCache"` (R8.3). SDK uses its own Meter (R11.11) with no `tenantKey` tag. Histograms for duration (R8.5, R11.11). RemoteIpHash optional (R3.6, R9.6).
- **Backward compatibility**: No change to write side (R12.2, R12.3), no change to legacy services (R12.4), no DB migration (R12.5), no new NuGet (R12.6), no whitelist change (R12.7), additive only (R12.1, R12.9, R12.10).
- **Multi-tenancy**: tenantKey from URL only (R2.2), normalized (R2.3), gated by per-tenant API key (R3). Rate limit partitioned per-tenant (R4.6). API key store hot-reloadable for revocation (R1.6, R3.5).
- **Maintainability**: Public_Read_Endpoint controller is a single new file in Admin_Api_Host. SDK is a single new project. No cross-cutting refactor required (R12).
- **Distributability**: SDK project SHALL be packable as NuGet (`<IsPackable>true</IsPackable>` in csproj); package metadata (PackageId, Authors, Description, RepositoryUrl) SHALL be set; per AGENTS.md Out-of-scope rules, actual publication to a feed is operator-driven, not feature-driven.

## Out-of-scope

The following items are intentionally NOT covered by this spec and SHALL be addressed in separate specs if/when needed:

1. OAuth-based authentication (client_credentials grant on STS) for the public-read endpoint as an alternative to API key.
2. Persistent revocation list / per-key TTL / per-key audit trail (current design = remove from `appsettings.json` + reload).
3. Plural / batch endpoint `GET /api/public/tenants/{tenantKey}/clients` returning multiple snapshots in one response.
4. Public-read endpoint hosted on `Sts_Host` instead of (or in addition to) `Admin_Api_Host`.
5. Non-.NET SDKs (TypeScript / Swift / Kotlin); only .NET SDK in this feature.
6. Server-Sent Events / WebSocket push for snapshot invalidation to SDK consumers (consumers rely on `Cache-Control: max-age` polling).
7. DB fallback when Redis is unavailable (current behaviour: 503).
8. Snapshot envelope `version` migration logic in the SDK beyond surfacing `Sdk_Cache_Outcome.TransientFailure` for unknown versions.
9. CORS preflight credential support (`AllowCredentials = true`); explicitly disallowed in R5.3.
10. Admin UI panel to inspect / rotate API keys; configuration is JSON-only in this feature.
11. Per-IP rate limiting (delegated to operator's reverse proxy / WAF per R9.2).
12. Bypass of HTTPS requirement for non-localhost hosts (R9.7).
13. Extending Public_Safe_Fields with additional fields (must happen in spec `tenant-client-cache-expansion`).

## Acceptance Criteria mapping

| Requirement | AC | Goal |
|---|---|---|
| R1 | 1.1–1.9 | Configuration + API key store + hot reload + fail-fast validation |
| R2 | 2.1–2.9 | Endpoint contract (route, layering, response shape) |
| R3 | 3.1–3.8 | Auth + API key validation (constant-time, anti-enumeration) |
| R4 | 4.1–4.9 | Rate limit per tenant (token bucket, ordering before service call) |
| R5 | 5.1–5.8 | CORS allowlist + restricted methods/headers + preflight |
| R6 | 6.1–6.9 | ETag + Cache-Control + If-None-Match + 304 |
| R7 | 7.1–7.9 | Failure responses (400, 401, 404, 405, 429, 503) |
| R8 | 8.1–8.8 | Observability (logs + metrics, redaction) |
| R9 | 9.1–9.8 | Threat model (DoS, key leak, enumeration, log poisoning, HTTPS) |
| R10 | 10.1–10.11 | SDK contract (project, DI, options, DTO, no-static-state) |
| R11 | 11.1–11.12 | SDK retry + backoff + in-memory cache + revalidation |
| R12 | 12.1–12.10 | Backward compatibility (no write-side change, no migration, additive) |
