# Design Document

Tenant Client Cache Public Read

## Overview

Feature này xây dựng **mặt đọc public** của snapshot Client cache đã được spec `tenant-client-cache-expansion` build. Nó **không** tạo write-side mới, **không** tạo read-side mới ở tier service, **không** thêm DB fallback. Toàn bộ phụ thuộc data nằm sau `ITenantClientCacheService.ReadSnapshotAsync(tenantKey, clientId, ct)` đã có sẵn (R12.3).

Hai deliverable cốt lõi:

1. **Server-side controller pipeline** (in-process trong host `Skoruba.Duende.IdentityServer.Admin.UI.Api`, gọi tắt `Admin_Api_Host`): expose route `GET /api/public/tenants/{tenantKey}/clients/{clientId}` với chuỗi filter HTTPS → CORS → API key → Rate limit → Controller → ETag negotiation → Audit log + Metrics. Pipeline thuần additive, không đụng controller / policy hiện hữu (R12.1, R12.10). Tất cả file mới nằm dưới `src/Skoruba.Duende.IdentityServer.Admin.UI.Api/Services/PublicTenantClients/` ngoại trừ controller (`Controllers/`) và một mở rộng nhỏ trong `Helpers/StartupHelpers.cs`.

2. **SDK NuGet project** mới `Skoruba.Duende.IdentityServer.TenantClientCache.Client` (target `net8.0`, `IsPackable=true`) sống trong solution. SDK gói `IHttpClientFactory` + native retry loop + `IMemoryCache` revalidation cho consumer .NET, KHÔNG phụ thuộc third-party NuGet mới (R10.1, R12.6). Output là một artifact NuGet, KHÔNG có npm/TS counterpart (out-of-scope).

Phụ thuộc kế thừa từ `tenant-client-cache-expansion` (đọc thiết kế parent để hiểu chi tiết):

- `ITenantClientCacheService` đã định nghĩa sẵn ba method `ReadSnapshotAsync / WriteSnapshotAsync / InvalidateSnapshotAsync` + envelope `ClientCacheSnapshotEnvelope { Version, TenantKey, ClientId, LastWriteUtc, Data }`. Public_Read_Endpoint chỉ chạm `ReadSnapshotAsync` (R2.1, R12.3).
- Public_Safe_Fields whitelist (38 trường) định nghĩa nguyên gốc tại spec parent Glossary; feature này KHÔNG re-define (R12.7).
- Meter `"TenantClientCache"` đã tồn tại; feature này thêm 7 counter + 1 histogram MỚI bên trong meter đó (R8.3) thay vì tạo Meter mới.
- Snapshot_Pipeline_Disabled state (`TenantClientCache:Enabled = false`) đã có sẵn; Public_Read_Endpoint phải chuyển nó thành 503 (R7.4, R12.8).

Phạm vi nằm ngoài (đã chốt requirements, lặp lại để bám phạm vi khi review):

- KHÔNG read endpoint trên Sts_Host.
- KHÔNG OAuth client_credentials.
- KHÔNG persistent revocation list.
- KHÔNG plural / batch endpoint.
- KHÔNG Admin UI panel cho API key.
- KHÔNG DB fallback.
- KHÔNG envelope/contract change từ parent spec.
- KHÔNG SDK ngoài .NET.

### Goals

1. Một và chỉ một route mới: `GET /api/public/tenants/{tenantKey}/clients/{clientId}` (HEAD cùng pipeline, R2.9).
2. Per-tenant API key gating bằng SHA-256 hex constant-time, hot-reloadable (R1, R3).
3. Per-tenant token-bucket rate limit (R4) sau API key validation.
4. Strict CORS allowlist mặc định rỗng (R5).
5. Validator + ETag + `If-None-Match` 304 negotiation (R6).
6. Đầy đủ outcome: 200 / 304 / 400 / 401 / 404 / 405 / 429 / 503 (R7).
7. Audit log structured + metrics tags an toàn cardinality (R8).
8. Threat model fully covered: enumeration, log poisoning, HTTPS, IP hashing, content-type sniffing (R9).
9. SDK consumer có `IHttpClientFactory` + native retry + `IMemoryCache` + ETag revalidation, không global static (R10, R11).
10. Không break write side / legacy `IClientScopeCacheService` / OpenAPI surface hiện hữu (R12).

### Non-goals (đã chốt requirements)

KHÔNG public-read trên Sts_Host, KHÔNG OAuth, KHÔNG batch, KHÔNG GraphQL, KHÔNG persistent revocation, KHÔNG admin UI cho API key, KHÔNG DB fallback, KHÔNG đổi Public_Safe_Fields, KHÔNG đổi snapshot envelope, KHÔNG SDK ngoài .NET, KHÔNG broadcast invalidation.

## Architecture

### Request pipeline (sequence)

```mermaid
sequenceDiagram
    participant Cli as Consumer (mobile / SPA / .NET service)
    participant LB as Reverse proxy / WAF
    participant AspNet as ASP.NET Core (Admin_Api_Host)
    participant HttpsF as HttpsRequiredFilter
    participant Cors as CORS middleware<br/>(policy="TenantClientCachePublicRead")
    participant Auth as TenantApiKeyAuthorizationFilter
    participant Rate as RateLimiter middleware<br/>(policy="TenantClientCachePublicRead")
    participant Ctl as PublicTenantClientsController
    participant Svc as ITenantClientCacheService
    participant Redis as IDistributedCache (Redis)

    Cli->>LB: GET /api/public/tenants/{t}/clients/{c}<br/>X-Tenant-Api-Key: <plaintext>
    LB->>AspNet: forward (HTTPS terminated upstream)
    AspNet->>HttpsF: OnAuthorizationAsync
    alt non-https + non-localhost
        HttpsF-->>Cli: 400 {"error":"https_required"}<br/>(R9.7)
    else
        HttpsF->>Cors: continue
        Cors->>Auth: continue (CORS preflight handled<br/>per R5; no AllowCredentials)
        Auth->>Auth: validate header presence,<br/>compute sha256 hex,<br/>FixedTimeEquals vs IOptionsMonitor<br/>(R3.1, R3.2, R3.5)
        alt missing/invalid
            Auth-->>Cli: 401 {"error":"missing_api_key"<br/>| "invalid_api_key"} (R3.1, R3.2, R3.3)<br/>tenantKey tag NOT emitted (R8.4)
        else valid
            Auth->>Rate: continue (R3.8)
            alt token bucket empty
                Rate-->>Cli: 429 {"error":"rate_limit_exceeded"}<br/>+ Retry-After (R4.5)
            else token consumed
                Rate->>Ctl: route to action
                Ctl->>Ctl: validate path tenantKey<br/>(R7.1) + clientId (R7.2)
                alt invalid path
                    Ctl-->>Cli: 400 {"error":"invalid_..."}<br/>(R7.1, R7.2)
                else
                    Ctl->>Svc: ReadSnapshotAsync(tenantKey, clientId,<br/>HttpContext.RequestAborted) (R2.1, R2.8)
                    Svc->>Redis: GetAsync(logical key)
                    alt pipeline disabled
                        Svc-->>Ctl: SnapshotPipelineDisabled
                        Ctl-->>Cli: 503 {"error":"snapshot_pipeline_disabled"}<br/>+ Retry-After: 60 (R7.4)
                    else miss / corrupt
                        Svc-->>Ctl: null
                        Ctl-->>Cli: 404 {"error":"snapshot_not_found"} (R7.3)
                    else throw transient
                        Svc-->>Ctl: exception bubbles
                        Ctl->>Ctl: PublicReadExceptionFilter
                        Ctl-->>Cli: 503 {"error":"snapshot_unavailable"}<br/>+ Retry-After: 5 (R7.5, R7.8)
                    else hit
                        Svc-->>Ctl: ClientCacheSnapshotEnvelope
                        Ctl->>Ctl: serialize Data → bytes<br/>compute ETag W/"sha256-hex"<br/>(R6.1, R6.8)
                        alt If-None-Match matches OR "*"
                            Ctl-->>Cli: 304 + ETag + Cache-Control + Vary<br/>(R6.4, R6.5)
                        else
                            Ctl-->>Cli: 200 + body + ETag<br/>+ Cache-Control: public, max-age=N, no-transform<br/>+ Vary: X-Tenant-Api-Key<br/>+ X-Snapshot-Last-Write-Utc<br/>+ X-Snapshot-Version<br/>+ X-Content-Type-Options: nosniff<br/>(R6.1-3, R6.6-7, R9.8)
                        end
                    end
                end
            end
        end
    end
```

### Layer responsibility

| Layer | Trách nhiệm | Files dự kiến (NEW) |
|---|---|---|
| Pipeline filter — HTTPS gate | Reject plain HTTP cho non-localhost trước khi chạm API key (R9.7) | `Services/PublicTenantClients/HttpsRequiredFilter.cs` |
| Pipeline filter — API key validate | Validate `X-Tenant-Api-Key` constant-time, short-circuit 401, emit Audit (R3) | `Services/PublicTenantClients/TenantApiKeyAuthorizationFilter.cs`, `Services/PublicTenantClients/ITenantApiKeyValidator.cs`, `Services/PublicTenantClients/TenantApiKeyValidator.cs` |
| Pipeline filter — Exception mapper | Map unhandled `Exception` ra 503 với `snapshot_unavailable` (R7.5, R7.8) | `Services/PublicTenantClients/PublicReadExceptionFilter.cs` |
| Middleware — Rate limit | Token bucket partition by `normalize(tenantKey)` (R4) | đăng ký trong `StartupHelpers.AddTenantClientCachePublicRead(...)` |
| Middleware — CORS policy | Allowlist origins, restricted methods/headers (R5) | đăng ký trong `StartupHelpers.AddTenantClientCachePublicRead(...)` |
| Controller | Validate path inputs (R7.1, R7.2), call `ReadSnapshotAsync`, ETag negotiation (R6), set headers (R6, R9.8) | `Controllers/PublicTenantClientsController.cs` |
| Options | Bind `TenantClientCachePublicRead` section, fail-fast validate (R1, R4.3, R4.4, R5.6, R6.2, R9.6) | `Configuration/TenantClientCachePublicReadOptions.cs`, `Configuration/TenantClientCachePublicReadOptionsValidator.cs` |
| Helper — IP hashing | SHA-256(remoteIp + per-host salt) (R9.6) | `Services/PublicTenantClients/IpHashHelper.cs` |
| Metrics | Reuse Meter `"TenantClientCache"`; thêm 7 counter + 1 histogram (R8.3, R8.5, R11.11) | extend `Services/TenantClientCache/TenantClientCacheMetrics.cs` (file đã có ở parent spec) |
| Wiring | Một extension method gắn toàn bộ vào `IServiceCollection` + `IEndpointRouteBuilder` (R12.10) | `Helpers/StartupHelpers.cs` (phương thức tiện ích MỚI, không sửa method hiện có) |
| SDK project | Tách project, NuGet packable (R10.1) | `src/Skoruba.Duende.IdentityServer.TenantClientCache.Client/*` |

### Cross-cutting concerns

- **Cancellation**: tất cả filter + controller pass `HttpContext.RequestAborted` xuống `ReadSnapshotAsync` (R2.8). Filter không tạo CTS riêng để tránh che cancellation từ host shutdown.
- **Logging**: dùng `ILogger<TenantApiKeyAuthorizationFilter>`, `ILogger<PublicTenantClientsController>`, `ILogger<PublicReadExceptionFilter>`. Tất cả structured log đi qua một helper static `Audit_Event_Public_Read.Emit(logger, fields)` để áp dụng schema R8 + redaction R8.7.
- **Metrics**: singleton `TenantClientCacheMetrics` (đã có ở parent) được mở rộng để chứa thêm counter / histogram cho public-read; KHÔNG tạo Meter thứ hai (R8.3).
- **Activity / CorrelationId**: lấy `Activity.Current?.TraceId` tại thời điểm log (R8.6).
- **Reservation**: route `/api/public/tenants/...` được opt-out khỏi default authentication / authorization policy của Admin_Api_Host bằng `[AllowAnonymous]` ở controller + endpoint metadata `RequireAuthorization(...)` KHÔNG được áp dụng (R12.10).

## Components and Interfaces

### `TenantClientCachePublicReadOptions`

Bind từ section `TenantClientCachePublicRead` trong `appsettings.json`. Đặt cùng folder convention với options khác trong Admin_Api_Host (`Configuration/`).

```csharp
namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;

public sealed class TenantClientCachePublicReadOptions
{
    public const string SectionName = "TenantClientCachePublicRead";

    /// <summary>
    /// Map: tenantKey (lowercased) → SHA-256 hex lowercase of API key.
    /// R1.2, R1.4, R1.5. Default: empty dictionary (R1.3).
    /// </summary>
    public IDictionary<string, string> ApiKeys { get; set; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    public RateLimitOptions RateLimit { get; set; } = new();
    public CorsOptions Cors { get; set; } = new();
    public ResponseCacheOptions ResponseCache { get; set; } = new();
    public AuditOptions Audit { get; set; } = new();

    public sealed class RateLimitOptions
    {
        // R4.2 + R4.3 + R4.4
        public int TokenLimit { get; set; } = 30;
        public int TokensPerPeriod { get; set; } = 30;
        public TimeSpan ReplenishmentPeriod { get; set; } = TimeSpan.FromMinutes(1);
        public int QueueLimit { get; set; } = 0;
        public bool AutoReplenishment { get; set; } = true;
    }

    public sealed class CorsOptions
    {
        // R5.1, R5.6
        public IList<string> AllowedOrigins { get; set; } = new List<string>();
        // R5.7
        public int PreflightMaxAgeSeconds { get; set; } = 600;
    }

    public sealed class ResponseCacheOptions
    {
        // R6.2
        public int MaxAgeSeconds { get; set; } = 60;
    }

    public sealed class AuditOptions
    {
        // R3.6
        public bool LogIpHash { get; set; } = true;
        // R9.6 — MUST be non-empty random string in Production; validator enforces.
        public string RemoteIpSalt { get; set; } = string.Empty;
    }
}
```

Wire-up:

