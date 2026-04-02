# Blazor API User Tenant Registry Client Sample

This sample shows how a tenant application such as Blazor API User can call the dedicated tenant-facing registry endpoint hosted by `STS.Identity`.

This flow is separate from `Skoruba Admin.Api`. The tenant app forwards the tenant admin bearer token it already received to the STS endpoint:

- `GET http://localhost:5010/api/tenant-admin/tenant-registry?serviceName=BlazorApiUser`

Reference:

- [tenant-admin-tenant-registry-api.md](/E:/Duende.IdentityServer.Admin/docs/tenant-admin-tenant-registry-api.md)
- [TenantRegistryClient.cs](/E:/Duende.IdentityServer.Admin/docs/TenantRegistryClient.cs)

## Why this flow

- No dependency on `skoruba_identity_admin_api`
- No dependency on `Skoruba Admin.Api`
- Tenant isolation is enforced by the `tenant_key` claim in the token
- The caller cannot request another tenant's registry entry

## Expected token

The caller should send a bearer token issued by `http://localhost:5010` that contains:

- `role = SkorubaIdentityTenantAdministrator`
- `tenant_key = tenant1`

## Expected response

```json
{
  "tenantId": "1",
  "identifier": "tenant1",
  "name": "Tenant 1",
  "secretName": "db/tenants/tenant1/user-api",
  "connectionSecrets": {
    "BlazorApiUser": "db/tenants/tenant1/user-api"
  },
  "isActive": true
}
```

## Usage example

```csharp
private const string UserApiServiceName = "BlazorApiUser";

var tenant = await _tenantRegistryClient.GetCurrentTenantAsync(UserApiServiceName, cancellationToken)
    ?? throw new InvalidOperationException("The current tenant was not found.");

var secretName = tenant.SecretName;
```
