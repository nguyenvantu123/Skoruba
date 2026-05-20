# Requirements Document

## Introduction

Hiện tại `TenantInfrastructure` đang dùng một database vật lý riêng (`idsrv_master`) thông qua `MasterDbContext` và connection string `ConnectionStrings:MasterDb` để lưu danh sách tenants (bảng `tenants`). Yêu cầu của tính năng này là gộp toàn bộ dữ liệu master/tenant registry vào chung database `IdentityServerAdmin` (database mà các service STS và Admin.Api đã sử dụng cho `AdminIdentityDbContext` thông qua connection string `ConnectionStrings:IdentityDbConnection`), và mọi truy cập tenant registry từ runtime, design-time và background service đều phải đọc từ `IdentityDbConnection` thay vì `MasterDb`.

Tính năng này thuần về phối lại hạ tầng dữ liệu (data infrastructure refactor): không thay đổi domain model nghiệp vụ của tenant, không thay đổi luồng auth/token, không thêm tính năng mới cho người dùng. Mục tiêu là giảm số database cần vận hành, đơn giản hóa cấu hình, và tận dụng provider/naming convention đã có sẵn của `IdentityServerAdmin`.

## Glossary

- **TenantInfrastructure**: Project `src/Skoruba.Duende.IdentityServer.TenantInfrastructure/TenantInfrastructure` chứa abstraction tenant registry, EF context và migrations cho tenant registry.
- **MasterDbContext**: `DbContext` hiện tại của TenantInfrastructure (`TenantInfrastructure.MasterDb.MasterDbContext`) trỏ tới database `idsrv_master`. Sau thay đổi sẽ trỏ tới database `IdentityServerAdmin` thông qua `IdentityDbConnection`.
- **TenantInfo**: Entity tenant (`TenantInfrastructure.MasterDb.TenantInfo`), ánh xạ vào bảng `tenants`.
- **TenantInfrastructureOptions**: Options class điều khiển cấu hình TenantInfrastructure (`MasterConnectionString`, `ApplyMasterDbMigrations`, `AllowMasterDbAutoMigration`, Redis, resolution).
- **IdentityDbConnection**: Connection string name (`ConnectionStrings:IdentityDbConnection`) của database `IdentityServerAdmin`, hiện đang được `AdminIdentityDbContext` sử dụng.
- **MasterDb_Connection_Key**: Tên connection string cũ `ConnectionStrings:MasterDb`. Sau thay đổi không còn được hệ thống đọc nữa.
- **IdentityServerAdmin_Database**: Database vật lý đích cho tenant registry sau khi gộp.
- **AdminIdentityDbContext**: `Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.DbContexts.AdminIdentityDbContext`, dùng `IdentityDbConnection`.
- **DatabaseProviderConfiguration**: Cấu hình `ProviderType` (SqlServer | PostgreSQL | MySql) đã có trong `appsettings.json` của STS và Admin.Api.
- **STS_Identity**: Project `Skoruba.Duende.IdentityServer.STS.Identity`.
- **Admin_Api**: Project `Skoruba.Duende.IdentityServer.Admin.Api`.
- **MasterDbContextFactory**: `IDesignTimeDbContextFactory<MasterDbContext>` dùng cho `dotnet ef` migrations.
- **MasterDb_Migrations**: Tập migrations EF Core hiện tại trong thư mục `TenantInfrastructure/MasterDb/Migrations` (DbInit, UpdateTenant, LowercaseTables).
- **TenantRegistryCacheRefreshService**: `BackgroundService` định kỳ đọc tenants từ DB và nạp cache.
- **EfTenantStore / EfTenantRepository**: Các implementation EF của `ITenantStore` và `ITenantRepository`, hiện inject `IDbContextFactory<MasterDbContext>`.
- **InitializeTenantMasterDbAsync**: Extension method gọi tại Startup để chạy migration / `EnsureCreated` cho tenant registry DB.

## Requirements

### Requirement 1: Bỏ database `idsrv_master` ở runtime

**User Story:** Là một operator, tôi muốn STS_Identity và Admin_Api không còn yêu cầu một database `idsrv_master` riêng, để tôi giảm số database phải vận hành và đồng bộ.

