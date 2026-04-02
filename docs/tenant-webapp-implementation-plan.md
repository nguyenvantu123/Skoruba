# Tenant WebApp Implementation Plan

## 1. Goal

Move tenant webapp to a clean tenant-domain API model where:

- tenant admins use central identity for authentication
- tenant users use tenant-local authentication
- both are represented in tenant DB as `tenant_user`
- settings and org chart live in tenant DB
- webapp stops scattering direct calls across unrelated APIs

## 2. Assumptions

- tenant webapp currently calls APIs directly
- tenant DB already contains the tenant-local auth table for tenant users
- a tenant backend or BFF can be introduced or extended

## 3. Backend Work For Tenant Team

### 3.1 Authentication Model

Support two caller types:

- `TenantUser`
  - authenticated by tenant-local auth
- `TenantAdmin`
  - authenticated by central STS bearer token

Normalize both into one tenant-domain principal:

- `tenant_user_id`
- `tenant_key`
- `account_type`
- `auth_source`

### 3.2 Required Schema

Implement these tables or their equivalent:

- `tenant_user`
- `tenant_user_settings`
- `org_unit`
- `position`
- `user_assignment`

Add indexes:

- `tenant_user(tenant_key, normalized_username)`
- `tenant_user(tenant_key, external_identity_id)`
- `tenant_user_settings(tenant_user_id)`
- `org_unit(tenant_key, parent_id)`
- `user_assignment(tenant_key, tenant_user_id, is_primary)`

### 3.3 Required APIs

#### Auth

- `POST /api/auth/login`
- `POST /api/auth/refresh`
- `POST /api/auth/logout`

#### Current user

- `GET /api/me`
- `PUT /api/me`
- `PUT /api/me/password`

#### User settings

- `GET /api/me/settings`
- `PUT /api/me/settings`

Request/response:

```json
{
  "isDarkMode": true,
  "lastPageVisit": "/dashboard"
}
```

#### Org chart

- `GET /api/org-chart`
- `GET /api/org-units`
- `POST /api/org-units`
- `GET /api/positions`
- `POST /api/positions`

#### User management

- `GET /api/users`
- `POST /api/users`
- `PUT /api/users/{id}`
- `PUT /api/users/{id}/status`

### 3.4 Tenant Admin Provisioning

When tenant admin calls tenant API with central STS token:

1. validate token
2. read `sub` and `tenant_key`
3. find `tenant_user` by `(tenant_key, external_identity_id, account_type = TenantAdmin)`
4. if not found, auto-create `tenant_user`
5. continue request using the resulting `tenant_user_id`

### 3.5 Settings Cache

Use Redis with DB as source of truth.

Read path:

1. build key `tenant:{tenantKey}:user:{tenantUserId}:settings`
2. read Redis
3. on miss, read DB
4. if no row, create default
5. write cache

Write path:

1. update DB
2. write cache

### 3.6 Password Rules

`PUT /api/me/password`:

- allowed only for `TenantLocal` accounts
- must verify current password
- must rotate password hash and security stamp as needed

If `account_type = TenantAdmin` and `auth_source = CentralIdentity`:

- reject with explicit error such as `PasswordManagedByCentralIdentity`

## 4. WebApp Work For Frontend Team

### 4.1 Session Handling

Detect caller mode:

- tenant admin session from central STS
- tenant user session from tenant-local auth

Webapp should not decide business tenant freely. Tenant context must come from trusted runtime configuration and authenticated identity.

### 4.2 App Bootstrap Flow

On app load:

1. load authenticated session
2. call `GET /api/me`
3. call `GET /api/me/settings`
4. initialize UI theme from `isDarkMode`
5. initialize route tracking for `lastPageVisit`

### 4.3 Theme Handling

On initial load:

- prefer API settings value
- optionally keep a short-lived local mirror for instant paint

On theme toggle:

1. update UI immediately
2. call `PUT /api/me/settings`
3. persist `isDarkMode`

### 4.4 Last Page Visit

On meaningful route change:

1. debounce updates
2. call `PUT /api/me/settings`
3. persist `lastPageVisit`

Do not write on every noisy internal navigation event.

### 4.5 Error Handling

If `GET /api/me/settings` fails:

- keep usable defaults
- do not break app shell

If `PUT /api/me/settings` fails:

- keep optimistic UI if acceptable
- retry later or surface non-blocking warning

If password change returns `PasswordManagedByCentralIdentity`:

- show message telling tenant admin to change password from central identity profile flow

## 5. Suggested WebApp Client Contracts

### `GET /api/me`

```json
{
  "tenantUserId": "42",
  "tenantKey": "tenant1",
  "accountType": "TenantAdmin",
  "authSource": "CentralIdentity",
  "username": "admin1",
  "displayName": "Tenant Admin",
  "email": "admin1@example.com",
  "isActive": true
}
```

### `GET /api/me/settings`

```json
{
  "isDarkMode": true,
  "lastPageVisit": "/dashboard"
}
```

### `PUT /api/me/password` success

```json
{
  "success": true
}
```

### `PUT /api/me/password` central-admin rejection

```json
{
  "code": "PasswordManagedByCentralIdentity",
  "message": "This account password is managed by central identity."
}
```

## 6. Rollout Plan

### Phase 1

- add tenant DB schema
- add tenant admin auto-provisioning into `tenant_user`

### Phase 2

- implement `/api/me` and `/api/me/settings`
- integrate Redis cache

### Phase 3

- switch dark mode and last page visit in webapp to use tenant API

### Phase 4

- implement `/api/me/password`
- add clear UX split between tenant user and tenant admin password ownership

### Phase 5

- implement org-chart APIs and screens
- implement tenant user management APIs and screens

## 7. Acceptance Criteria

- tenant user settings survive logout/login and cache expiration
- tenant admins appear as tenant-domain users with `account_type = TenantAdmin`
- tenant admins cannot change password through tenant-local password endpoint
- tenant users can change password through tenant-local password endpoint
- no tenant user or admin can read or update data across tenants
- org chart data is fully tenant-scoped

## 8. Suggested Team Split

Central/Skoruba team:

- central tenant admin auth
- tenant registry and tenant metadata
- token claims such as `tenant_key`
- integration guidance

Tenant backend team:

- tenant-domain schema
- tenant-local auth
- tenant settings persistence
- org chart APIs
- tenant admin auto-provisioning into `tenant_user`

Tenant webapp team:

- consume `/api/me`, `/api/me/settings`, `/api/me/password`
- wire theme and last-page persistence
- build org-chart UI against tenant APIs
