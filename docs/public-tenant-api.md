# Public Tenant API

This endpoint exposes a minimal public tenant directory for pre-login search.

Base URL:

- `https://localhost:7397`

Authentication:

- not required

## Endpoint

### `GET /api/tenants/public?search={term}`

Rules:

- `search` is required
- `search` must be at least `PublicTenantDirectory:SearchMinLength`
- `search` must be at most `PublicTenantDirectory:SearchMaxLength`
- the response returns active tenants only
- the response returns tenant names only

Example:

```http
GET /api/tenants/public?search=branch HTTP/1.1
Host: localhost:7397
Accept: application/json
```

Response body:

```json
[
  {
    "displayName": "Branch A"
  },
  {
    "displayName": "Branch B"
  }
]
```

## Security Notes

- This is intentionally `search-only`; it does not return the full tenant list when `search` is missing.
- This endpoint does not return `tenantKey`, `redirectUrl`, `logoUrl`, secrets, or internal IDs.
- Rate limiting and cache duration are configured from `PublicTenantDirectory` in app settings.
- Web and mobile clients should debounce user input and call this endpoint only after the minimum search length is met.

## Config

Example `appsettings.json` section:

```json
{
  "PublicTenantDirectory": {
    "ResponseCacheSeconds": 300,
    "SearchMinLength": 2,
    "SearchMaxLength": 100,
    "RateLimitPermitLimit": 30,
    "RateLimitWindowSeconds": 60,
    "RateLimitQueueLimit": 0
  }
}
```

## Generated TypeScript Client

The generated TypeScript client now exposes:

- `client.TenantsClient.getPublicTenants(search: string)`

Reference:

- [TenantPublicDirectoryWebSample.ts](/E:/Duende.IdentityServer.Admin/docs/TenantPublicDirectoryWebSample.ts)

## Mobile Sample

Reference:

- [TenantPublicDirectoryMobileSample.dart](/E:/Duende.IdentityServer.Admin/docs/TenantPublicDirectoryMobileSample.dart)