```csharp
services.AddOptions<TenantClientCachePublicReadOptions>()
    .Bind(configuration.GetSection(TenantClientCachePublicReadOptions.SectionName))
    .ValidateOnStart();
services.AddSingleton<IValidateOptions<TenantClientCachePublicReadOptions>,
                      TenantClientCachePublicReadOptionsValidator>();
```

`AddOptions<T>().ValidateOnStart()` đảm bảo fail-fast khi host khởi động (R1.4, R1.5, R4.3, R4.4, R5.6).

### `TenantClientCachePublicReadOptionsValidator`

Validation tùy biến (vì DataAnnotations không đủ diễn đạt SHA-256 hex format + sliding range giữa các field).

```csharp
namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;

internal sealed class TenantClientCachePublicReadOptionsValidator
    : IValidateOptions<TenantClientCachePublicReadOptions>
{
    private static readonly Regex Sha256HexLower =
        new("^[0-9a-f]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IHostEnvironment _env;

    public TenantClientCachePublicReadOptionsValidator(IHostEnvironment env) => _env = env;

    public ValidateOptionsResult Validate(string? name, TenantClientCachePublicReadOptions o)
    {
        var errors = new List<string>();

        // R1.4 + R1.5: ApiKeys map shape
        foreach (var kvp in o.ApiKeys)
        {
            if (kvp.Key != kvp.Key.Trim() || kvp.Key.Any(char.IsUpper))
                errors.Add($"ApiKeys key '{kvp.Key}' must be trimmed lowercase.");
            if (!Sha256HexLower.IsMatch(kvp.Value ?? string.Empty))
                errors.Add($"ApiKeys[{kvp.Key}] is not a 64-char lowercased hex SHA-256 digest.");
                // NOTE: never include kvp.Value in the message (R1.4).
        }

        // R4.3
        if (o.RateLimit.TokenLimit < 1 || o.RateLimit.TokenLimit > 10_000)
            errors.Add($"RateLimit:TokenLimit out of [1,10000]: '{o.RateLimit.TokenLimit}'.");

        // R4.4
        if (o.RateLimit.ReplenishmentPeriod < TimeSpan.FromSeconds(1) ||
            o.RateLimit.ReplenishmentPeriod > TimeSpan.FromHours(1))
            errors.Add("RateLimit:ReplenishmentPeriod must be in [00:00:01, 01:00:00].");

        // R5.6
        foreach (var origin in o.Cors.AllowedOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var u))
            {
                errors.Add($"Cors:AllowedOrigins entry '{origin}' is not an absolute URL.");
                continue;
            }

            var isLocalhost = string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase);
            if (u.Scheme != Uri.UriSchemeHttps && !(u.Scheme == Uri.UriSchemeHttp && isLocalhost))
                errors.Add($"Cors:AllowedOrigins entry '{origin}' must use https (or http for localhost).");
        }

        // R5.7
        if (o.Cors.PreflightMaxAgeSeconds < 0 || o.Cors.PreflightMaxAgeSeconds > 86_400)
            errors.Add("Cors:PreflightMaxAgeSeconds must be in [0, 86400].");

        // R6.2
        if (o.ResponseCache.MaxAgeSeconds < 0 || o.ResponseCache.MaxAgeSeconds > 3600)
            errors.Add("ResponseCache:MaxAgeSeconds must be in [0, 3600].");

        // R9.6: salt MUST be non-empty in Production. Dev / Staging may default to empty
        // — validator emits a Warning log but does not fail-fast there.
        if (_env.IsProduction() && string.IsNullOrWhiteSpace(o.Audit.RemoteIpSalt))
            errors.Add("Audit:RemoteIpSalt is required in Production (R9.6).");

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
```

### `ITenantApiKeyValidator` + `TenantApiKeyValidator`

Pure compute + lookup, lifetime singleton để đảm bảo zero allocation per request beyond `byte[32]`.

```csharp
namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.PublicTenantClients;

public interface ITenantApiKeyValidator
{
    /// <summary>
    /// Returns true iff <paramref name="apiKeyPlaintext"/> matches the configured hash
    /// for <paramref name="normalizedTenantKey"/>. Constant-time comparison (R3.2).
    /// Caller MUST pre-normalize tenantKey via Trim().ToLowerInvariant() (R2.3).
    /// </summary>
    bool TryValidate(string normalizedTenantKey, ReadOnlySpan<char> apiKeyPlaintext);
}

internal sealed class TenantApiKeyValidator : ITenantApiKeyValidator
{
    private readonly IOptionsMonitor<TenantClientCachePublicReadOptions> _options;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public TenantApiKeyValidator(IOptionsMonitor<TenantClientCachePublicReadOptions> options)
        => _options = options;

    public bool TryValidate(string normalizedTenantKey, ReadOnlySpan<char> apiKeyPlaintext)
    {
        // R3.5: re-read CurrentValue every call to honour hot-reload (R1.6).
        var snapshot = _options.CurrentValue.ApiKeys;
        if (!snapshot.TryGetValue(normalizedTenantKey, out var expectedHexLower))
            return false;

        Span<byte> computed = stackalloc byte[32];
        var byteCount = Utf8NoBom.GetByteCount(apiKeyPlaintext);
        var rentedBytes = byteCount <= 256
            ? stackalloc byte[byteCount]
            : new byte[byteCount];
        Utf8NoBom.GetBytes(apiKeyPlaintext, rentedBytes);
        SHA256.HashData(rentedBytes, computed);

        Span<byte> expected = stackalloc byte[32];
        if (!TryParseHexLower(expectedHexLower, expected))
            return false;

        // R3.2: constant-time, NOT short-circuit byte comparison.
        return CryptographicOperations.FixedTimeEquals(computed, expected);
    }

    private static bool TryParseHexLower(string hex, Span<byte> dest) { /* deterministic 64→32 */ }
}
```

Lifetime `Singleton`. Validator KHÔNG cache derived bytes (R3.5). Hot-reload chỉ cần `IConfiguration` underlying provider có `reloadOnChange = true`, đã sẵn ở host (R1.6).

### `TenantApiKeyAuthorizationFilter`

```csharp
internal sealed class TenantApiKeyAuthorizationFilter : IAsyncAuthorizationFilter
{
    public const string HeaderName = "X-Tenant-Api-Key";
    private const string TenantKeyRouteKey = "tenantKey";

    private readonly ITenantApiKeyValidator _validator;
    private readonly ILogger<TenantApiKeyAuthorizationFilter> _logger;
    private readonly IOptionsMonitor<TenantClientCachePublicReadOptions> _options;
    private readonly TenantClientCacheMetrics _metrics;
    private readonly IpHashHelper _ipHash;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext ctx)
    {
        // R3.7: only header. Do NOT consult query / cookie / body.
        if (!ctx.HttpContext.Request.Headers.TryGetValue(HeaderName, out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            ShortCircuit(ctx, StatusCodes.Status401Unauthorized, "missing_api_key", "Unauthorized");
            return;
        }

        // R7.1 happens in the controller AFTER auth; here we only normalize for lookup.
        var tenantKey = ((string?)ctx.RouteData.Values[TenantKeyRouteKey] ?? string.Empty)
            .Trim().ToLowerInvariant();

        var ok = _validator.TryValidate(tenantKey, raw.ToString().AsSpan());
        if (!ok)
        {
            // R3.3: do NOT differentiate "tenant not registered" vs "wrong key".
            ShortCircuit(ctx, StatusCodes.Status401Unauthorized, "invalid_api_key", "Unauthorized");
            return;
        }
        // valid → fall through; rate limiter (R4) runs next.
    }

    private void ShortCircuit(AuthorizationFilterContext ctx, int status, string error, string outcome)
    {
        // R3.4: do NOT log the raw header, the SHA-256 hash, or the raw tenantKey.
        // R8.4: 'Unauthorized' counter has NO tenantKey tag.
        _metrics.PublicReadUnauthorized();
        _logger.LogWarning(
            "{EventType} outcome={Outcome} corr={CorrelationId} remoteIpHash={RemoteIpHash}",
            "TenantClientCachePublicRead.Unauthorized",
            outcome,
            Activity.Current?.TraceId.ToString(),
            _ipHash.Hash(ctx.HttpContext.Connection.RemoteIpAddress));

        ctx.Result = new ObjectResult(new { error })
        {
            StatusCode = status,
            ContentTypes = { "application/json; charset=utf-8" }
        };
    }
}
```

Đăng ký:

```csharp
services.AddScoped<TenantApiKeyAuthorizationFilter>();
services.AddSingleton<ITenantApiKeyValidator, TenantApiKeyValidator>();
```

Filter được áp dụng qua attribute `[ServiceFilter(typeof(TenantApiKeyAuthorizationFilter))]` trên controller action, KHÔNG áp dụng global (R12.1, R12.10). Vì `IAsyncAuthorizationFilter` chạy trước rate limiter middleware mặc định khi controller được route, ASP.NET Core 8 cần wire qua endpoint metadata để đảm bảo thứ tự (xem section "Pipeline ordering" bên dưới) — R3.8 yêu cầu auth trước rate limit.

### Pipeline ordering (R3.8 + R4.7 + R4.9)

Để guarantee thứ tự HTTPS → CORS → API key → Rate limit → Path validation → Service call, dùng kết hợp endpoint metadata + middleware ordering:

```mermaid
flowchart TD
    A[Request] --> B[HttpsRequiredFilter<br/>(IAsyncAuthorizationFilter)]
    B -->|reject 400| Z[response]
    B -->|allow| C[CORS middleware]
    C --> D[Routing middleware]
    D --> E[TenantApiKeyAuthorizationFilter<br/>(IAsyncAuthorizationFilter, runs in MVC layer<br/>BEFORE RateLimit endpoint)]
    E -->|reject 401| Z
    E -->|allow| F[Rate limiter middleware<br/>(EnableRateLimiting attribute)]
    F -->|reject 429| Z
    F -->|allow| G[PublicTenantClientsController action<br/>+ path validation R7.1/R7.2]
    G -->|reject 400| Z
    G --> H[ReadSnapshotAsync]
    H --> I[ETag negotiation + headers]
    I --> Z
```

Vì ASP.NET Core 8 đặt `UseRateLimiter()` trước `UseEndpoints()`, mặc định rate limit nằm TRƯỚC authorization filter. Để đảo ngược (R3.8), feature dùng cấu trúc:

1. Đặt `app.UseRateLimiter()` SAU `app.UseAuthorization()` trong startup (đã là pattern trong host hiện tại — confirm khi implement). Authorization filter `IAsyncAuthorizationFilter` chạy bên trong endpoint resolution, do đó vẫn earlier than rate limiter middleware nếu rate limiter wrap entire endpoint pipeline.
2. Implementation thực tế: dùng `RateLimiterOptions.OnRejected` để KHÔNG consume token cho authentication-failed request bằng cách đặt rate limit ở **action-level attribute** `[EnableRateLimiting("TenantClientCachePublicRead")]` — `IAsyncAuthorizationFilter.OnAuthorizationAsync` chạy trước endpoint filters trong MVC pipeline, đảm bảo 401 không tốn token.
3. R4.9 (malformed input → 400 không tốn token): vì path validation chạy bên trong controller action, nó nằm SAU rate limiter consume. Để đảm bảo R4.9, tách path validation thành một `IAsyncResourceFilter` trên controller: filter chạy trước action body và trước EnableRateLimiting attribute KHÔNG đảm bảo. **Decision**: thay vào đó, route constraints `{tenantKey:regex(^[a-z0-9_-]+$)}` được áp dụng để URL không match regex bị 404 (mặc định framework, R7.9 cho phép); còn validation `length ≤ 128` và `clientId length ≤ 200` chạy trong controller — chấp nhận chúng tốn 1 token (rate limit dùng default 30/phút, rủi ro DoS không đáng kể vì bad path đã bị `tenantKey` regex chặn ngay route layer). Document tradeoff trong "Risks AND Mitigations".

### Rate limiter wiring

```csharp
services.AddRateLimiter(options =>
{
    options.AddPolicy("TenantClientCachePublicRead", httpContext =>
    {
        var tenantKey = ((string?)httpContext.Request.RouteValues["tenantKey"] ?? string.Empty)
            .Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(tenantKey))
            return RateLimitPartition.GetNoLimiter("__noop__");

        var cfg = httpContext.RequestServices
            .GetRequiredService<IOptionsMonitor<TenantClientCachePublicReadOptions>>()
            .CurrentValue.RateLimit;

        return RateLimitPartition.GetTokenBucketLimiter(tenantKey, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = cfg.TokenLimit,
            TokensPerPeriod = cfg.TokensPerPeriod,
            ReplenishmentPeriod = cfg.ReplenishmentPeriod,
            QueueLimit = cfg.QueueLimit,
            AutoReplenishment = cfg.AutoReplenishment
        });
    });

    options.OnRejected = static async (ctx, ct) =>
    {
        var retryAfter = 1; // R4.5 fallback
        if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var ts))
            retryAfter = (int)Math.Ceiling(ts.TotalSeconds);
        ctx.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        ctx.HttpContext.Response.ContentType = "application/json; charset=utf-8";
        await ctx.HttpContext.Response.WriteAsync("""{"error":"rate_limit_exceeded"}""", ct);
        // R8.1 + R8.4: emit RateLimited counter tagged with tenantKey,
        // emit Audit_Event_Public_Read at Warning.
    };
});
```

Partition key = `normalize(tenantKey)`, KHÔNG bao gồm IP / `clientId` / API key (R4.6).

### CORS policy

```csharp
services.AddCors(o =>
{
    o.AddPolicy("TenantClientCachePublicRead", policy =>
    {
        var cfg = sp.GetRequiredService<IOptions<TenantClientCachePublicReadOptions>>().Value.Cors;
        if (cfg.AllowedOrigins.Count == 0)
            policy.WithOrigins(); // R5.4: zero origins
        else
            policy.WithOrigins(cfg.AllowedOrigins.ToArray());

        policy.WithMethods("GET", "HEAD", "OPTIONS")           // R5.2
              .WithHeaders("X-Tenant-Api-Key", "If-None-Match", "Accept") // R5.2
              .WithExposedHeaders("ETag", "Cache-Control")     // R5.8
              .DisallowCredentials()                            // R5.3
              .SetPreflightMaxAge(TimeSpan.FromSeconds(cfg.PreflightMaxAgeSeconds)); // R5.7
    });
});
```