#### Acceptance Criteria

1. THE TenantInfrastructure SHALL persist all tenant registry data inside IdentityServerAdmin_Database.
2. THE TenantInfrastructure SHALL NOT open any database connection that targets the legacy `idsrv_master` database during normal runtime.
3. WHEN STS_Identity starts with valid configuration, THE STS_Identity SHALL boot successfully without requiring a value for `ConnectionStrings:MasterDb`.
4. WHEN Admin_Api starts with valid configuration, THE Admin_Api SHALL boot successfully without requiring a value for `ConnectionStrings:MasterDb`.
5. IF `ConnectionStrings:MasterDb` is present in configuration but `ConnectionStrings:IdentityDbConnection` is also present, THEN THE TenantInfrastructure SHALL ignore `ConnectionStrings:MasterDb` and use `ConnectionStrings:IdentityDbConnection`.

### Requirement 2: Tenant registry đọc/ghi qua `IdentityDbConnection`

**User Story:** Là developer, tôi muốn mọi truy cập tenant registry sử dụng cùng connection string `IdentityDbConnection` như `AdminIdentityDbContext`, để cấu hình kết nối được tập trung và nhất quán.

#### Acceptance Criteria

1. WHEN TenantInfrastructure resolves the connection string for MasterDbContext at runtime, THE TenantInfrastructure SHALL read the value from `ConnectionStrings:IdentityDbConnection`.
2. WHEN TenantInfrastructureOptions is configured by STS_Identity, THE STS_Identity SHALL bind the tenant registry connection string from `ConnectionStrings:IdentityDbConnection`.
3. WHEN TenantInfrastructureOptions is configured by Admin_Api, THE Admin_Api SHALL bind the tenant registry connection string from `ConnectionStrings:IdentityDbConnection`.
4. WHEN EfTenantStore, EfTenantRepository, or TenantRegistryCacheRefreshService opens a database connection, THE component SHALL connect to IdentityServerAdmin_Database using the resolved IdentityDbConnection value.
5. IF `ConnectionStrings:IdentityDbConnection` is missing or empty at startup, THEN THE TenantInfrastructure SHALL throw a startup exception that names the missing key `ConnectionStrings:IdentityDbConnection`.

### Requirement 3: Provider parity với `DatabaseProviderConfiguration`

**User Story:** Là operator deploy trên SQL Server, tôi muốn TenantInfrastructure dùng đúng provider mà `DatabaseProviderConfiguration` đã chọn, để tenant registry không bị ép sang MySQL.

#### Acceptance Criteria

1. WHEN `DatabaseProviderConfiguration:ProviderType` equals `SqlServer`, THE MasterDbContext SHALL be configured with the SQL Server EF Core provider.
2. WHEN `DatabaseProviderConfiguration:ProviderType` equals `PostgreSQL`, THE MasterDbContext SHALL be configured with the Npgsql EF Core provider.
3. WHEN `DatabaseProviderConfiguration:ProviderType` equals `MySql`, THE MasterDbContext SHALL be configured with the MySQL EF Core provider.
4. THE MasterDbContext SHALL apply the same naming convention helper that `AdminIdentityDbContext` uses for the same provider value.
5. WHERE the configured provider is MySql, THE MasterDbContext SHALL store the `ConnectionSecrets` column using the MySQL `json` column type.
6. WHERE the configured provider is SqlServer or PostgreSQL, THE MasterDbContext SHALL store the `ConnectionSecrets` column using a provider-appropriate text column type that supports JSON payloads.
7. IF `DatabaseProviderConfiguration:ProviderType` is missing or contains a value outside the supported set, THEN THE TenantInfrastructure SHALL throw a startup exception that lists the supported provider values.

### Requirement 4: Migration / data relocation cho tenant table

**User Story:** Là operator nâng cấp một môi trường đang chạy, tôi muốn dữ liệu trong bảng `tenants` của `idsrv_master` được chuyển sang `IdentityServerAdmin` mà không mất bản ghi nào, để các tenant hiện hữu vẫn đăng nhập được sau nâng cấp.

