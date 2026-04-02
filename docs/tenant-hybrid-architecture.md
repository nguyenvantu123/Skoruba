# Tenant Hybrid Identity And Domain Architecture

## 1. Context

The system uses a hybrid identity model:

- `tenant admin` accounts authenticate in central `IdentityServerAdmin` / `STS.Identity`
- `tenant user` accounts authenticate in each tenant database, for example `tenant1.user`
- tenant webapp currently calls APIs directly
- `lastPageVisit` and `isDarkMode` must be persisted in tenant DB
- organization chart must be modeled in tenant DB

This document defines the target architecture so that authentication, profile data, settings, and tenant domain features do not cross the wrong boundary.

## 2. Core Principles

- Central identity is responsible only for central-authenticated accounts and token issuance.
- Tenant domain data belongs to tenant databases.
- Redis is a cache, not the long-term source of truth for tenant user settings.
- Every tenant-facing cache key and lookup must include `tenant_key`.
- The client must never choose an arbitrary tenant for read or write operations.
- Password ownership must stay with the system that authenticates that account.

## 3. Identity Ownership

### 3.1 Tenant Admin

Tenant admin is a central account:

- authenticates through `STS.Identity`
- receives token claims such as `sub`, `role`, and `tenant_key`
- changes password through central identity endpoints only

Tenant admin must also exist in tenant domain data using `tenant_user` with a dedicated account type.

Recommended shape:

- `tenant_user.account_type = TenantAdmin`
- `tenant_user.external_identity_id = central user id / STS subject`
- `tenant_user.username` may mirror the central username for display/search
- `tenant_user` remains the tenant-domain profile record used by org chart, settings, audit ownership, and tenant-scoped business logic

This avoids duplicating password hashes in tenant DB for tenant admins while still allowing tenant admins to participate in tenant domain features.

### 3.2 Tenant User

Tenant user is a tenant-local auth account:

- authenticates against `tenantX.user`
- changes password through tenant auth API only
- owns its profile, settings, and org-chart assignments entirely in tenant DB

`tenant_user.account_type = TenantUser`

## 4. System Boundaries

### 4.1 Central STS / IdentityServerAdmin

Responsibilities:

- sign-in for tenant admins
- token issuance
- central roles and claims
- password change for tenant admins
- central user administration
- tenant lookup / central tenant metadata when needed

Non-responsibilities:

- tenant user password management
- tenant user settings persistence
- org chart persistence
- tenant user profile editing

### 4.2 Tenant API / BFF

This is the required target backend for tenant webapp.

Responsibilities:

- tenant user login / refresh / logout
- loading current tenant user profile
- loading and saving user settings
- org chart queries and updates
- tenant user administration
- linking central tenant admin principals to tenant-domain `tenant_user`

The tenant API resolves tenant context from trusted runtime context, token claims, or inbound tenant routing. It must not trust arbitrary tenant identifiers from the browser for sensitive operations.

### 4.3 Tenant WebApp

Responsibilities:

- present tenant UI
- call tenant API or BFF only
- keep lightweight local UI state
- optionally cache small non-authoritative values in browser storage for responsiveness

Non-responsibilities:

- direct password logic
- direct database access
- resolving tenant connection strings
- mixing central-admin APIs with tenant-user APIs in the same feature flow

## 5. Target Tenant Domain Data Model

## 5.1 `tenant_user`

Purpose:

- canonical tenant-domain actor record for both tenant admins and tenant users

Recommended fields:

- `id`
- `tenant_key`
- `account_type` (`TenantAdmin`, `TenantUser`)
- `auth_source` (`CentralIdentity`, `TenantLocal`)
- `external_identity_id` nullable for central-linked admin accounts
- `username`
- `normalized_username`
- `email`
- `display_name`
- `phone_number`
- `password_hash` nullable for central-linked admin accounts
- `security_stamp` if tenant-local auth stack uses it
- `is_active`
- `department_id` nullable
- `manager_user_id` nullable
- `created_at_utc`
- `updated_at_utc`

Rules:

- `TenantAdmin` rows use `auth_source = CentralIdentity`
- `TenantAdmin.password_hash` should remain null
- `TenantUser` rows use `auth_source = TenantLocal`
- `TenantUser.external_identity_id` is null unless future federation is introduced

## 5.2 `tenant_user_settings`

Purpose:

- durable per-user settings

Recommended fields:

- `tenant_user_id`
- `is_dark_mode`
- `last_page_visit`
- `updated_at_utc`
- `updated_by_tenant_user_id` nullable

One row per tenant user.

## 5.3 Org Chart

### `org_unit`

- `id`
- `tenant_key`
- `code`
- `name`
- `parent_id`
- `sort_order`
- `is_active`

### `position`

- `id`
- `tenant_key`
- `code`
- `name`
- `org_unit_id`
- `is_active`

### `user_assignment`

- `id`
- `tenant_key`
- `tenant_user_id`
- `org_unit_id`
- `position_id`
- `manager_user_id`
- `is_primary`
- `effective_from_utc`
- `effective_to_utc`

This model supports hierarchical org structures, user placement, and manager relationships.

## 6. Password Ownership Rules

### 6.1 Tenant Admin Password

Password source of truth:

- central `IdentityServerAdmin` / `STS.Identity`

Flow:

- tenant admin uses central sign-in
- change password calls central password endpoint
- tenant DB is not updated with password data