Áp dụng qua `[EnableCors("TenantClientCachePublicRead")]` ở controller, KHÔNG đặt làm default policy (R5.5).

### `PublicTenantClientsController`

Controller mới, route prefix `/api/public/tenants`. Một và chỉ một action public.

```csharp
namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Controllers;

[ApiController]
[AllowAnonymous]                                             // R12.10
[Route("api/public/tenants")]
[EnableCors("TenantClientCachePublicRead")]
[EnableRateLimiting("TenantClientCachePublicRead")]
[ServiceFilter(typeof(HttpsRequiredFilter))]                 // R9.7 — runs first
[ServiceFilter(typeof(TenantApiKeyAuthorizationFilter))]     // R3 — runs after HTTPS
[ServiceFilter(typeof(PublicReadExceptionFilter))]
[Tags("PublicTenantClients")]                                // R12.9
public sealed class PublicTenantClientsController : ControllerBase
{
    private const int TenantKeyMaxLength = 128;
    private const int ClientIdMaxLength  = 200;
    private static readonly Regex TenantKeyShape =
        new("^[a-z0-9_-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly ITenantClientCacheService _snapshots;
    private readonly IOptionsMonitor<TenantClientCachePublicReadOptions> _options;
    private readonly TenantClientCacheMetrics _metrics;
    private readonly ILogger<PublicTenantClientsController> _logger;
    private readonly IpHashHelper _ipHash;

    [HttpGet("{tenantKey}/clients/{clientId}")]
    [HttpHead("{tenantKey}/clients/{clientId}")]              // R2.9
    [Produces("application/json")]
    public async Task<IActionResult> GetAsync(
        string tenantKey,
        string clientId,
        CancellationToken cancellationToken)
    {
        var sw = ValueStopwatch.StartNew();

        // R7.1
        var normalizedTenantKey = (tenantKey ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalizedTenantKey)
            || normalizedTenantKey.Length > TenantKeyMaxLength
            || !TenantKeyShape.IsMatch(normalizedTenantKey))
            return Bad("invalid_tenant_key", normalizedTenantKey);

        // R7.2
        var trimmedClientId = (clientId ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmedClientId)
            || trimmedClientId.Length > ClientIdMaxLength)
            return Bad("invalid_client_id", normalizedTenantKey);

        // R2.1, R2.8: only collaborator beyond HttpContext.
        var envelope = await _snapshots.ReadSnapshotAsync(
            normalizedTenantKey, trimmedClientId, HttpContext.RequestAborted);

        if (envelope is null)
        {
            // R7.3 — Miss / corrupt / stale all surfaced as 404 (parent spec R10.4 + R2.8).
            return NotFound(normalizedTenantKey, sw.Elapsed);
        }

        // Snapshot_Pipeline_Disabled signal: when ITenantClientCacheService returns
        // a sentinel envelope with Version == -1 (parent spec convention for "disabled"),
        // surface as 503 (R7.4). If the parent service throws SnapshotPipelineDisabledException
        // instead, the PublicReadExceptionFilter catches and routes to 503/snapshot_pipeline_disabled.
        if (envelope.Version <= 0)
            return PipelineDisabled(normalizedTenantKey, sw.Elapsed);

        // R6.1, R6.8 — deterministic serialize then hash.
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(envelope.Data, Json);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bodyBytes, hash);
        var etag = $"W/\"{Convert.ToHexString(hash).ToLowerInvariant()}\"";

        // R6.4, R6.5 — If-None-Match negotiation (RFC 7232).
        var requestEtag = Request.Headers.IfNoneMatch.ToString();
        if (Matches(requestEtag, etag))
        {
            WriteCommonHeaders(etag, envelope);
            return StatusCode(StatusCodes.Status304NotModified);
        }

        WriteCommonHeaders(etag, envelope);
        Response.ContentType = "application/json; charset=utf-8";  // R2.6

        if (HttpMethods.IsHead(Request.Method))
        {
            // R2.9 — HEAD: same headers, empty body.
            Response.ContentLength = bodyBytes.Length;
            EmitHit(normalizedTenantKey, sw.Elapsed);
            return new EmptyResult();
        }

        await Response.Body.WriteAsync(bodyBytes, HttpContext.RequestAborted);
        EmitHit(normalizedTenantKey, sw.Elapsed);
        return new EmptyResult();
    }

    private void WriteCommonHeaders(string etag, ClientCacheSnapshotEnvelope env)
    {
        var maxAge = _options.CurrentValue.ResponseCache.MaxAgeSeconds;
        Response.Headers.ETag = etag;                                            // R6.1
        Response.Headers.CacheControl = $"public, max-age={maxAge}, no-transform"; // R6.2 + R9.8
        Response.Headers.Vary = "X-Tenant-Api-Key";                              // R6.3
        Response.Headers["X-Snapshot-Last-Write-Utc"]
            = env.LastWriteUtc.ToString("o", CultureInfo.InvariantCulture);      // R6.6
        Response.Headers["X-Snapshot-Version"]
            = env.Version.ToString(CultureInfo.InvariantCulture);                // R6.7
        Response.Headers["X-Content-Type-Options"] = "nosniff";                  // R9.8
    }

    private static bool Matches(string requestEtag, string serverEtag) { /* RFC 7232 W/, '*' */ }

    private IActionResult Bad(string error, string normalizedTenantKey) { /* 400, audit BadRequest, R8.4 no tenantKey tag */ }
    private IActionResult NotFound(string normalizedTenantKey, TimeSpan elapsed) { /* 404 R7.3 */ }
    private IActionResult PipelineDisabled(string nt, TimeSpan e) { /* 503 + Retry-After: 60, R7.4 */ }
    private void EmitHit(string normalizedTenantKey, TimeSpan elapsed) { /* counter + histogram + Audit Information */ }
}
```

Lưu ý:

- Action không tự catch `Exception`; `PublicReadExceptionFilter` (xem dưới) lo. Nhờ vậy controller giữ logic tuyến tính, dễ unit test (R7.5, R7.8).
- Action không inject `IClientService` / `IClientRepository` / `DbContext` (R2.7, R12.10). Compile-time + static-check (R12.10) sẽ enforce.
- Phân biệt "pipeline disabled" vs "transient" được rõ qua sentinel `envelope.Version <= 0` HOẶC qua exception type — quyết định cụ thể tùy parent spec phơi bày interface; xem "Open Questions" section.

### `TenantClientCacheMetrics` extension

File `Services/TenantClientCache/TenantClientCacheMetrics.cs` đã tồn tại (parent spec). Feature này thêm một block partial / hoặc thêm field bổ sung trong cùng class. KHÔNG tạo file mới (R8.3 cấm Meter thứ hai).

```csharp
// Additional members appended to existing TenantClientCacheMetrics
private readonly Counter<long> _publicReadHit;
private readonly Counter<long> _publicReadNotModified;
private readonly Counter<long> _publicReadMiss;
private readonly Counter<long> _publicReadUnauthorized;
private readonly Counter<long> _publicReadRateLimited;
private readonly Counter<long> _publicReadBadRequest;
private readonly Counter<long> _publicReadServiceUnavailable;
private readonly Histogram<double> _publicReadDuration;

// In ctor:
_publicReadHit             = meter.CreateCounter<long>("tenant_client_cache.public_read.hit");
_publicReadNotModified     = meter.CreateCounter<long>("tenant_client_cache.public_read.not_modified");
_publicReadMiss            = meter.CreateCounter<long>("tenant_client_cache.public_read.miss");
_publicReadUnauthorized    = meter.CreateCounter<long>("tenant_client_cache.public_read.unauthorized");
_publicReadRateLimited     = meter.CreateCounter<long>("tenant_client_cache.public_read.rate_limited");
_publicReadBadRequest      = meter.CreateCounter<long>("tenant_client_cache.public_read.bad_request");
_publicReadServiceUnavailable = meter.CreateCounter<long>("tenant_client_cache.public_read.service_unavailable");
_publicReadDuration        = meter.CreateHistogram<double>("tenant_client_cache.public_read.duration_ms");

// Tagging policy enforcement (R8.4):
public void PublicReadHit(string tenantKey, double ms) {
    _publicReadHit.Add(1, new KeyValuePair<string, object?>("tenantKey", tenantKey));
    _publicReadDuration.Record(ms,
        new KeyValuePair<string, object?>("outcome", "Hit"),
        new KeyValuePair<string, object?>("tenantKey", tenantKey));
}
public void PublicReadUnauthorized() {
    // R8.4: NO tenantKey tag for Unauthorized (anti-enumeration).
    _publicReadUnauthorized.Add(1);
}
public void PublicReadBadRequest() {
    // R8.4: NO tenantKey tag for BadRequest.
    _publicReadBadRequest.Add(1);
}
// ... similar helpers for NotModified / Miss / RateLimited / ServiceUnavailable, all WITH tenantKey tag.
```

### `IpHashHelper`

```csharp
namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.PublicTenantClients;

public sealed class IpHashHelper
{
    private readonly IOptionsMonitor<TenantClientCachePublicReadOptions> _options;
    private static readonly UTF8Encoding Utf8 = new(false);

    public IpHashHelper(IOptionsMonitor<TenantClientCachePublicReadOptions> options)
        => _options = options;

    public string? Hash(IPAddress? remoteIp)
    {
        var audit = _options.CurrentValue.Audit;
        if (!audit.LogIpHash || remoteIp is null) return null;          // R3.6

        var salt = audit.RemoteIpSalt ?? string.Empty;                  // R9.6
        Span<byte> hash = stackalloc byte[32];
        var input = Utf8.GetBytes(remoteIp + ":" + salt);
        SHA256.HashData(input, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
```

Lifetime singleton; helper KHÔNG cache hash output để tôn trọng hot-reload salt (use case hiếm; mỗi request một SHA-256 ≈ 1 µs).

### `HttpsRequiredFilter`

```csharp
internal sealed class HttpsRequiredFilter : IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext ctx)
    {
        var req = ctx.HttpContext.Request;
        if (req.IsHttps) return Task.CompletedTask;

        var host = req.Host.Host;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.IsLoopback(ctx.HttpContext.Connection.RemoteIpAddress ?? IPAddress.None))
            return Task.CompletedTask;

        ctx.Result = new ObjectResult(new { error = "https_required" })
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { "application/json; charset=utf-8" }
        };
        return Task.CompletedTask;
    }
}
```

Đăng ký Singleton (no scoped state). Chạy trước `TenantApiKeyAuthorizationFilter` nhờ thứ tự `[ServiceFilter]` declaration trên controller (R9.7).

### `PublicReadExceptionFilter`

```csharp
internal sealed class PublicReadExceptionFilter : IAsyncExceptionFilter
{
    private readonly ILogger<PublicReadExceptionFilter> _logger;
    private readonly TenantClientCacheMetrics _metrics;

    public Task OnExceptionAsync(ExceptionContext ctx)
    {
        if (ctx.Exception is OperationCanceledException
            && ctx.HttpContext.RequestAborted.IsCancellationRequested)
        {
            // Caller disconnected — treat as silent.
            ctx.ExceptionHandled = true;
            return Task.CompletedTask;
        }

        var tenantKey = ((string?)ctx.RouteData.Values["tenantKey"] ?? string.Empty)
            .Trim().ToLowerInvariant();

        _metrics.PublicReadServiceUnavailable(tenantKey);
        _logger.LogError(ctx.Exception,
            "{EventType} tenant={TenantKey} outcome={Outcome} corr={CorrelationId}",
            "TenantClientCachePublicRead.ServiceUnavailable",
            tenantKey,
            "ServiceUnavailable",
            Activity.Current?.TraceId.ToString());

        ctx.HttpContext.Response.Headers.RetryAfter = "5"; // R7.5
        ctx.Result = new ObjectResult(new { error = "snapshot_unavailable" })
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable,
            ContentTypes = { "application/json; charset=utf-8" }
        };
        ctx.ExceptionHandled = true; // R7.8: never let 500 escape
        return Task.CompletedTask;
    }
}
```

## SDK Project Design

### Project layout

```
src/Skoruba.Duende.IdentityServer.TenantClientCache.Client/
├── Skoruba.Duende.IdentityServer.TenantClientCache.Client.csproj
├── TenantClientCacheClientServiceCollectionExtensions.cs
├── TenantClientCacheClientOptions.cs
├── ITenantClientCacheClient.cs
├── TenantClientCacheClient.cs
├── Models/
│   ├── PublicClientSnapshot.cs
│   ├── TenantClientSnapshotResult.cs
│   └── SdkCacheOutcome.cs
└── Internal/
    ├── TenantClientCacheClientMetrics.cs
    ├── TenantClientCacheClientRetryPolicy.cs
    └── TenantClientCacheClientCacheEntry.cs
```

### csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>

    <IsPackable>true</IsPackable>
    <PackageId>Skoruba.Duende.IdentityServer.TenantClientCache.Client</PackageId>
    <Description>SDK for the public-read endpoint of the tenant client cache.</Description>
    <Authors>Skoruba</Authors>
    <RepositoryUrl>https://github.com/skoruba/Duende.IdentityServer.Admin</RepositoryUrl>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <PackageTags>identityserver;duende;tenant;cache;sdk</PackageTags>
  </PropertyGroup>

  <!-- R10.1: only packages already in the solution's transitive set. -->
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Http" />
    <PackageReference Include="Microsoft.Extensions.Caching.Memory" />
    <PackageReference Include="Microsoft.Extensions.Options" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <!-- System.Text.Json comes in via the framework reference on net8.0 -->
  </ItemGroup>
</Project>
```

Lưu ý: phiên bản package KHÔNG đặt cứng trong csproj; thừa hưởng từ `Directory.Packages.props` (Central Package Management) đã có ở solution. KHÔNG thêm version mới ngoài tập đã có (R12.6).

### `TenantClientCacheClientServiceCollectionExtensions`

```csharp
namespace Skoruba.Duende.IdentityServer.TenantClientCache.Client;

public static class TenantClientCacheClientServiceCollectionExtensions
{
    public const string HttpClientName = "TenantClientCachePublicRead"; // R10.6