#### Acceptance Criteria

1. THE TenantInfrastructure SHALL provide an EF Core migration that creates the `tenants` table inside IdentityServerAdmin_Database when the table does not yet exist.
2. THE migration SHALL preserve the existing schema of TenantInfo: `Id`, `TenantKey` (unique, max length 64), `DisplayName` (max length 256), `IsActive`, `ConnectionSecrets` (JSON payload), `RedirectUrl` (max length 2048), `LogoUrl`, `CreatedUtc`.
3. THE migration SHALL place the `tenants` table in IdentityServerAdmin_Database without colliding with any existing table managed by `AdminIdentityDbContext`, `IdentityServerConfigurationDbContext`, `IdentityServerPersistedGrantDbContext`, `IdentityServerDataProtectionDbContext`, `AdminLogDbContext`, `AdminAuditLogDbContext`, or `AdminConfigurationDbContext`.
4. WHEN the `tenants` table already exists inside IdentityServerAdmin_Database with the expected schema, THE migration SHALL be idempotent and SHALL NOT recreate or truncate the table.
5. THE feature SHALL document a one-time data migration path that copies existing rows from `idsrv_master.tenants` into `IdentityServerAdmin.tenants` preserving primary key values and column values.
6. WHEN `TenantInfrastructure:ApplyMasterDbMigrations` is `true` and `TenantInfrastructure:AllowMasterDbAutoMigration` is `true`, THE InitializeTenantMasterDbAsync SHALL apply pending tenant migrations against IdentityServerAdmin_Database at host startup.
7. WHEN `TenantInfrastructure:ApplyMasterDbMigrations` is `true` and `TenantInfrastructure:AllowMasterDbAutoMigration` is `false`, THE InitializeTenantMasterDbAsync SHALL skip applying migrations and SHALL log that auto-migration is disabled.
8. WHEN `TenantInfrastructure:ApplyMasterDbMigrations` is `false`, THE InitializeTenantMasterDbAsync SHALL fall back to `EnsureCreatedAsync` against IdentityServerAdmin_Database.

### Requirement 5: Cấu hình `appsettings.json` và biến môi trường

**User Story:** Là DevOps, tôi muốn các file `appsettings.json` và docker-compose chỉ liệt kê những connection string còn được dùng, để cấu hình triển khai gọn và không gây nhầm lẫn.

#### Acceptance Criteria

1. THE STS_Identity `appsettings.json` SHALL NOT include `ConnectionStrings:MasterDb` after the change.
2. THE Admin_Api `appsettings.json` SHALL NOT include `ConnectionStrings:MasterDb` after the change.
3. THE STS_Identity `appsettings.json` SHALL retain `ConnectionStrings:IdentityDbConnection` and SHALL keep its current value as the source of truth for tenant registry access.
4. THE Admin_Api `appsettings.json` SHALL retain `ConnectionStrings:IdentityDbConnection` and SHALL keep its current value as the source of truth for tenant registry access.
5. THE workspace root `docker-compose.yml` SHALL NOT export `ConnectionStrings__MasterDb` for the STS_Identity, Admin_Api, or Admin services after the change.
6. THE workspace root `docker-compose.yml` SHALL export `ConnectionStrings__IdentityDbConnection` for the STS_Identity and Admin_Api services pointing at IdentityServerAdmin_Database.
7. THE `TenantInfrastructure` configuration section in `appsettings.json` SHALL retain the keys `ApplyMasterDbMigrations`, `AllowMasterDbAutoMigration`, and `RedisInstanceName` with their existing semantics so that operators do not need to rename keys during upgrade.
8. WHERE the project ships sample `appsettings.Development.json` files for STS_Identity or Admin_Api, THE sample files SHALL NOT introduce a `ConnectionStrings:MasterDb` value.

### Requirement 6: Design-time `IDesignTimeDbContextFactory` cho EF migrations

**User Story:** Là developer chạy `dotnet ef migrations`, tôi muốn factory design-time của tenant registry dùng `IdentityDbConnection`, để lệnh `dotnet ef` không yêu cầu một biến môi trường riêng cho `MasterDb`.

