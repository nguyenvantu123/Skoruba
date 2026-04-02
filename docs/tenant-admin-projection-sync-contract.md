# Tenant Admin Projection Sync Contract

## Goal

Keep `Skoruba` as the source of truth for `TenantAdmin` accounts while projecting those accounts into the tenant-domain user store immediately after central changes.

This sync applies only to accounts that currently hold the tenant-admin role:

- `SkorubaIdentityTenantAdministrator`

## Trigger Points From Skoruba

Central Skoruba now emits sync attempts on:

- tenant admin assignment
- tenant admin unassignment
- central user update, but only if the user is still a tenant admin
- central user delete, but only if the user was a tenant admin

## Suggested Tenant User API Endpoint

- URL: `POST /api/internal/tenant-admins/projection`
- Authentication: internal service-to-service only
- Suggested header: `X-Internal-Api-Key: <shared-secret>`

## Request Body

```json
{
  "tenantKey": "tenant1",
  "externalIdentityId": "3d0cc9c0-2fe3-4fd0-a49b-7f6a9ef8c001",
  "userName": "tenantadmin1",
  "email": "tenantadmin1@example.com",
  "phoneNumber": "0123456789",
  "branchCode": "tenant1",
  "roles": ["SkorubaIdentityTenantAdministrator"],
  "accountType": "TenantAdmin",
  "authSource": "CentralIdentity",
  "isActive": true,
  "operation": "Upsert"
}
```

## Operation Semantics

- `Upsert`
  - create the tenant-domain user row if missing
  - otherwise update profile fields
  - ensure each projected role exists locally, then assign it to the projected user
- `Deactivate`
  - keep the tenant-domain row but mark it inactive or remove projected tenant-admin roles locally
- `Delete`
  - tenant user API may soft-delete instead of hard-delete if there are references or audit links

## Tenant DB Shape

Recommended tenant user record values for central tenant admins:

- `account_type = TenantAdmin`
- `auth_source = CentralIdentity`
- `external_identity_id = <Skoruba user id>`
- `password_hash = null`
- role membership includes the projected tenant-admin role from Skoruba

## Required Tenant User API Behavior

The tenant user API should add these rules:

- upsert and lookup by `(tenant_key, external_identity_id, account_type = TenantAdmin)`
- when handling `Upsert`, create missing roles locally and assign the projected roles from `request.roles`
- do not allow `/connect/token` local password login for `auth_source = CentralIdentity`
- use the projected row for:
  - `/api/me`
  - settings
  - org chart
  - audit ownership

## Why Password Hash Should Not Be Synced

Password ownership stays in the system that authenticates that account.

For projected tenant-admin rows:

- central Skoruba / STS owns sign-in
- tenant user API owns tenant-domain profile and business participation
- tenant DB should not become a second password source for the same tenant admin
