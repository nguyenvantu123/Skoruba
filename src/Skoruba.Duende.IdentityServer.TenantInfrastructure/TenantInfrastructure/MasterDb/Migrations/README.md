# TenantInfrastructure MasterDb Migrations

The repository uses `20260302104419_DbInit` as the initial migration and `20260302120655_UpdateTenant` to move tenant connection secret storage from a single `ConnectionStringSecretName` value to the JSON `ConnectionSecrets` column.

## Connection string

Set the design-time connection string before running migration commands:

```powershell
$env:ConnectionStrings__MasterDb="Server=...;Database=...;Uid=...;Pwd=...;"
```

## Update notes

- `20260302120655_UpdateTenant` copies existing values into `ConnectionSecrets` using the default service key `BlazorApiUser`.
- If your deployment needs a different default service key, adjust the SQL in that migration before applying it.
