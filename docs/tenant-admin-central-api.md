# Tenant Admin Central API

Central Skoruba / STS now exposes only tenant-admin focused APIs for tenant-facing integration.

## Base URL

- `http://localhost:5010`

## Authorization

All endpoints require a tenant-admin bearer token issued by `STS.Identity`.

Required token characteristics:

- valid bearer token
- role `SkorubaIdentityTenantAdministrator`
- claim `tenant_key`

## Endpoints

### `GET /api/tenant-admin/account/me`

Returns the current central tenant-admin account resolved from the token.

Example response:

```json
{
  "userId": "127f2720-ffff-4309-b63f-ad27983dd714",
  "tenantKey": "tenant1",
  "userName": "tenant-admin-1",
  "displayName": "tenant-admin-1",
  "email": "admin1@example.com",
  "isActive": true,
  "roles": [
    "SkorubaIdentityTenantAdministrator"
  ]
}
```

### `PUT /api/tenant-admin/account/password`

Changes password for the current central tenant-admin account.

Request:

```json
{
  "currentPassword": "OldPassword123!",
  "newPassword": "NewPassword123!"
}
```

Success response:

```json
{
  "success": true,
  "errors": []
}
```

Failure response:

- HTTP `400`
- validation payload with password policy or current-password errors

### `GET /api/tenant-admin/tenant-registry?serviceName=BlazorApiUser`

Returns central tenant registry metadata for the tenant identified by the token `tenant_key`.

This endpoint remains the correct way for tenant-admin flows to resolve tenant service secret names from Skoruba.

## What Was Removed

The old central endpoint:

- `GET /api/tenant/user-settings`
- `PUT /api/tenant/user-settings`

has been removed from `STS.Identity`.

Reason:

- tenant user settings such as `isDarkMode` and `lastPageVisit` belong to tenant DB, not central identity
- keeping them in STS would violate the agreed tenant-domain boundary

## Integration Rule

Use Skoruba central APIs only for:

- tenant-admin identity
- tenant-admin password
- tenant registry metadata

Do not use Skoruba central APIs as the permanent store for:

- tenant user settings
- tenant user profile
- tenant org chart
- tenant-local password management
