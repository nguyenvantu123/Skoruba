# Tenant Admin Tenant Registry API

This is the dedicated tenant-facing endpoint hosted by `STS.Identity`. It is separate from `Skoruba Admin.Api` and is intended for tenant admin callers that already hold a tenant admin access token issued by the STS.

## Endpoint

- Method: `GET`
- URL: `http://localhost:5010/api/tenant-admin/tenant-registry?serviceName=BlazorApiUser`
- Authentication: bearer token required
- Required claims:
  - `role = SkorubaIdentityTenantAdministrator`
  - `tenant_key = <current-tenant>`

The endpoint does not accept a tenant identifier from the caller. It always resolves the tenant from the `tenant_key` claim in the token, which prevents cross-tenant reads.

## Response

```json
{
  "tenantId": "1",
  "identifier": "tenant1",
  "name": "Tenant 1",
  "secretName": "db/tenants/tenant1/user-api",
  "connectionSecrets": {
    "BlazorApiUser": "db/tenants/tenant1/user-api",
    "BlazorWebApiFiles": "db/tenants/tenant1/file-api"
  },
  "isActive": true
}
```

## Notes

- `serviceName` is optional. If omitted, `secretName` falls back to the default service key on the server.
- If the requested service key does not exist, `secretName` is returned as an empty string.
- This endpoint is intended for tenant-side integrations and does not depend on the `skoruba_identity_admin_api` scope.