    public static IServiceCollection AddTenantClientCacheClient(
        this IServiceCollection services,
        Action<TenantClientCacheClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<TenantClientCacheClientOptions>()
            .Configure(configure)
            .Validate(o =>                                   // R10.7, R10.8
            {
                if (o.BaseAddress is null) return false;
                if (!o.BaseAddress.IsAbsoluteUri) return false;
                var isLocalhost = string.Equals(o.BaseAddress.Host, "localhost",
                    StringComparison.OrdinalIgnoreCase);
                if (o.BaseAddress.Scheme != Uri.UriSchemeHttps && !isLocalhost) return false;
                if (string.IsNullOrWhiteSpace(o.ApiKey)) return false;
                if (o.HttpTimeout < TimeSpan.FromSeconds(1)
                    || o.HttpTimeout > TimeSpan.FromSeconds(60)) return false;
                if (o.MaxRetryAttempts < 0 || o.MaxRetryAttempts > 5) return false;
                if (o.RetryBaseDelay < TimeSpan.FromMilliseconds(10)
                    || o.RetryBaseDelay > TimeSpan.FromSeconds(5)) return false;
                if (o.MaxClientCacheTtl < TimeSpan.Zero
                    || o.MaxClientCacheTtl > TimeSpan.FromHours(1)) return false;
                return true;
            }, "TenantClientCacheClientOptions failed validation (R10.7, R10.8).")
            .ValidateOnStart();

        services.AddHttpClient(HttpClientName, (sp, http) =>
        {
            var opts = sp.GetRequiredService<IOptions<TenantClientCacheClientOptions>>().Value;
            http.BaseAddress = opts.BaseAddress;
            http.Timeout = opts.HttpTimeout;                                       // R11.12
            http.DefaultRequestHeaders.UserAgent.ParseAdd(BuildUserAgent());        // R10.9
        });

        services.AddSingleton<TenantClientCacheClientMetrics>();                   // R11.11
        services.AddMemoryCache();                                                 // R10.7
        services.AddSingleton<ITenantClientCacheClient, TenantClientCacheClient>();// R10.2

        return services;
    }

    private static string BuildUserAgent()
    {
        var asm = typeof(TenantClientCacheClientServiceCollectionExtensions).Assembly;
        var ver = asm.GetName().Version?.ToString() ?? "0.0.0";
        return $"Skoruba.Duende.IdentityServer.TenantClientCache.Client/{ver}";
    }
}
```

### `TenantClientCacheClientOptions`

```csharp
public sealed class TenantClientCacheClientOptions
{
    public Uri? BaseAddress { get; set; }                                  // R10.7
    public string ApiKey { get; set; } = string.Empty;                     // R10.7
    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(5);   // R10.7
    public int MaxRetryAttempts { get; set; } = 2;                         // R11.1
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(200); // R11.3
    public TimeSpan MaxClientCacheTtl { get; set; } = TimeSpan.FromMinutes(5);     // R11.6
    public bool EnableInMemoryCaching { get; set; } = true;                // R11.6
}
```

### `ITenantClientCacheClient`

```csharp
public interface ITenantClientCacheClient
{
    /// <summary>
    /// Get the snapshot for (tenantKey, clientId). The SDK MAY return an in-memory cache hit (R11.6, R11.7).
    /// </summary>
    Task<TenantClientSnapshotResult> GetClientAsync(
        string tenantKey,
        string clientId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Force-bypass the in-memory cache and revalidate against the server with the supplied
    /// If-None-Match header (R11.8).
    /// </summary>
    Task<TenantClientSnapshotResult> GetClientAsync(
        string tenantKey,
        string clientId,
        string? ifNoneMatch,
        CancellationToken cancellationToken = default);
}
```

### `TenantClientCacheClient` implementation skeleton

```csharp
internal sealed class TenantClientCacheClient : ITenantClientCacheClient
{
    private const string ApiKeyHeader = "X-Tenant-Api-Key";
    private const string TenantKeyRoute = "tenantKey";
    private const string ClientIdRoute = "clientId";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _memoryCache;
    private readonly IOptionsMonitor<TenantClientCacheClientOptions> _options;
    private readonly ILogger<TenantClientCacheClient> _logger;
    private readonly TenantClientCacheClientMetrics _metrics;
    private readonly TenantClientCacheClientRetryPolicy _retry;

    public Task<TenantClientSnapshotResult> GetClientAsync(string tenantKey, string clientId, CancellationToken ct = default)
        => GetClientAsync(tenantKey, clientId, ifNoneMatch: null, ct);

    public async Task<TenantClientSnapshotResult> GetClientAsync(
        string tenantKey, string clientId, string? ifNoneMatch, CancellationToken ct = default)
    {
        var sw = ValueStopwatch.StartNew();
        var nt = (tenantKey ?? throw new ArgumentNullException(nameof(tenantKey)))
            .Trim().ToLowerInvariant();
        var nc = (clientId ?? throw new ArgumentNullException(nameof(clientId))).Trim();

        var opts = _options.CurrentValue;
        var cacheKey = (nt, nc);

        // R11.7 + R11.8 — local cache lookup (skipped if caller passed ifNoneMatch).
        if (opts.EnableInMemoryCaching && ifNoneMatch is null
            && _memoryCache.TryGetValue<TenantClientCacheClientCacheEntry>(cacheKey, out var hit))
        {
            _metrics.HitLocal();
            _logger.LogInformation(
                "{EventType} tenantKey={TenantKey} clientId={ClientId} outcome={Outcome} source=local",
                "TenantClientCacheClient.HitLocal", nt, nc, "Hit");
            return new TenantClientSnapshotResult(
                hit.Snapshot, hit.Etag, hit.LastWriteUtc, hit.Version,
                SdkCacheOutcome.Hit, RetryAfter: null);
        }

        var http = _httpClientFactory.CreateClient(
            TenantClientCacheClientServiceCollectionExtensions.HttpClientName);

        // R11.9 — auto-revalidate by re-using cached ETag when the local entry has expired.
        var revalidationEtag = ifNoneMatch
            ?? (_memoryCache.TryGetValue<TenantClientCacheClientCacheEntry>(cacheKey, out var stale)
                ? stale.Etag : null);

        var attempt = 0;
        Exception? lastException = null;
        HttpResponseMessage? response = null;
        while (attempt <= opts.MaxRetryAttempts)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"api/public/tenants/{Uri.EscapeDataString(nt)}/clients/{Uri.EscapeDataString(nc)}");
            req.Headers.Add(ApiKeyHeader, opts.ApiKey);                              // R10.7
            if (!string.IsNullOrEmpty(revalidationEtag))
                req.Headers.TryAddWithoutValidation("If-None-Match", revalidationEtag);

            try
            {
                response = await http.SendAsync(req,
                    HttpCompletionOption.ResponseHeadersRead, ct);

                if (_retry.ShouldRetry(response.StatusCode, attempt, opts.MaxRetryAttempts))
                {
                    _metrics.RetryAttempted();
                    response.Dispose();
                    response = null;
                    await Task.Delay(_retry.NextDelay(attempt, opts.RetryBaseDelay), ct);
                    attempt++;
                    continue;
                }
                break;                                                                // success or non-retriable
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;                                                                // R11.5
            }
            catch (Exception ex) when (TenantClientCacheClientRetryPolicy.IsTransientNetworkException(ex))
            {
                lastException = ex;
                if (attempt >= opts.MaxRetryAttempts)
                    break;
                _metrics.RetryAttempted();
                await Task.Delay(_retry.NextDelay(attempt, opts.RetryBaseDelay), ct);
                attempt++;
            }
        }

        // Translate response or terminal exception to TenantClientSnapshotResult.
        return await TranslateAsync(response, lastException, nt, nc, cacheKey, opts, sw.Elapsed, ct);
    }

    private async Task<TenantClientSnapshotResult> TranslateAsync(
        HttpResponseMessage? resp, Exception? lastException,
        string nt, string nc, (string,string) key,
        TenantClientCacheClientOptions opts, TimeSpan elapsed,
        CancellationToken ct)
    {
        if (resp is null)
        {
            _metrics.TransientFailure();
            return new TenantClientSnapshotResult(null, null, null, null,
                SdkCacheOutcome.TransientFailure, RetryAfter: null);
        }

        switch ((int)resp.StatusCode)
        {
            case 200:
                {
                    var snapshot = await resp.Content.ReadFromJsonAsync<PublicClientSnapshot>(ct)
                        ?? throw new InvalidOperationException("body deserialization returned null");
                    var etag = resp.Headers.ETag?.Tag;
                    var lastWrite = TryParseDate(resp.Headers, "X-Snapshot-Last-Write-Utc");
                    var version = TryParseInt(resp.Headers, "X-Snapshot-Version");
                    var maxAge = resp.Headers.CacheControl?.MaxAge ?? TimeSpan.Zero;
                    var ttl = TimeSpan.FromTicks(Math.Min(maxAge.Ticks, opts.MaxClientCacheTtl.Ticks));
                    if (opts.EnableInMemoryCaching && ttl > TimeSpan.Zero)
                        _memoryCache.Set(key,
                            new TenantClientCacheClientCacheEntry(snapshot, etag, lastWrite, version),
                            ttl);                                                       // R11.6
                    _metrics.HitRemote();
                    return new(snapshot, etag, lastWrite, version, SdkCacheOutcome.Miss, null);
                }
            case 304:
                {
                    // R11.9 — surface previously cached snapshot.
                    _memoryCache.TryGetValue<TenantClientCacheClientCacheEntry>(key, out var cached);
                    var maxAge = resp.Headers.CacheControl?.MaxAge ?? TimeSpan.Zero;
                    if (cached is not null && opts.EnableInMemoryCaching && maxAge > TimeSpan.Zero)
                    {
                        var ttl = TimeSpan.FromTicks(Math.Min(maxAge.Ticks, opts.MaxClientCacheTtl.Ticks));
                        _memoryCache.Set(key, cached, ttl);
                    }
                    _metrics.NotModified();
                    return new(cached?.Snapshot, cached?.Etag, cached?.LastWriteUtc,
                        cached?.Version, SdkCacheOutcome.NotModified, null);
                }
            case 401: _metrics.Unauthorized();         return Empty(SdkCacheOutcome.Unauthorized,         resp);
            case 404: _metrics.Miss();                 return Empty(SdkCacheOutcome.NotFound,             resp);
            case 429: _metrics.RateLimited();          return Empty(SdkCacheOutcome.RateLimited,          resp);
            case 503: _metrics.ServiceUnavailable();   return Empty(SdkCacheOutcome.ServiceUnavailable,   resp);
            default:
                // Unknown 4xx (e.g. 400 for invalid path, R7.1/R7.2): fold into TransientFailure
                // so caller has a single bucket for "treat as fail-soft, retry later".
                _metrics.TransientFailure();           return Empty(SdkCacheOutcome.TransientFailure,     resp);
        }
    }

    private static TenantClientSnapshotResult Empty(SdkCacheOutcome outcome, HttpResponseMessage? resp)
    {
        TimeSpan? retryAfter = null;
        if (resp?.Headers.RetryAfter is { Delta: { } d }) retryAfter = d;
        else if (resp?.Headers.RetryAfter is { Date: { } dt })
            retryAfter = dt - DateTimeOffset.UtcNow;
        return new(null, null, null, null, outcome, retryAfter);                         // R10.4 + R11.4
    }
}
```

### `Models/PublicClientSnapshot.cs` — sealed record với toàn bộ 38 Public_Safe_Fields

```csharp
namespace Skoruba.Duende.IdentityServer.TenantClientCache.Client.Models;

public sealed record PublicClientSnapshot
{
    [JsonPropertyName("clientId")]                 public string ClientId { get; init; } = string.Empty;
    [JsonPropertyName("clientName")]               public string? ClientName { get; init; }
    [JsonPropertyName("clientUri")]                public string? ClientUri { get; init; }
    [JsonPropertyName("logoUri")]                  public string? LogoUri { get; init; }
    [JsonPropertyName("description")]              public string? Description { get; init; }
    [JsonPropertyName("enabled")]                  public bool Enabled { get; init; }
    [JsonPropertyName("protocolType")]             public string ProtocolType { get; init; } = "oidc";

    [JsonPropertyName("redirectUris")]             public IReadOnlyList<string> RedirectUris { get; init; } = Array.Empty<string>();
    [JsonPropertyName("postLogoutRedirectUris")]   public IReadOnlyList<string> PostLogoutRedirectUris { get; init; } = Array.Empty<string>();
    [JsonPropertyName("allowedCorsOrigins")]       public IReadOnlyList<string> AllowedCorsOrigins { get; init; } = Array.Empty<string>();
    [JsonPropertyName("allowedGrantTypes")]        public IReadOnlyList<string> AllowedGrantTypes { get; init; } = Array.Empty<string>();
    [JsonPropertyName("allowedScopes")]            public IReadOnlyList<string> AllowedScopes { get; init; } = Array.Empty<string>();
    [JsonPropertyName("allowedIdentityTokenSigningAlgorithms")]
                                                   public IReadOnlyList<string> AllowedIdentityTokenSigningAlgorithms { get; init; } = Array.Empty<string>();

    [JsonPropertyName("requirePkce")]              public bool RequirePkce { get; init; }
    [JsonPropertyName("allowPlainTextPkce")]       public bool AllowPlainTextPkce { get; init; }
    [JsonPropertyName("requireClientSecret")]      public bool RequireClientSecret { get; init; }
    [JsonPropertyName("requireConsent")]           public bool RequireConsent { get; init; }
    [JsonPropertyName("allowOfflineAccess")]       public bool AllowOfflineAccess { get; init; }
    [JsonPropertyName("allowAccessTokensViaBrowser")] public bool AllowAccessTokensViaBrowser { get; init; }
    [JsonPropertyName("alwaysIncludeUserClaimsInIdToken")] public bool AlwaysIncludeUserClaimsInIdToken { get; init; }

    [JsonPropertyName("frontChannelLogoutUri")]    public string? FrontChannelLogoutUri { get; init; }
    [JsonPropertyName("frontChannelLogoutSessionRequired")] public bool FrontChannelLogoutSessionRequired { get; init; }
    [JsonPropertyName("backChannelLogoutUri")]     public string? BackChannelLogoutUri { get; init; }
    [JsonPropertyName("backChannelLogoutSessionRequired")] public bool BackChannelLogoutSessionRequired { get; init; }

