# TenantInfrastructure MasterDb Migrations

The repository uses `20260302104419_DbInit` as the initial migration and `20260302120655_UpdateTenant` to move tenant connection secret storage from a single `ConnectionStringSecretName` value to the JSON `ConnectionSecrets` column.

Tenant registry tables now live inside the **`IdentityServerAdmin`** database alongside `AdminIdentityDbContext`, `IdentityServerConfigurationDbContext`, `IdentityServerPersistedGrantDbContext`, and `IdentityServerDataProtectionDbContext`. There is no separate `idsrv_master` database anymore.

## Connection string

The design-time factory (`MasterDbContextFactory`) reads the connection string in this order (highest priority first):

1. Command-line argument `--connection=<value>` passed to `dotnet ef`.
2. Environment variable `ConnectionStrings__IdentityDbConnection`.

If both are missing or whitespace, the factory throws `InvalidOperationException` and migration commands abort. The legacy variable `ConnectionStrings__MasterDb` is no longer read.

```powershell
# Option A: environment variable
$env:ConnectionStrings__IdentityDbConnection="Server=...;Database=IdentityServerAdmin;Uid=...;Pwd=...;"

dotnet ef migrations add <Name> `
    --project src/Skoruba.Duende.IdentityServer.TenantInfrastructure/TenantInfrastructure/TenantInfrastructure.csproj `
    --context MasterDbContext

dotnet ef database update `
    --project src/Skoruba.Duende.IdentityServer.TenantInfrastructure/TenantInfrastructure/TenantInfrastructure.csproj `
    --context MasterDbContext
```

```powershell
# Option B: explicit --connection argument (overrides the env var)
dotnet ef database update `
    --project src/Skoruba.Duende.IdentityServer.TenantInfrastructure/TenantInfrastructure/TenantInfrastructure.csproj `
    --context MasterDbContext `
    -- --connection="Server=...;Database=IdentityServerAdmin;Uid=...;Pwd=...;"
```

## Database provider selection

Set the env var `DatabaseProviderConfiguration__ProviderType` to choose the EF Core provider used by the design-time factory. Valid values:

- `SqlServer`
- `PostgreSQL`
- `MySql`

When the variable is not set, the factory defaults to `MySql` to preserve the existing workspace behaviour. Any other value triggers `InvalidOperationException` listing the supported providers.

```powershell
$env:DatabaseProviderConfiguration__ProviderType="MySql"
```

## Migrations history table

`MasterDbContext` is configured to use a dedicated migrations history table named **`__EFMigrationsHistory_TenantRegistry`**. This is intentional: the `IdentityServerAdmin` database is shared with `AdminIdentityDbContext` (and the IdentityServer Configuration / PersistedGrant / DataProtection contexts), all of which use the default `__EFMigrationsHistory` table. Keeping the tenant registry history in a separate table lets each context apply and track its migrations independently inside the same physical database without overwriting each other's history rows.

When you `dotnet ef migrations add` against `MasterDbContext`, EF only inspects `__EFMigrationsHistory_TenantRegistry`, so it will not interpret unrelated `AdminIdentityDbContext` migration entries as already-applied tenant migrations.

## Provider coverage

This PR ships **MySQL migrations only**. There are no migration assemblies generated yet for SqlServer or PostgreSQL.

Until those assemblies land in a follow-up PR, operators running the SqlServer or PostgreSQL provider should either:

- Set `TenantInfrastructure:ApplyMasterDbMigrations=false` (the default) so startup calls `EnsureCreatedAsync` against `IdentityServerAdmin` and creates the `tenants` table from the model, or
- Apply the schema manually (e.g. translate the MySQL migration to provider-specific DDL during a maintenance window).

The follow-up PR will generate proper SqlServer / PostgreSQL migration assemblies and wire them via `MigrationsAssembly(...)` once those providers are actually deployed.

## One-time data copy from `idsrv_master`

When upgrading from a deployment that previously used a separate `idsrv_master` database, operators must copy existing tenant rows from `idsrv_master.tenants` into `IdentityServerAdmin.tenants`. This is a manual maintenance step and is **not** performed at startup.

See the section **"One-time data copy SQL snippets"** in `.kiro/specs/consolidate-master-into-identity-db/design.md` for the actual SQL and operator runbook.

## Update notes

- `20260302120655_UpdateTenant` copies existing values into `ConnectionSecrets` using the default service key `BlazorApiUser`.
- If your deployment needs a different default service key, adjust the SQL in that migration before applying it.