### 6.2 Tenant User Password

Password source of truth:

- tenant DB auth tables

Flow:

- tenant user signs in through tenant auth API
- change password verifies old password in tenant auth store
- writes new password hash to tenant DB

Never share one password lifecycle across both auth sources.

## 7. User Settings Ownership

Source of truth:

- tenant DB table `tenant_user_settings`

Cache:

- Redis or distributed cache

Cache key pattern:

- `tenant:{tenantKey}:user:{tenantUserId}:settings`

Read flow:

1. resolve current tenant and current tenant user
2. read Redis cache
3. if cache hit, return cached DTO
4. if cache miss, read tenant DB
5. if row does not exist, create default row
6. write-through Redis
7. return result

Write flow:

1. validate caller belongs to resolved tenant
2. update `tenant_user_settings`
3. commit tenant DB
4. overwrite Redis cache entry

Do not store mutable settings like `isDarkMode` or `lastPageVisit` in JWT claims.

## 8. Tenant Admin Mapping Into Tenant Domain

Tenant admin must exist as `tenant_user` with a distinct account type.

Recommended provisioning model:

1. central tenant admin account is created in `IdentityServerAdmin`
2. first time the tenant admin enters tenant domain, tenant API checks for `tenant_user` by:
   - `account_type = TenantAdmin`
   - `external_identity_id = token.sub`
   - `tenant_key = token.tenant_key`
3. if not found, tenant API auto-provisions a `tenant_user` row
4. settings and org-chart participation now operate through that `tenant_user` record

This gives every actor in tenant space a tenant-domain identity without collapsing auth sources.

## 9. Recommended API Surface

## 9.1 Central APIs

Examples:

- `GET /api/account/me`
- `PUT /api/account/password`
- `GET /api/tenant-admin/tenant-registry`

These remain central concerns only.

## 9.2 Tenant APIs

Authentication:

- tenant-user auth endpoints for `TenantLocal`
- tenant-admin bearer validation for `CentralIdentity`

Recommended endpoints:

- `POST /api/auth/login`
- `POST /api/auth/refresh`
- `POST /api/auth/logout`
- `GET /api/me`
- `PUT /api/me`
- `PUT /api/me/password`
- `GET /api/me/settings`
- `PUT /api/me/settings`
- `GET /api/org-chart`
- `GET /api/org-units`
- `POST /api/org-units`
- `GET /api/positions`
- `POST /api/positions`
- `GET /api/users`
- `POST /api/users`
- `PUT /api/users/{id}`
- `PUT /api/users/{id}/status`

Behavior:

- `GET /api/me` returns tenant-domain profile from `tenant_user`
- `GET /api/me/settings` returns durable settings from `tenant_user_settings`
- `PUT /api/me/password` applies only when `auth_source = TenantLocal`
- tenant admin password changes do not go through tenant API

## 10. Security And Tenant Isolation

Required safeguards:

- every tenant DB query must be scoped to resolved tenant
- every Redis cache key must include tenant identity
- tenant admin tokens must carry `tenant_key`
- central-identity tenant admins must not be able to project into another tenant
- no API should allow a browser to submit an arbitrary tenant key for privileged writes

Least-privilege rules:

- if a tenant admin calls tenant API, derive tenant from trusted token claims and route context
- if a tenant user calls tenant API, derive user identity from the authenticated tenant-local session/token

## 11. Migration Strategy

### Phase 1. Foundation

- introduce the target schema in tenant DB
- add `tenant_user.account_type`
- add `tenant_user.auth_source`
- add `tenant_user.external_identity_id`
- add `tenant_user_settings`
- add org chart tables

### Phase 2. Tenant Admin Linking

- create provisioning logic to ensure every central tenant admin has a tenant-domain `tenant_user` row
- backfill existing tenant admins where needed

### Phase 3. Settings

- move `isDarkMode` and `lastPageVisit` to tenant DB
- put Redis cache in front
- deprecate claim-based or admin-side preference storage for tenant webapp

### Phase 4. Tenant Domain APIs

- deliver `/api/me`, `/api/me/settings`, `/api/me/password`, and org-chart endpoints
- keep tenant-admin and tenant-user authorization paths separate where needed

### Phase 5. WebApp Cutover

- switch tenant webapp from direct scattered API calls to tenant API/BFF calls
- retire old preference flows

## 12. Testing Checklist

Authentication and authorization:

- tenant admin can sign in centrally and reach tenant API only for its own tenant
- tenant user can sign in locally and reach tenant API only for its own tenant
- tenant admin cannot use tenant password-change endpoint
- tenant user cannot use central password-change endpoint

Settings:

- cache miss reads tenant DB and warms Redis
- cache hit returns without DB read
- update writes DB and refreshes Redis
- settings never bleed across tenant keys

Org chart:

- same user can be resolved to department, position, and manager
- recursive org unit tree is tenant-scoped

Provisioning:

- first central tenant-admin access creates tenant-domain `tenant_user` once
- repeated access does not duplicate rows

## 13. What This Means For The Current Repo

This repository can safely own:

- central identity behavior
- tenant metadata / tenant registry
- reference implementation docs and contracts

This repository should not become the permanent home for tenant-user auth, tenant settings persistence, or org-chart persistence if those belong to tenant DB and tenant webapp runtime.

The tenant API/BFF implementation should live in the tenant application codebase or a dedicated tenant backend codebase.