    [JsonPropertyName("accessTokenLifetime")]      public int AccessTokenLifetime { get; init; }
    [JsonPropertyName("identityTokenLifetime")]    public int IdentityTokenLifetime { get; init; }
    [JsonPropertyName("authorizationCodeLifetime")] public int AuthorizationCodeLifetime { get; init; }
    [JsonPropertyName("absoluteRefreshTokenLifetime")] public int AbsoluteRefreshTokenLifetime { get; init; }
    [JsonPropertyName("slidingRefreshTokenLifetime")]  public int SlidingRefreshTokenLifetime { get; init; }
    [JsonPropertyName("refreshTokenExpiration")]   public int RefreshTokenExpiration { get; init; }
    [JsonPropertyName("refreshTokenUsage")]        public int RefreshTokenUsage { get; init; }
    [JsonPropertyName("updateAccessTokenClaimsOnRefresh")] public bool UpdateAccessTokenClaimsOnRefresh { get; init; }

    [JsonPropertyName("enableLocalLogin")]         public bool EnableLocalLogin { get; init; }
    [JsonPropertyName("requirePushedAuthorization")] public bool RequirePushedAuthorization { get; init; }
    [JsonPropertyName("requireRequestObject")]     public bool RequireRequestObject { get; init; }
    [JsonPropertyName("initiateLoginUri")]         public string? InitiateLoginUri { get; init; }
    [JsonPropertyName("useTenantRedirectPairs")]   public bool UseTenantRedirectPairs { get; init; }

    [JsonPropertyName("lastWriteUtc")]             public DateTime LastWriteUtc { get; init; }
}
```

### `Models/TenantClientSnapshotResult.cs`

```csharp
public sealed record TenantClientSnapshotResult(
    PublicClientSnapshot? Snapshot,                  // null for non-success or NotModified-without-prior-cache
    string? Etag,
    DateTimeOffset? LastWriteUtc,
    int? Version,
    SdkCacheOutcome Outcome,
    TimeSpan? RetryAfter);                            // R11.4
```

### `Models/SdkCacheOutcome.cs`

```csharp
public enum SdkCacheOutcome
{
    Hit,                    // local memory hit (R11.7)
    Miss,                   // server 200 (fetched fresh body)
    NotModified,            // server 304 (R11.9)
    NotFound,               // server 404 (R7.3)
    Unauthorized,           // server 401 (R3.1, R3.2)
    RateLimited,            // server 429 (R4.5)
    ServiceUnavailable,     // server 503 (R7.4, R7.5)
    TransientFailure        // 5xx exhausted retries OR unknown 4xx
}
```

### `Internal/TenantClientCacheClientMetrics.cs`

```csharp
internal sealed class TenantClientCacheClientMetrics
{
    public const string MeterName = "Skoruba.Duende.IdentityServer.TenantClientCache.Client";

    private readonly Meter _meter = new(MeterName, "1.0");
    private readonly Counter<long> _hitLocal;
    private readonly Counter<long> _hitRemote;
    private readonly Counter<long> _notModified;
    private readonly Counter<long> _miss;
    private readonly Counter<long> _unauthorized;
    private readonly Counter<long> _rateLimited;
    private readonly Counter<long> _serviceUnavailable;
    private readonly Counter<long> _transientFailure;
    private readonly Counter<long> _retryAttempted;
    private readonly Histogram<double> _duration;

    public TenantClientCacheClientMetrics()
    {
        _hitLocal           = _meter.CreateCounter<long>("client.read.hit_local");
        _hitRemote          = _meter.CreateCounter<long>("client.read.hit_remote");
        _notModified        = _meter.CreateCounter<long>("client.read.not_modified");
        _miss               = _meter.CreateCounter<long>("client.read.miss");
        _unauthorized       = _meter.CreateCounter<long>("client.read.unauthorized");
        _rateLimited        = _meter.CreateCounter<long>("client.read.rate_limited");
        _serviceUnavailable = _meter.CreateCounter<long>("client.read.service_unavailable");
        _transientFailure   = _meter.CreateCounter<long>("client.read.transient_failure");
        _retryAttempted     = _meter.CreateCounter<long>("client.read.retry_attempted");
        _duration           = _meter.CreateHistogram<double>("client.read.duration_ms");
    }

    // R11.11: tag only by `outcome`, NEVER by tenantKey.
    public void HitLocal()              => _hitLocal.Add(1);
    public void HitRemote()             => _hitRemote.Add(1);
    public void NotModified()           => _notModified.Add(1);
    public void Miss()                  => _miss.Add(1);
    public void Unauthorized()          => _unauthorized.Add(1);
    public void RateLimited()           => _rateLimited.Add(1);
    public void ServiceUnavailable()    => _serviceUnavailable.Add(1);
    public void TransientFailure()      => _transientFailure.Add(1);
    public void RetryAttempted()        => _retryAttempted.Add(1);
    public void RecordDuration(double ms, SdkCacheOutcome outcome) =>
        _duration.Record(ms, new KeyValuePair<string, object?>("outcome", outcome.ToString()));
}
```

### `Internal/TenantClientCacheClientRetryPolicy.cs`

```csharp
internal sealed class TenantClientCacheClientRetryPolicy
{
    public bool ShouldRetry(HttpStatusCode status, int attempt, int maxAttempts)
    {
        if (attempt >= maxAttempts) return false;
        return status is HttpStatusCode.InternalServerError      // R11.1: 500
            or HttpStatusCode.BadGateway                          // 502
            or HttpStatusCode.ServiceUnavailable                  // 503
            or HttpStatusCode.GatewayTimeout;                     // 504
        // R11.2: 4xx (400, 401, 403, 404, 405, 429) NEVER retry
    }

    public TimeSpan NextDelay(int attempt, TimeSpan baseDelay)
    {
        // R11.3: baseDelay * 2^attempt, capped at min(60s, baseDelay * 2^MaxRetryAttempts).
        // No jitter (deterministic for tests).
        var ticks = baseDelay.Ticks * (1L << attempt);
        var cap = TimeSpan.FromMinutes(1).Ticks;
        return TimeSpan.FromTicks(Math.Min(ticks, cap));
    }

    public static bool IsTransientNetworkException(Exception ex)
    {
        // R11.1: HttpRequestException always transient.
        // TaskCanceledException only transient when due to HttpClient.Timeout (not caller token).
        if (ex is HttpRequestException) return true;
        if (ex is TaskCanceledException tce && tce.InnerException is TimeoutException) return true;
        if (ex is SocketException) return true;
        return false;
    }
}
```


## Data Models

### `appsettings.json` sample

```json
{
  "TenantClientCache": {
    "Enabled": true,
    "AbsoluteTtl": "01:00:00",
    "RefreshInterval": "01:00:00",
    "WriteTimeoutMs": 2000,
    "MaxClientsPerTenant": 5000
  },

  "TenantClientCachePublicRead": {
    "ApiKeys": {
      "acme":   "9b74c9897bac770ffc029102a200c5de975be4d242f1d7c3aa2e21f51b1edcaf",
      "globex": "5d41402abc4b2a76b9719d911017c592e7b6e2d3f4a5b6c7d8e9fa0b1c2d3e4f"
    },
    "RateLimit": {
      "TokenLimit": 30,
      "TokensPerPeriod": 30,
      "ReplenishmentPeriod": "00:01:00",
      "QueueLimit": 0,
      "AutoReplenishment": true
    },
    "Cors": {
      "AllowedOrigins": [
        "https://acme.example.com",
        "https://app.globex.example.com"
      ],
      "PreflightMaxAgeSeconds": 600
    },
    "ResponseCache": {
      "MaxAgeSeconds": 60
    },
    "Audit": {
      "LogIpHash": true,
      "RemoteIpSalt": "REPLACE_WITH_PER_HOST_RANDOM_STRING"
    }
  }
}
```

### API key entry shape

| Property | Type | Constraint | Validates |
|---|---|---|---|
| Key (dictionary key) | string | trimmed, lowercase, ASCII; matches `^[a-z0-9_-]+$` | R1.5, R7.1 (consistent normalization) |
| Value | string | exactly 64 lowercased hex chars (`^[0-9a-f]{64}$`) | R1.4 |
| Multiplicity | one entry per tenant | revoke = remove + reload | R1.6, R3.5 |

### Wire format examples

**200 OK** (`GET /api/public/tenants/acme/clients/acme-spa`):

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8
ETag: W/"d3b07384d113edec49eaa6238ad5ff00cdb4f0a6dd8f8f8d8f8f8f8f8f8f8f8f"
Cache-Control: public, max-age=60, no-transform
Vary: X-Tenant-Api-Key
X-Snapshot-Last-Write-Utc: 2026-04-01T12:34:56.789Z
X-Snapshot-Version: 1
X-Content-Type-Options: nosniff
Access-Control-Expose-Headers: ETag, Cache-Control

{
  "clientId":"acme-spa",
  "clientName":"Acme SPA",
  "clientUri":null,
  "logoUri":null,
  "description":null,
  "enabled":true,
  "protocolType":"oidc",
  "redirectUris":["https://acme.example.com/callback"],
  "postLogoutRedirectUris":["https://acme.example.com/"],
  "allowedCorsOrigins":["https://acme.example.com"],
  "allowedGrantTypes":["authorization_code"],
  "allowedScopes":["openid","profile","acme.api"],
  "allowedIdentityTokenSigningAlgorithms":[],
  "requirePkce":true,
  "allowPlainTextPkce":false,
  "requireClientSecret":false,
  "requireConsent":false,
  "allowOfflineAccess":true,
  "allowAccessTokensViaBrowser":true,
  "alwaysIncludeUserClaimsInIdToken":false,
  "frontChannelLogoutUri":null,
  "frontChannelLogoutSessionRequired":true,
  "backChannelLogoutUri":null,
  "backChannelLogoutSessionRequired":true,
  "accessTokenLifetime":3600,
  "identityTokenLifetime":300,
  "authorizationCodeLifetime":300,
  "absoluteRefreshTokenLifetime":2592000,
  "slidingRefreshTokenLifetime":1296000,
  "refreshTokenExpiration":1,
  "refreshTokenUsage":1,
  "updateAccessTokenClaimsOnRefresh":false,
  "enableLocalLogin":true,
  "requirePushedAuthorization":false,
  "requireRequestObject":false,
  "initiateLoginUri":null,
  "useTenantRedirectPairs":true,
  "lastWriteUtc":"2026-04-01T12:34:56.789Z"
}
```

(R2.4 + R2.5: response root chỉ là `data`, không bao gồm envelope `version` / `tenantKey` / `clientId` / `lastWriteUtc` — chúng được phơi qua header dedicated; R6.6 + R6.7.)

**304 Not Modified** (`If-None-Match` matches):

```http
HTTP/1.1 304 Not Modified
ETag: W/"d3b07384d113edec49eaa6238ad5ff00cdb4f0a6dd8f8f8d8f8f8f8f8f8f8f8f"
Cache-Control: public, max-age=60, no-transform
Vary: X-Tenant-Api-Key
X-Snapshot-Last-Write-Utc: 2026-04-01T12:34:56.789Z
X-Snapshot-Version: 1
X-Content-Type-Options: nosniff
Content-Length: 0
```

(R6.4 — same headers as 200, empty body.)

**401 Unauthorized** (missing or invalid key):

```http
HTTP/1.1 401 Unauthorized
Content-Type: application/json; charset=utf-8

{"error":"invalid_api_key"}
```

(R3.1 / R3.2 / R3.3 — `missing_api_key` vs `invalid_api_key` distinguished only at the `error` string level; status, headers, timing characteristics identical.)

**429 Too Many Requests**:

```http
HTTP/1.1 429 Too Many Requests
Retry-After: 17
Content-Type: application/json; charset=utf-8

{"error":"rate_limit_exceeded"}
```

(R4.5 — `Retry-After` is `ceil(TimeUntilNextReplenishment.TotalSeconds)` with fallback `1`.)

**503 Service Unavailable** (snapshot pipeline disabled OR transient Redis failure):

```http
HTTP/1.1 503 Service Unavailable
Retry-After: 60
Content-Type: application/json; charset=utf-8

{"error":"snapshot_pipeline_disabled"}
```

OR

```http
HTTP/1.1 503 Service Unavailable
Retry-After: 5
Content-Type: application/json; charset=utf-8

{"error":"snapshot_unavailable"}
```

(R7.4 — `snapshot_pipeline_disabled` + `Retry-After: 60`; R7.5 — `snapshot_unavailable` + `Retry-After: 5`. R7.8 — never 500.)

## Error Handling

| Fault / scenario | HTTP status | Response body | Log level | Metric counter |
|---|---|---|---|---|
| Plain HTTP, non-localhost (R9.7) | 400 | `{"error":"https_required"}` | Warning | `tenant_client_cache.public_read.bad_request` (no `tenantKey` tag) |
| Missing `X-Tenant-Api-Key` (R3.1) | 401 | `{"error":"missing_api_key"}` | Warning | `tenant_client_cache.public_read.unauthorized` (no `tenantKey` tag) |
| Wrong key OR tenant not registered (R3.2, R3.3) | 401 | `{"error":"invalid_api_key"}` | Warning | `tenant_client_cache.public_read.unauthorized` (no `tenantKey` tag) |
| `tenantKey` malformed (R7.1) | 400 | `{"error":"invalid_tenant_key"}` | Warning | `tenant_client_cache.public_read.bad_request` (no `tenantKey` tag) |
| `clientId` malformed (R7.2) | 400 | `{"error":"invalid_client_id"}` | Warning | `tenant_client_cache.public_read.bad_request` (no `tenantKey` tag) |
| Token bucket exhausted (R4.5) | 429 + `Retry-After` | `{"error":"rate_limit_exceeded"}` | Warning | `tenant_client_cache.public_read.rate_limited` (tagged `tenantKey`) |
| Cache miss / corrupt / stale (R7.3 + parent R10.4 + R2.8) | 404 | `{"error":"snapshot_not_found"}` | Debug | `tenant_client_cache.public_read.miss` (tagged `tenantKey`) |
| Pipeline disabled (R7.4) | 503 + `Retry-After: 60` | `{"error":"snapshot_pipeline_disabled"}` | Error | `tenant_client_cache.public_read.service_unavailable` (tagged `tenantKey`) |
| `ITenantClientCacheService` throws transient (R7.5) | 503 + `Retry-After: 5` | `{"error":"snapshot_unavailable"}` | Error | `tenant_client_cache.public_read.service_unavailable` (tagged `tenantKey`) |
| Unhandled `Exception` falling through (R7.8) | 503 + `Retry-After: 5` | `{"error":"snapshot_unavailable"}` | Error | `tenant_client_cache.public_read.service_unavailable` (tagged `tenantKey`) |
| `OperationCanceledException` because client disconnected | (no response written, framework handles) | — | Debug | (none — cancellation, not failure) |
| Method other than GET / HEAD (R2.9) | 405 | (framework default) | Information | (none) |
| Successful 200 (R2.4 + R6.1 + R6.2 + R6.3 + R6.6 + R6.7 + R9.8) | 200 | snapshot data + headers | Information | `tenant_client_cache.public_read.hit` (tagged `tenantKey`) |
| Successful 304 (R6.4 + R6.5) | 304 | empty | Information | `tenant_client_cache.public_read.not_modified` (tagged `tenantKey`) |

