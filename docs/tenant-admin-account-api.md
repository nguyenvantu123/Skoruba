# Tenant Admin Account API

These central STS endpoints are intended for a tenant-admin webapp session.

Base URL:

- `http://localhost:5010`

Authorization:

- bearer token issued by STS
- role `SkorubaIdentityTenantAdministrator`
- claim `tenant_key`

## Endpoints

### `GET /api/tenant-admin/account/me`
Returns the current tenant-admin account summary.

```json
{
  "id": "127f2720-fff4-4309-b63f-ad27983dd714",
  "userName": "tenantadmin",
  "tenantId": "tenant1",
  "email": "tenantadmin@example.com",
  "firstName": "Tenant",
  "lastName": "Admin",
  "phoneNumber": "0123456789",
  "roles": ["SkorubaIdentityTenantAdministrator"],
  "hasPassword": true,
  "accountType": "TenantAdmin",
  "authSource": "CentralIdentity",
  "externalIdentityId": "127f2720-fff4-4309-b63f-ad27983dd714"
}
```

### `GET /api/tenant-admin/account/personal-data`
Returns current tenant-admin personal data and OpenID profile claims.

### `PUT /api/tenant-admin/account/password`
```json
{
  "currentPassword": "OldPassword123!",
  "newPassword": "NewPassword123!"
}
```

### `GET /api/tenant-admin/account/two-factor`
```json
{
  "hasAuthenticator": true,
  "isTwoFactorEnabled": true,
  "recoveryCodesLeft": 8
}
```

### `POST /api/tenant-admin/account/two-factor/setup`
Returns the shared key and authenticator URI for QR-code generation.

### `POST /api/tenant-admin/account/two-factor/enable`
```json
{
  "code": "123456"
}
```
Success may include newly generated recovery codes.

### `POST /api/tenant-admin/account/two-factor/disable`
Disables two-factor authentication.

### `POST /api/tenant-admin/account/two-factor/reset-authenticator`
Disables 2FA, resets the authenticator key, and returns a new setup payload.

### `POST /api/tenant-admin/account/two-factor/recovery-codes`
Generates 10 new recovery codes when 2FA is enabled.

## Sample client for webapp
Reference:
- [TenantAdminAccountClient.cs](/E:/Duende.IdentityServer.Admin/docs/TenantAdminAccountClient.cs)

### appsettings.json
```json
{
  "TenantAdminAccountApi": {
    "StsBaseUrl": "http://localhost:5010/"
  }
}
```

### Program.cs
```csharp
builder.Services.AddTenantAdminAccountClient(builder.Configuration);
```

### Usage example
```csharp
var me = await _tenantAdminAccountClient.GetMeAsync(cancellationToken);
var personalData = await _tenantAdminAccountClient.GetPersonalDataAsync(cancellationToken);
var twoFactor = await _tenantAdminAccountClient.GetTwoFactorStatusAsync(cancellationToken);
var setup = await _tenantAdminAccountClient.GetTwoFactorSetupAsync(cancellationToken);
var enableResult = await _tenantAdminAccountClient.EnableTwoFactorAsync("123456", cancellationToken);
var changePassword = await _tenantAdminAccountClient.ChangePasswordAsync("OldPassword123!", "NewPassword123!", cancellationToken);
```