#### Acceptance Criteria

1. WHEN `dotnet ef migrations` is invoked against TenantInfrastructure, THE MasterDbContextFactory SHALL resolve the connection string from `ConnectionStrings__IdentityDbConnection` environment variable as the first source.
2. WHEN `dotnet ef migrations` is invoked with a `--connection=<value>` argument, THE MasterDbContextFactory SHALL prefer the explicit `--connection` argument over any environment variable.
3. WHEN neither `--connection=<value>` nor `ConnectionStrings__IdentityDbConnection` is provided, THE MasterDbContextFactory SHALL throw an `InvalidOperationException` whose message instructs the developer to set `ConnectionStrings__IdentityDbConnection` or pass `--connection=<value>`.
4. THE MasterDbContextFactory SHALL select its EF Core provider based on the value of `DatabaseProviderConfiguration__ProviderType` environment variable when present, defaulting to the same provider that `AdminIdentityDbContext` uses for the matching value.
5. THE MasterDbContextFactory SHALL NOT read `ConnectionStrings__MasterDb` after the change.

### Requirement 7: Bảo toàn hành vi runtime tenant resolution và auth

**User Story:** Là người dùng cuối, tôi muốn việc đăng nhập theo tenant sau khi nâng cấp vẫn hoạt động y như trước, để không có gián đoạn.

#### Acceptance Criteria

1. THE feature SHALL NOT alter the contract of `ITenantStore`, `ITenantRepository`, `ITenantRegistryCache`, or `ITenantContextAccessor`.
2. THE feature SHALL NOT change tenant resolution semantics implemented in `SubdomainTenantResolver` and `TenantResolutionMiddleware`.
3. THE feature SHALL NOT change the structure of the `tenants` table beyond what Requirement 4 prescribes.
4. THE feature SHALL NOT change token lifetimes, signing key configuration, or authentication scheme registration in STS_Identity.
5. WHEN STS_Identity resolves an identity database connection string for a tenant via `StsIdentityDbConnectionStringResolver`, THE resolver SHALL continue to read tenant connection secrets from the tenant registry without changing its public interface.
6. THE TenantRegistryCacheRefreshService SHALL continue to refresh the tenant cache on the same interval and SHALL log the same `Refreshed tenant registry cache for {TenantCount} tenant(s).` message at the same severity.
7. WHEN a runtime tenant lookup uses `EfTenantRepository.GetByKeyAsync`, THE repository SHALL keep its existing per-call timeout of 10 seconds.

### Requirement 8: Verification và acceptance tests

**User Story:** Là maintainer, tôi muốn build, unit tests và smoke tests xác nhận thay đổi này không phá hệ thống, để CI bảo vệ chất lượng.

#### Acceptance Criteria

1. WHEN `dotnet build` is executed against the solution, THE build SHALL succeed with zero new compile errors introduced by this feature.
2. WHEN `dotnet test` is executed against the solution, THE test run SHALL succeed with zero new failing tests introduced by this feature.
3. WHEN STS_Identity is started with a valid `ConnectionStrings:IdentityDbConnection` and a populated `tenants` table inside IdentityServerAdmin_Database, THE STS_Identity SHALL successfully resolve the tenant registry on the first incoming tenant-scoped request.
4. WHEN Admin_Api is started with a valid `ConnectionStrings:IdentityDbConnection` and a populated `tenants` table inside IdentityServerAdmin_Database, THE Admin_Api SHALL successfully serve the existing tenant-related endpoints under `/api/tenants` and `/api/tenants/public`.
5. WHEN STS_Identity is started with a missing `ConnectionStrings:IdentityDbConnection`, THE STS_Identity SHALL fail fast at startup with the exception message defined in Requirement 2.5.
6. WHEN Admin_Api is started with a missing `ConnectionStrings:IdentityDbConnection`, THE Admin_Api SHALL fail fast at startup with the exception message defined in Requirement 2.5.
7. WHEN `dotnet ef migrations add <Name>` is executed against TenantInfrastructure with `ConnectionStrings__IdentityDbConnection` set, THE command SHALL produce a migration without requiring `ConnectionStrings__MasterDb`.