### Exception swallowing boundaries

- `PublicTenantClientsController.GetAsync` lets `OperationCanceledException` propagate so the framework writes nothing on disconnect (R2.8 + parent spec R10.5 cancellation contract).
- `PublicReadExceptionFilter` catches every other unhandled `Exception` and converts it to 503 (R7.5, R7.8). Filter records `Outcome=ServiceUnavailable` Audit_Event_Public_Read at level Error (R8.2).
- `TenantApiKeyAuthorizationFilter` does not throw on auth failure; it short-circuits with `ObjectResult` (R3.1, R3.2).
- Configuration validation fails the host fast at startup; subsequent runtime errors are caught by `PublicReadExceptionFilter` (R1.4, R1.5, R4.3, R4.4, R5.6, R6.2, R9.6 enforced before first request).

### Retry policy (server)

KHÔNG có retry server-side. Một request, một `ITenantClientCacheService.ReadSnapshotAsync` call. Lý do: client-side SDK đã có retry (R11.1) và adding server retry sẽ amplify Redis load nếu Redis transiently slow.

## Security Model

| Threat | Vector | Mitigation | Validates | Forward-reference |
|---|---|---|---|---|
| Tenant enumeration via 401 differentiation | Attacker probes random `tenantKey` values, observes "tenant not registered" vs "wrong key" | Both → 401 with body `{"error":"invalid_api_key"}` (R3.3); response body, status, and `Retry-After` identical; constant-time hash comparison (R3.2) | R9.1 | Tasks: `Verify_Tenant_Enumeration_Identical_Response` |
| API-key brute-force enumeration | Attacker rotates many keys against one tenant | Per-tenant token bucket 30 req/min default (R4); per-IP rate limit delegated to operator's reverse proxy / WAF (out-of-scope, documented assumption) | R9.2 | Tasks: `Verify_RateLimit_Token_Bucket_Behavior`; ops runbook entry |
| Log poisoning via attacker-controlled `tenantKey` | Attacker spams arbitrary tenantKey strings hoping logs ingest cardinality | 401 / 400 logs do NOT include raw `tenantKey` (R3.4 + R7.1); only `RemoteIpHash` + `CorrelationId` (R8.7) | R9.3 | Tasks: `Verify_Audit_Log_Redaction` property test |
| API key leak via plaintext storage | Configuration file copy / git diff exposes plaintext key | Only SHA-256 hex stored in `Api_Key_Store` (R1.2, R1.4); plaintext lives only in consumer-side options | R9.5 (structural) | Tasks: `Verify_Configuration_Holds_Only_Hex_Hashes` |
| API key leak via logs | Error path logs request headers verbatim | Filter and controller use structured logging with explicit field allowlist; never include `X-Tenant-Api-Key`, hash, or response body (R3.4, R8.7, R10.10) | R9.5 | Tasks: `Verify_No_API_Key_In_Logs` |
| API key leak via HTTP-without-TLS | Consumer or curl test invokes endpoint over plain HTTP | `HttpsRequiredFilter` returns 400 before API key validation (R9.7); reverse proxy is expected to reject plain HTTP first, but the host enforces defense-in-depth | R9.7 | Tasks: `Verify_Plain_HTTP_Rejected_With_400` |
| Cross-origin scraping by malicious browser | Web app on attacker origin tries to fetch with cookies | CORS allowlist defaults empty (R5.4); no `AllowCredentials` (R5.3); `Vary: X-Tenant-Api-Key` prevents intermediate cache poisoning (R6.3) | R9.4 | Tasks: `Verify_CORS_Default_Empty_Origins` |
| Snapshot scraping at scale | Single API key iterates all `clientId` for tenant | Per-tenant token bucket caps at 30 req/min (R4.2); ~33 minutes per 1000-client tenant (R9.4 documented threshold) | R9.4 | Tasks: `Verify_RateLimit_Per_Tenant_Throughput` |
| Secret material leakage in body | Future refactor of envelope adds a secret field | Body is `envelope.Data` (Public_Safe_Fields only) by parent spec R2; static-analysis enforced by parent spec R12 + R15.4; `[Tags("PublicTenantClients")]` + dedicated controller forbids `IClientService` injection (R12.10) | R9.5 | Tasks: `Verify_Controller_Has_No_DbContext_Or_IClientService` |
| Sniff-based content-type confusion | Browser caches snapshot as image / script | `X-Content-Type-Options: nosniff` (R9.8) | R9.8 | Tasks: `Verify_Response_Headers_Contain_Nosniff` |
| Edge transformation invalidating ETag | Compression or transform proxy mutates body bytes | `Cache-Control: ..., no-transform` (R9.8) | R9.8 | Tasks: `Verify_Cache_Control_Includes_No_Transform` |
| Raw IP leak in logs (GDPR) | Default Serilog enricher captures `RemoteIpAddress` | `IpHashHelper` produces `sha256(remoteIp + salt)`; `Audit:LogIpHash` toggle (R3.6); `RemoteIpSalt` non-empty in Production (R9.6) | R9.6 | Tasks: `Verify_RemoteIpHash_NotRaw_In_Audit_Event` |
| Per-tenant cardinality explosion in metrics | Metric `unauthorized` tagged by `tenantKey` lets attacker enumerate via Prometheus | `Unauthorized` and `BadRequest` counters omit `tenantKey` tag (R8.4); other counters include the tag because tenant identity is already authenticated for those code paths | R8.4, R9.3 | Tasks: `Verify_Counter_Tag_Policy` property test |

Operator's reverse proxy / WAF MUST handle: per-IP rate limiting, IP reputation filtering, header size limits, body size limits. These are explicit operator responsibilities documented in R9.2; the host does not duplicate them.

## Observability

### Meter reuse (server)

- Server-side metrics extend the existing Meter `"TenantClientCache"` (R8.3). New counters:
  - `tenant_client_cache.public_read.hit`            (tagged `tenantKey`)
  - `tenant_client_cache.public_read.not_modified`   (tagged `tenantKey`)
  - `tenant_client_cache.public_read.miss`           (tagged `tenantKey`)
  - `tenant_client_cache.public_read.unauthorized`   (NO `tenantKey` tag — R8.4)
  - `tenant_client_cache.public_read.rate_limited`   (tagged `tenantKey`)
  - `tenant_client_cache.public_read.bad_request`    (NO `tenantKey` tag — R8.4)
  - `tenant_client_cache.public_read.service_unavailable` (tagged `tenantKey`)
- New histogram:
  - `tenant_client_cache.public_read.duration_ms`    (tagged `outcome` + optional `tenantKey` per R8.4)

### New Meter (SDK)

- SDK side uses Meter `"Skoruba.Duende.IdentityServer.TenantClientCache.Client"` (R11.11). Different name from server intentionally (different runtime, different cardinality budget).
- Counters listed in `TenantClientCacheClientMetrics`. Tag policy: only `outcome`, never `tenantKey` (R11.11) — consumers wanting per-tenant breakdown can dimension via structured logs.

### Audit_Event_Public_Read schema

| Field | Type | Source | Notes |
|---|---|---|---|
| `EventType` | string | enum from R8.1 | `TenantClientCachePublicRead.{Hit, NotModified, Miss, Unauthorized, RateLimited, BadRequest, ServiceUnavailable}` |
| `TenantKey` | string | normalized URL path | OMITTED for `Unauthorized` and `BadRequest` (R8.4 — same anti-enumeration rationale) |
| `ClientId` | string | URL path, trimmed | OMITTED for `Unauthorized` and `BadRequest` |
| `Outcome` | string | matches event suffix | One of: `Hit, NotModified, Miss, Unauthorized, RateLimited, BadRequest, ServiceUnavailable` |
| `DurationMs` | double | `Stopwatch` from filter / controller entry | total wall-clock for the request |
| `CorrelationId` | string \| null | `Activity.Current?.TraceId.ToString()` | R8.6 |
| `RemoteIpHash` | string \| null | `IpHashHelper.Hash(...)` | OMITTED when `Audit:LogIpHash = false` (R3.6); otherwise SHA-256 hex |
| `HttpStatus` | int | `Response.StatusCode` | written for completeness |
| `ETagSent` | string \| null | controller | only logged on `Hit` and `NotModified`; never the request `If-None-Match` value (irrelevant) |
| `RetryAfterSeconds` | int \| null | `Response.Headers.RetryAfter` | only logged on `RateLimited` and `ServiceUnavailable` |

### Forbidden fields

Audit MUST NOT contain (R3.4, R8.7, R10.10):

- Raw `X-Tenant-Api-Key` header value.
- SHA-256 hash of the API key.
- Response body bytes (snapshot data).
- The full snapshot envelope object.
- Any field whose name matches `*Secret*` (case-insensitive). Property 1 (parent spec) gives this guarantee structurally; the public-read controller never touches secret fields.
- Raw remote IP (only `RemoteIpHash` allowed; R9.6).

### `RemoteIpHash` flow

```mermaid
flowchart LR
    IP["HttpContext.Connection.RemoteIpAddress"] --> H{LogIpHash<br/>enabled?}
    H -- no --> N[null]
    H -- yes --> S[salt = Audit:RemoteIpSalt]
    S -- empty + Production --> Fail[Validator fails fast<br/>(R9.6)]
    S -- non-empty --> SHA["sha256(ip + ':' + salt) → hex lower"]
    SHA --> Out["RemoteIpHash field"]
    N --> Out
    Fail --> Halt[Host startup aborts]
```

`RemoteIpHash` cùng request luôn deterministic (same IP + same salt → same hex), giúp operator group by attacker IP mà không thấy raw IP trong log.

## Backward Compatibility

### File-level change summary

| File | NEW / EDIT | Path | Notes |
|---|---|---|---|
| `Configuration/TenantClientCachePublicReadOptions.cs` | NEW | `src/Skoruba.Duende.IdentityServer.Admin.UI.Api/` | POCO + nested options classes |
| `Configuration/TenantClientCachePublicReadOptionsValidator.cs` | NEW | `src/Skoruba.Duende.IdentityServer.Admin.UI.Api/` | `IValidateOptions<>` |
| `Services/PublicTenantClients/ITenantApiKeyValidator.cs` | NEW | `src/Skoruba.Duende.IdentityServer.Admin.UI.Api/` | interface |
| `Services/PublicTenantClients/TenantApiKeyValidator.cs` | NEW | `src/Skoruba.Duende.IdentityServer.Admin.UI.Api/` | singleton impl |
| `Services/PublicTenantClients/TenantApiKeyAuthorizationFilter.cs` | NEW | `src/Skoruba.Duende.IdentityServer.Admin.UI.Api/` | `IAsyncAuthorizationFilter` |
| `Services/PublicTenantClients/HttpsRequiredFilter.cs` | NEW | `src/Skoruba.Duende.IdentityServer.Admin.UI.Api/` | `IAsyncAuthorizationFilter` |
| `Services/PublicTenantClients/PublicReadExceptionFilter.cs` | NEW | `src/Skoruba.Duende.IdentityServer.Admin.UI.Api/` | `IAsyncExceptionFilter` |
| `Services/PublicTenantClients/IpHashHelper.cs` | NEW | `src/Skoruba.Duende.IdentityServer.Admin.UI.Api/` | singleton helper |
| `Services/TenantClientCache/TenantClientCacheMetrics.cs` | EDIT | `src/Skoruba.Duende.IdentityServer.Admin.UI.Api/` | append 7 counter + 1 histogram + helper methods (no rename, additive only) |
| `Controllers/PublicTenantClientsController.cs` | NEW | `src/Skoruba.Duende.IdentityServer.Admin.UI.Api/` | one action; `[AllowAnonymous]` + `[Tags("PublicTenantClients")]` |
| `Helpers/StartupHelpers.cs` | EDIT | `src/Skoruba.Duende.IdentityServer.Admin.UI.Api/` | add `AddTenantClientCachePublicRead(...)` extension method (and a corresponding `UseTenantClientCachePublicRead(...)` if needed for middleware ordering); existing methods untouched |
| `appsettings.json` (template) | EDIT | `src/Skoruba.Duende.IdentityServer.Admin.UI.Api/` | append `TenantClientCachePublicRead` section with empty `ApiKeys`, default sub-sections |
| `Skoruba.Duende.IdentityServer.TenantClientCache.Client.csproj` | NEW | `src/Skoruba.Duende.IdentityServer.TenantClientCache.Client/` | netstandard2.0? — NO; `net8.0` per R10.1 |
| `TenantClientCacheClientServiceCollectionExtensions.cs` | NEW | `src/Skoruba.Duende.IdentityServer.TenantClientCache.Client/` | extension method |
| `TenantClientCacheClientOptions.cs` | NEW | `src/Skoruba.Duende.IdentityServer.TenantClientCache.Client/` | options POCO |
| `ITenantClientCacheClient.cs` | NEW | `src/Skoruba.Duende.IdentityServer.TenantClientCache.Client/` | interface |
| `TenantClientCacheClient.cs` | NEW | `src/Skoruba.Duende.IdentityServer.TenantClientCache.Client/` | impl |
| `Models/PublicClientSnapshot.cs` | NEW | `src/Skoruba.Duende.IdentityServer.TenantClientCache.Client/Models/` | sealed record, 38 fields |
| `Models/TenantClientSnapshotResult.cs` | NEW | `src/Skoruba.Duende.IdentityServer.TenantClientCache.Client/Models/` | sealed record |
| `Models/SdkCacheOutcome.cs` | NEW | `src/Skoruba.Duende.IdentityServer.TenantClientCache.Client/Models/` | enum |
| `Internal/TenantClientCacheClientMetrics.cs` | NEW | `src/Skoruba.Duende.IdentityServer.TenantClientCache.Client/Internal/` | Meter wrapper |
| `Internal/TenantClientCacheClientRetryPolicy.cs` | NEW | `src/Skoruba.Duende.IdentityServer.TenantClientCache.Client/Internal/` | retry decision + delay calc |
| `Internal/TenantClientCacheClientCacheEntry.cs` | NEW | `src/Skoruba.Duende.IdentityServer.TenantClientCache.Client/Internal/` | record holding `(Snapshot, Etag, LastWriteUtc, Version)` |
| Solution file (`*.sln`) | EDIT | `/` | add new SDK project |
| `tests/.../UnitTests/PublicTenantClients/...` | NEW | `tests/Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests/` | controller, filters, validator unit tests |
| `tests/.../IntegrationTests/PublicTenantClients/...` | NEW | `tests/Skoruba.Duende.IdentityServer.Admin.IntegrationTests/` | full pipeline with `MemoryDistributedCache` + fake `ITenantClientCacheService` |
| `tests/.../Client.UnitTests/...` | NEW | `tests/Skoruba.Duende.IdentityServer.TenantClientCache.Client.UnitTests/` | SDK retry, cache, deserialization, metrics |

R12.1 / R12.10: KHÔNG file controller / startup hiện có nào bị thay đổi semantic; chỉ thêm method extension hoặc registration code. R12.5 / R12.6: KHÔNG migration EF, KHÔNG NuGet third-party mới.

### Touchpoints with existing host wiring

- `Helpers/StartupHelpers.cs` đã có pattern `AddAdminAspNetIdentityServices`, `AddAdminAuthentication`, `AddIdentityServer`, ... Method mới `AddTenantClientCachePublicRead(IServiceCollection, IConfiguration)` đặt cuối file, registration block tự đóng kín, không sửa method cũ.
- `Program.cs` / `Startup.cs` của Admin_Api_Host gọi method mới qua một dòng `services.AddTenantClientCachePublicRead(Configuration);` và một dòng `app.UseRateLimiter();` (đã có sẵn nếu host đã sử d���ng rate limiter cho feature khác — confirm khi implement; nếu chưa, thêm dòng đó vào `Configure(app)` block).
- OpenAPI generator (NSwag/Swashbuckle) tự discover controller mới via `[Tags("PublicTenantClients")]` → tag riêng, không trộn với `Clients` (R12.9).

## Risks AND Mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Tenant key xuất hiện trong access log của reverse proxy (path-based routing log full URL) | Medium | Medium — reveals tenant set to ops staff with log access | Document trong runbook: configure NGINX / ALB access log to redact `/api/public/tenants/[^/]+/clients/[^/]+` path segment OR use HTTPS-only + restricted log access. Application-level access log (Serilog request middleware) MUST log only the route template (not the substituted values) for this endpoint — wire a custom enricher or rely on `IHttpLoggingInterceptor` to drop the path. |
| API key hashes leaked via `appsettings.json` in git | Low | High — but only enables impersonation, not data tier | SHA-256 hash is one-way; rotation = swap value + reload (R1.6). Reduce blast by storing hashes in Azure Key Vault / AWS Secrets Manager and binding via configuration provider — out-of-scope of this spec but documented. |
| `IOptionsMonitor` hot-reload race: a request arrives between `IConfigurationProvider.Reload` and the new dictionary being published | Low | Low — at most one stale validate per tenant per reload | `IOptionsMonitor.CurrentValue` reads volatile snapshot atomically; the worst case is a request validating against the prior dictionary (legacy key still valid for one extra request). Acceptable per R1.6 wording ("on the next request"). Document in runbook that operators should treat reload as eventually consistent within sub-second window. |
| SDK in-memory cache returns stale data when server has rotated key but consumer holds an old snapshot | Low | Low | SDK respects `Cache-Control: max-age` (R11.6); on next refresh consumer revalidates with `If-None-Match` (R11.9). Worst-case staleness ≤ `MaxClientCacheTtl` (default 5 min). Caller can force revalidation via `GetClientAsync(..., ifNoneMatch, ct)` (R11.8). |
| `503 snapshot_unavailable` hides Redis OOM that should escalate | Medium | Medium — operator may dismiss as transient | Audit_Event_Public_Read at level Error includes exception message (redacted) + type name; metric counter `service_unavailable` tagged with `tenantKey` allows per-tenant alerting. Operator's monitoring SHOULD alert on rate of this counter (e.g. > 5/min for any tenant). Document threshold in runbook. |
| Path validation runs after token consumption (R4.9 partial gap) | Low | Low | Route constraint `{tenantKey:regex(^[a-z0-9_-]+$)}` rejects most malformed `tenantKey` at routing layer (404, no token consumed). Length / `clientId` validation does consume one token per malformed request, but at default 30/min that is negligible. Document in security runbook; if pressure increases, lift validation into an `IAsyncResourceFilter` that runs before `EnableRateLimiting`. |
| Salt persistence: per-host salt regenerated on each restart breaks `RemoteIpHash` continuity for incident investigation | Medium | Low | Operator MUST persist salt outside the binary deployment (e.g. config file mounted from secrets store, or env var injected at startup). Validator emits Warning if salt is empty in Dev/Staging; fails fast in Production (R9.6). See "Open Questions" for stable-salt strategy decision. |
| Whitelist drift: a future `Public_Safe_Fields` change in parent spec produces a snapshot the SDK cannot deserialize | Medium | Medium | SDK declares all 38 fields as `[JsonPropertyName]`, unknown fields ignored by `System.Text.Json` default. New required fields → SDK semver bump (`PackageVersion`). Document in SDK changelog. |
| Multi-tenant rate-limit unfair to NAT'd consumer | Low | Low | Per-tenant partition (R4.6) means a single tenant behind NAT shares one bucket; deemed acceptable for B2B-style consumer pattern. Per-IP layer (R9.2) handled by reverse proxy. |
| `OnRejected` callback writes response after some headers were already written by middleware | Low | Low | ASP.NET Core 8 rate limiter writes 429 BEFORE controller binds; tested in Microsoft sample. Document risk + integration test that asserts `429` headers correct. |

## Open Questions

1. **net10.0 host vs net8.0 SDK**: Admin_Api_Host targets the framework version chosen by the parent solution (need to confirm at implementation time — may already be net8.0 in this repository). SDK explicitly targets `net8.0` per R10.1 to maximize consumer compatibility. If host upgrades to net10.0 later, SDK should remain net8.0 unless we accept dropping older consumers. **Decision deferred to first task** — confirm host TFM and adjust references.
2. **Weak ETag vs strong**: Spec mandates weak ETag `W/"<hex>"` (R6.1) because compression / no-transform rules and the lack of byte-exact serialization guarantees across runtimes make weak the safer choice. We accept that intermediate caches can serve a "byte-different but semantically equal" response — for this contract, identity at semantic level is sufficient. Confirmed in design.
3. **TokenBucket vs FixedWindow rate limiter**: Token bucket allows short bursts (full bucket can be drained quickly), which matches consumer SDK pattern of bootstrapping on app start. Fixed window would penalize the bootstrap pattern. **Decision: TokenBucket** (R4.1, default 30/30/1min). Open to revisit per operator feedback.
4. **SDK BaseAddress discovery (out-of-scope of this spec)**: Should the SDK pull base address from a service-registry (Consul, Eureka, AWS ALB DNS) instead of hardcoded option? Out-of-scope of R10.7 (which mandates explicit `BaseAddress`). Future SDK versions MAY add a discovery extension; this spec does not.
5. **Salt persistence**: R9.6 requires non-empty random salt in Production. Strategy: (a) ops sets `TenantClientCachePublicRead:Audit:RemoteIpSalt` in Key Vault / secrets manager; (b) host generates on first run, persists to a sidecar file; (c) host accepts ephemeral salt and rotates per restart (loses cross-restart correlation). **Recommended (a)** documented in runbook; (c) acceptable for development. Open: should host fail-fast in Staging too? Currently only Production fails.
6. **Unknown 4xx folding into `TransientFailure`**: SDK currently folds every non-mapped 4xx (e.g. an upgrade where server returns 422) into `SdkCacheOutcome.TransientFailure`. Alternative: surface a new outcome `BadRequest` to align with R7.1/R7.2. **Decision: keep folded into TransientFailure for v1**; document expectation that consumers treat all non-listed status as "fail-soft, retry later". Reconsider when adding a new outcome bucket.

## Performance

### Latency budget (server-side, p99)

| Stage | Target ms | Source / dependency |
|---|---|---|
| HTTPS check (TLS terminated upstream) | ~0.0 | upstream proxy |
| CORS middleware | ~0.1 | preflight only on OPTIONS |
| API key validate (SHA-256 + FixedTimeEquals) | ~0.2 | in-process; cheap |
| Rate limit check (token bucket) | ~0.1 | in-process |
| Path validation regex | ~0.05 | compiled regex |
| `ITenantClientCacheService.ReadSnapshotAsync` | ≤ 5 ms | Redis network round-trip + parent spec R14.1 |
| Serialize `Data` to bytes | ~1 ms | snapshot ≤ 256 KiB |
| Compute SHA-256 over bytes | ~0.5 ms | hardware accelerated |
| ETag negotiation (string compare) | ~0.05 | in-process |
| Write headers + flush body | ~2 ms | Kestrel + TCP send |
| **Total p99 (cache hit)** | **≤ ~9 ms** | inside test bench; ~25 ms allowable in production with WAN egress |

R-NFR Performance bullet (R-Performance) ràng buộc p99 ≤ upstream + 5 ms; budget trên đáp ứng.

### SDK retry worst-case wall-clock

| Attempt | Pre-delay | HTTP timeout (default) | Cumulative |
|---|---|---|---|
| 1 (initial) | 0 ms | 5 s | 5.0 s |
| 2 (retry 1) | 200 ms | 5 s | 10.2 s |
| 3 (retry 2) | 400 ms | 5 s | 15.6 s |

Default `MaxRetryAttempts = 2` ⇒ worst-case wall-clock ~15.6 s (R11.3 cap = 60 s ensures one further hypothetical attempt does not exceed 60 s pre-delay between retries).

`HttpClient.Timeout = HttpTimeout` (R11.12) — caller's `CancellationToken` is the only mechanism to abort earlier (R11.5).

### SDK in-memory cache

- Cache TTL = `min(Cache-Control max-age, MaxClientCacheTtl)` (R11.6).
- Default server `max-age = 60s`, default SDK `MaxClientCacheTtl = 300s` ⇒ effective TTL = 60s.
- Hit-local path is O(1) dictionary lookup via `IMemoryCache` ≈ 100 ns (R11.7).
- Misses fall back to HTTP request with optional revalidation `If-None-Match` (R11.9), which can return 304 with empty body — saves snapshot transfer cost.

### Memory footprint

- Server: per-request additional allocation ~2 KB (header strings + ETag computation buffer, `stackalloc` where possible).
- SDK: per-cache-entry ~3 KB (snapshot record + ETag string + metadata). Default `IMemoryCache` size limit not set; consumer can configure via `services.AddMemoryCache(o => o.SizeLimit = ...)` if needed.

### Throughput

- Default per-tenant rate `30 req/min` ⇒ 0.5 req/s per tenant per host. With 100 tenants and `N` host replicas, aggregate throughput is `N * 50 req/s` for steady state. Burst capability up to 30 req in `~ReplenishmentPeriod` per tenant.
- Redis read latency dominates; Redis cluster sized for write side already accommodates feature without additional capacity (R12.2).


## Correctness Properties

*A property is a characteristic or behavior that should hold true across all valid executions of a system — essentially, a formal statement about what the system should do. Properties serve as the bridge between human-readable specifications and machine-verifiable correctness guarantees.*

Sau prework + reflection (xem Glossary `Acceptance Criteria mapping` của requirements + prework analysis đã lưu), 47 EARS criterion test-được được hợp nhất xuống 20 property độc lập. Smoke / example checks (configuration shape, single-flag toggles, project-file metadata) được test bằng deterministic xUnit tests song song property tests, KHÔNG nằm trong danh sách property dưới đây.

### Property 1: Options validator rejects malformed entries without leaking values

*For any* `TenantClientCachePublicReadOptions` instance whose `ApiKeys` dictionary contains at least one entry where the key is non-trimmed-lowercase OR contains uppercase/whitespace OR the value does not match `^[0-9a-f]{64}$`, OR whose `RateLimit.TokenLimit` falls outside `[1, 10000]`, OR whose `RateLimit.ReplenishmentPeriod` falls outside `[00:00:01, 01:00:00]`, OR whose `Cors.AllowedOrigins` contains an origin that is not an absolute https URL (or http for `localhost`), OR whose `Cors.PreflightMaxAgeSeconds` falls outside `[0, 86400]`, OR whose `ResponseCache.MaxAgeSeconds` falls outside `[0, 3600]`, OR (in Production) whose `Audit.RemoteIpSalt` is empty, the validator SHALL return `Fail` whose error messages name the offending field BUT SHALL NOT include any offending API key value.

**Validates: Requirements 1.4, 1.5, 4.3, 4.4, 5.6, 5.7, 6.2, 9.6**

### Property 2: API key validator hot-reload

*For any* sequence `(tenant, oldHash, newHash, plaintextOld, plaintextNew)` where `IOptionsMonitor` source is updated from `oldHash` to `newHash` between two requests, the second `TryValidate(tenant, plaintextNew)` call SHALL return `true` AND `TryValidate(tenant, plaintextOld)` SHALL return `false`, without process restart.

**Validates: Requirements 1.6, 3.5**

### Property 3: API key constant-time validation

*For any* `(tenantKey, plaintextKey)` pair whose `sha256-hex-lowercase(plaintextKey)` matches the configured hash for `normalize(tenantKey)`, `ITenantApiKeyValidator.TryValidate` SHALL return `true`; for any pair whose hash does not match (whether tenant unregistered, hash mismatch, or empty store), `TryValidate` SHALL return `false`. Comparison SHALL be implemented via `CryptographicOperations.FixedTimeEquals`.

**Validates: Requirements 3.1, 3.2**

### Property 4: Tenant enumeration resistance

*For any* unregistered `tenantKey1` and registered-but-mismatched `(tenantKey2, wrongPlaintext)` pair, the responses to the two corresponding HTTPS requests SHALL be identical in: HTTP status (`401`), response body (`{"error":"invalid_api_key"}`), and absence of `Retry-After` / tenant-specific headers. The audit log entry for both SHALL omit the `TenantKey` field.

**Validates: Requirements 3.3, 9.1**

### Property 5: Path inputs only

*For any* request whose URL path contains `(tenantKey, clientId)`, plus any combination of arbitrary `tenantKey` / `clientId` values supplied via query string, request body, or any header other than `X-Tenant-Api-Key`, the controller SHALL invoke `ITenantClientCacheService.ReadSnapshotAsync` with arguments equal to `path.tenantKey.Trim().ToLowerInvariant()` and `path.clientId.Trim()` AND SHALL ignore the foreign values entirely.

**Validates: Requirements 2.2, 2.3, 3.7**

### Property 6: Whitespace-only API key header rejected

*For any* request whose `X-Tenant-Api-Key` header value is null, empty, OR composed entirely of whitespace, the response SHALL be HTTP 401 with body `{"error":"missing_api_key"}` AND `ITenantClientCacheService.ReadSnapshotAsync` SHALL NOT be invoked.

**Validates: Requirements 3.1, 3.7**

### Property 7: Authentication runs before rate limiter

*For any* sequence of unauthenticated requests targeting the same `tenantKey`, the token bucket for that tenant SHALL retain `TokenLimit` tokens after the entire sequence (no token consumption for 401-bound requests).

**Validates: Requirements 3.8, 4.7**

### Property 8: Rate limit per-tenant + 429 contract

*For any* burst of `n > TokenLimit` authenticated requests targeting the same `tenantKey` from any combination of remote IPs and authenticated API keys, exactly `TokenLimit` requests SHALL receive non-429 responses AND the remaining `n - TokenLimit` SHALL receive HTTP 429 with body `{"error":"rate_limit_exceeded"}` AND header `Retry-After: <ceil(seconds)>`. For each 429 response, `ITenantClientCacheService.ReadSnapshotAsync` SHALL NOT be invoked.

**Validates: Requirements 4.5, 4.6, 4.8**

### Property 9: Path validation rejects malformed inputs

*For any* `tenantKey` that is null/empty/whitespace, longer than 128 chars, OR contains a character outside `^[a-z0-9_-]+$` after `Trim().ToLowerInvariant()`, the response SHALL be HTTP 400 with body `{"error":"invalid_tenant_key"}`. *For any* `clientId` that is null/empty/whitespace OR longer than 200 chars after `Trim()`, the response SHALL be HTTP 400 with body `{"error":"invalid_client_id"}`. In both cases `ITenantClientCacheService.ReadSnapshotAsync` SHALL NOT be invoked.

**Validates: Requirements 7.1, 7.2**

### Property 10: Snapshot serialization + ETag determinism

*For any* `ClientCacheSnapshotEnvelope` with valid `Data`, the bytes produced by `JsonSerializer.SerializeToUtf8Bytes(envelope.Data, JsonSerializerDefaults.Web)` SHALL be byte-equal across repeated invocations on the same envelope, AND the response `ETag` header SHALL equal `W/"<sha256-hex-lowercase>"` of those bytes. The serialized JSON object SHALL have exactly the camelCase keys of the 38 Public_Safe_Fields and SHALL NOT contain `version`, `tenantKey`, `clientId`, `lastWriteUtc` at the root.

**Validates: Requirements 2.4, 2.5, 6.1, 6.8**

### Property 11: If-None-Match negotiation

*For any* `ClientCacheSnapshotEnvelope` for which the controller would normally return 200 with `ETag E`, sending the same request with header `If-None-Match: E` (with or without `W/` prefix, with or without surrounding whitespace, OR `If-None-Match: *`) SHALL produce HTTP 304 with empty body AND identical `ETag`, `Cache-Control`, `Vary`, `X-Snapshot-Last-Write-Utc`, `X-Snapshot-Version`, `X-Content-Type-Options` headers compared to the corresponding 200 response.

**Validates: Requirements 6.4, 6.5**

### Property 12: Response header completeness for success outcomes

*For any* 200 OR 304 response, the response SHALL include headers: `ETag`, `Cache-Control: public, max-age=<configured>, no-transform`, `Vary: X-Tenant-Api-Key`, `X-Snapshot-Last-Write-Utc: <iso8601 of envelope.LastWriteUtc>`, `X-Snapshot-Version: <envelope.Version>`, `X-Content-Type-Options: nosniff`. For 200 responses, `Content-Type` SHALL be `application/json; charset=utf-8`.

**Validates: Requirements 2.6, 6.2, 6.3, 6.6, 6.7, 9.8**

### Property 13: Failure body schema closed; never 5xx-other-than-503; never 3xx

*For any* terminal failure outcome (Unauthorized, BadRequest, NotFound, RateLimited, ServiceUnavailable, PipelineDisabled), the response body SHALL parse to a JSON object with exactly one property `error` of type string AND the response status SHALL be one of `{400, 401, 404, 405, 429, 503}` AND SHALL NOT be `5xx ∉ {503}` NOR `3xx`. *For any* unhandled `Exception` thrown anywhere in the pipeline, the response status SHALL be 503 with body `{"error":"snapshot_unavailable"}` AND SHALL NEVER be 500.

**Validates: Requirements 7.5, 7.6, 7.7, 7.8**

### Property 14: Audit log redaction

*For any* request and any outcome, the emitted Audit_Event_Public_Read entry SHALL NOT contain (as substring of any structured field value): the raw `X-Tenant-Api-Key` value, its SHA-256 hash, the response body, the snapshot envelope, the raw remote IP, OR any field whose name matches `*Secret*` (case-insensitive). For Unauthorized and BadRequest outcomes, the entry SHALL NOT contain the raw `tenantKey` from the URL path.

**Validates: Requirements 3.4, 8.7, 8.8, 9.3, 9.5, 10.10**

### Property 15: Audit event shape + log levels per outcome

*For any* terminal outcome, exactly one Audit_Event_Public_Read entry SHALL be emitted whose `EventType` matches the outcome name, whose `Outcome` field equals the outcome, whose `DurationMs` is non-negative, whose `CorrelationId` equals `Activity.Current?.TraceId.ToString()` (or null when no Activity), AND whose log level matches the (Outcome → Level) table: `Information` for `Hit`/`NotModified`, `Debug` for `Miss`, `Warning` for `Unauthorized`/`RateLimited`/`BadRequest`, `Error` for `ServiceUnavailable`.

**Validates: Requirements 8.1, 8.2, 8.6**

### Property 16: Metric tag policy

*For any* incremented counter from the public-read counter set, the tag set SHALL include `tenantKey` (lowercased) for `Hit`, `NotModified`, `Miss`, `RateLimited`, `ServiceUnavailable` outcomes AND SHALL omit any `tenantKey` tag for `Unauthorized` and `BadRequest` outcomes. *For any* histogram measurement on `tenant_client_cache.public_read.duration_ms`, the tag set SHALL include `outcome` AND (where applicable per the table above) `tenantKey`. No counter or histogram SHALL include a `clientId` tag.

**Validates: Requirements 8.4, 8.5**

### Property 17: HTTPS gate + RemoteIpHash

*For any* request whose scheme is plain HTTP AND whose host is not `localhost` (and whose `RemoteIpAddress` is not loopback), the response SHALL be HTTP 400 with body `{"error":"https_required"}` BEFORE API key validation is invoked. *For any* IP value `ip` and salt `salt`, the helper SHALL produce `RemoteIpHash = sha256-hex-lowercase(ip + ":" + salt)` AND no audit field SHALL contain the raw `ip` substring.

**Validates: Requirements 9.6, 9.7**

### Property 18: PublicClientSnapshot field set + camelCase

*For any* property declared on `PublicClientSnapshot`, the property SHALL be one of the 38 Public_Safe_Fields named in spec `tenant-client-cache-expansion` Glossary AND SHALL carry a `[JsonPropertyName]` attribute whose value is the camelCase form of the C# property name. The DTO SHALL NOT contain any property whose name matches `clientSecrets`, `claims`, `properties`, `identityProviderRestrictions`, `pairWiseSubjectSalt`, `id`, OR `*Secret*` (case-insensitive).

**Validates: Requirements 10.5, 12.7**

### Property 19: SDK retry decision + backoff formula

*For any* sequence of `m` HTTP responses where the last response has status `s_final` and earlier responses have status `s_i ∈ {500, 502, 503, 504}` OR throw `HttpRequestException`/`SocketException`/`TaskCanceledException(InnerException = TimeoutException)`, the SDK SHALL issue at most `min(m, MaxRetryAttempts + 1)` HTTP calls, return after the first non-retriable status, AND insert a delay of `RetryBaseDelay * 2^(attempt - 1)` capped at `min(60s, RetryBaseDelay * 2^MaxRetryAttempts)` between attempts. *For any* response with `s ∈ {400, 401, 403, 404, 405, 429}`, the SDK SHALL issue exactly 1 HTTP call.

**Validates: Requirements 11.1, 11.2, 11.3**

### Property 20: SDK in-memory cache + revalidation

*For any* sequence of SDK `GetClientAsync` calls on the same `(tenantKey, clientId)` key:
- A successful 200 response SHALL populate the cache with TTL `min(server max-age, MaxClientCacheTtl)` (TTL of 0 SHALL behave as no-cache).
- A subsequent call within TTL SHALL return `Outcome=Hit` without an HTTP request.
- A subsequent call after TTL SHALL issue an HTTP request with `If-None-Match: <cached-etag>`; on HTTP 304 the SDK SHALL return `Outcome=NotModified` with `Snapshot=<previously cached>` AND extend the cache TTL by the new `max-age`; on HTTP 200 the SDK SHALL replace the cached entry.
- A call passing an explicit non-null `ifNoneMatch` argument SHALL bypass the local cache lookup AND issue an HTTP request with that header.
- For any two distinct `(tenantKey, clientId)` keys, snapshots SHALL be isolated.

**Validates: Requirements 11.6, 11.7, 11.8, 11.9, 11.10**

## Testing Strategy

### Test pyramid

| Layer | Test type | Project | Notes |
|---|---|---|---|
| Server unit | Property-based + xUnit examples | `tests/Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests/PublicTenantClients/` (NEW project if not present) | Properties P1–P17 against `MemoryDistributedCache` + fake `ITenantClientCacheService` + spy `ILogger` + `MeterListener`. |
| SDK unit | Property-based + xUnit examples | `tests/Skoruba.Duende.IdentityServer.TenantClientCache.Client.UnitTests/` (NEW) | Properties P18–P20 against `HttpMessageHandler` test double + `MemoryCache`. |
| Server integration | xUnit + `WebApplicationFactory` + `MemoryDistributedCache` | `tests/Skoruba.Duende.IdentityServer.Admin.IntegrationTests/PublicTenantClients/` | End-to-end pipeline (HTTPS gate → CORS → API key → rate limit → controller → ETag). Smoke + 9.4 throughput burst test. |
| SDK integration | xUnit consumer harness against running `WebApplicationFactory` | Same integration project | Wire SDK against in-process server; verify retry, revalidation, cache hit-local end-to-end. |

### Test seam wiring

```csharp
public sealed class PublicReadTestFixture : IDisposable
{
    public WebApplicationFactory<TestStartup> Host { get; }
    public FakeTenantClientCacheService FakeService { get; }   // captures calls + injects responses
    public TestApiKeys Keys { get; }                            // pairs of (tenantKey, plaintext, hashHex)
    public CapturingLogger Logs { get; }
    public RecordingMeterListener Metrics { get; }              // captures counter + histogram emissions
    public TimeProvider Clock { get; }                          // controllable for rate-limit tests
}
```

- `FakeTenantClientCacheService` đăng ký thay `ITenantClientCacheService` (singleton) — controller chỉ phụ thuộc interface.
- `TestApiKeys` sinh deterministic `(plaintext, sha256Hex)` để feed vào `IOptionsMonitor` test snapshot.
- `RecordingMeterListener` listen Meter `"TenantClientCache"` AND Meter `"Skoruba.Duende.IdentityServer.TenantClientCache.Client"` (cho integration test).

### Property test configuration

- Mỗi property test annotate bằng comment XML:
  ```csharp
  // Feature: tenant-client-cache-public-read, Property 4: Tenant enumeration resistance
  ```
- Minimum 100 iterations / property. Hot-reload và rate-limit properties chạy ≥ 200 iterations vì input space lớn hơn.
- Generators tái sử dụng cho `PublicClientSnapshot`, `ClientCacheSnapshotEnvelope`, tenantKey/clientId fuzzers (whitespace, mixed case, long strings) đặt trong file `Generators.cs` chung.

### Property-based testing library choice

Tuân AGENTS.md "không thêm NuGet package mới" — feature này dùng tool đã có trong solution (xác nhận tại task implementation: nếu solution đã có FsCheck / Hedgehog → tái sử dụng; nếu chưa → fallback table-driven theory tests với matrix ≥ 50 distinct samples per property). Decision deferred to first task.

### Mandatory non-property scenarios (xUnit examples)

(a) Configuration empty → 401 every request (R1.7). (b) `Cache-Control: max-age=0` server side → SDK no-cache (R11.6 boundary). (c) `HEAD` method → 200 headers + empty body (R2.9). (d) `OPTIONS` preflight when `AllowedOrigins` empty → no `Access-Control-Allow-Origin` echoed (R5.4). (e) Hot-reload removing tenant key → next request 401 (R1.6). (f) Pipeline disabled (`TenantClientCache:Enabled=false`) → 503 + `Retry-After: 60` (R7.4). (g) Unhandled exception in service → 503 `snapshot_unavailable`, never 500 (R7.5, R7.8). (h) `User-Agent` header populated (R10.9). (i) SDK timeout via `HttpClient.Timeout` propagates as `TransientFailure` after retries exhausted (R11.12).
