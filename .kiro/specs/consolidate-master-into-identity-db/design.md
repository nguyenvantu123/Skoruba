# Design Document: Consolidate `idsrv_master` into `IdentityServerAdmin`

> Trạng thái: Draft cho phase Design (workflow `requirements-first`).
> Phạm vi: thuần refactor hạ tầng dữ liệu cho `TenantInfrastructure`. Không thay đổi domain tenant, không đổi luồng auth/token, không thêm tính năng người dùng.

## Overview

Mục tiêu của thay đổi này là **gộp database `idsrv_master` vào database `IdentityServerAdmin`** thông qua một connection string duy nhất là `ConnectionStrings:IdentityDbConnection`. Sau khi áp dụng:

- `TenantInfrastructure.MasterDb.MasterDbContext` không còn trỏ tới một database riêng (`idsrv_master`) qua `ConnectionStrings:MasterDb` nữa, mà chia sẻ cùng database vật lý với `AdminIdentityDbContext` (đó là `IdentityServerAdmin`).
- STS_Identity và Admin_Api chỉ cần một connection string `IdentityDbConnection` để vận hành toàn bộ stack identity + tenant registry.
- `MasterDbContext` vẫn là một `DbContext` riêng biệt (về mặt EF Core), không "merge" vào `AdminIdentityDbContext`. Điều này giữ nguyên ranh giới layering: `TenantInfrastructure` không phụ thuộc vào assembly `Skoruba.Duende.IdentityServer.Admin.EntityFramework.*`, không có cross-context navigation, và migration của tenant registry hoàn toàn độc lập.

Tác động kiến trúc tổng quát:

| Thành phần | Trước | Sau |
| --- | --- | --- |
| `TenantInfrastructure.MasterDbContext` | Trỏ vào DB `idsrv_master` qua `MasterDb` connection string. Hardcode MySQL provider + lower-case naming. | Trỏ vào DB `IdentityServerAdmin` qua `IdentityDbConnection`. Provider khớp `DatabaseProviderConfiguration:ProviderType`. Lower-case naming chỉ khi provider là MySql. |
| `STS_Identity.Startup` | Đọc `Configuration.GetConnectionString("MasterDb")` cho `TenantInfrastructureOptions.MasterConnectionString`. | Đọc `Configuration.GetConnectionString(ConfigurationConsts.IdentityDbConnectionStringKey)` ("IdentityDbConnection"). Truyền thêm provider type. |
| `Admin_Api.Startup` | Tương tự như STS. | Tương tự như STS, đổi sang `IdentityDbConnection` + provider type. |
| `MasterDbContextFactory` (design-time) | Đọc env `ConnectionStrings__MasterDb`, hardcode `UseMySQL` + lower-case. | Đọc env `ConnectionStrings__IdentityDbConnection` (và `--connection=` ưu tiên hơn). Provider chọn theo `DatabaseProviderConfiguration__ProviderType`, mặc định `MySql` (giữ behavior hiện tại của lệnh `dotnet ef`). |
| `appsettings.json` STS/Admin.Api | Có cả `MasterDb` và `IdentityDbConnection`. | Bỏ key `ConnectionStrings:MasterDb`. Giữ `IdentityDbConnection` và section `TenantInfrastructure`. |
| `docker-compose.yml` | Có thể export `ConnectionStrings__MasterDb` cho STS/Admin.Api. | Không export `ConnectionStrings__MasterDb`; chỉ giữ `ConnectionStrings__IdentityDbConnection`. |

> Ràng buộc tương thích: tên type (`MasterDbContext`, `MasterDbContextFactory`, `EfTenantStore`, `EfTenantRepository`, `TenantRegistryCacheRefreshService`) và tên option (`MasterConnectionString`, `ApplyMasterDbMigrations`, `AllowMasterDbAutoMigration`, `RedisInstanceName`) **không đổi**, để tránh đụng vào nhiều file ở STS, Admin.Api, Admin.UI.Api, Views (`using TenantInfrastructure.MasterDb;`).

## Architecture

### Component diagram

```mermaid
flowchart LR
    subgraph STS["STS_Identity host"]
        STSStartup[Startup]
        StsIdResolver[StsIdentityDbConnectionStringResolver]
    end

    subgraph AdminApi["Admin_Api host"]
        AdminStartup[Startup]
    end

    subgraph TI["TenantInfrastructure (assembly)"]
        OPT[TenantInfrastructureOptions]
        SCE[ServiceCollectionExtensions.AddTenantInfrastructure]
        ABE[ApplicationBuilderExtensions.InitializeTenantMasterDbAsync]
        Factory[IDbContextFactory&lt;MasterDbContext&gt;]
        Ctx[MasterDbContext]
        EfStore[EfTenantStore]
        EfRepo[EfTenantRepository]
        Cached[CachedTenantStore]
        Cache[ITenantRegistryCache]
        Refresh[TenantRegistryCacheRefreshService]
        DesignFactory[MasterDbContextFactory IDesignTimeDbContextFactory]
    end

    subgraph DB[(IdentityServerAdmin database)]
        TblTenants[(tenants)]
        TblMigHist1[(__EFMigrationsHistory_TenantRegistry)]
        TblAdmin[(AspNetUsers, Roles, ... AdminIdentityDbContext tables)]
        TblMigHist2[(__EFMigrationsHistory)]
    end

    STSStartup -- "GetConnectionString(IdentityDbConnection)" --> OPT
    AdminStartup -- "GetConnectionString(IdentityDbConnection)" --> OPT
    OPT --> SCE
    SCE --> Factory
    Factory --> Ctx
    SCE --> EfStore
    SCE --> EfRepo
    SCE --> Cached
    SCE --> Cache
    SCE --> Refresh
    Cached --> EfStore
    EfStore --> Factory
    EfRepo --> Factory
    Refresh --> Cached
    StsIdResolver --> EfRepo
    Ctx --> TblTenants
    Ctx --> TblMigHist1
    DesignFactory --> Ctx
    AdminStartup -. "AdminIdentityDbContext via Skoruba helpers" .-> TblAdmin
    AdminStartup -. "EF Core default" .-> TblMigHist2
    STSStartup -. "AdminIdentityDbContext via Skoruba helpers" .-> TblAdmin
    ABE --> Factory
    STSStartup --> ABE
    AdminStartup --> ABE
```

### Database isolation strategy

`AdminIdentityDbContext` và `MasterDbContext` cùng nằm trong DB vật lý `IdentityServerAdmin`, nhưng:

- **Không chia sẻ EF model**: `MasterDbContext.OnModelCreating` chỉ ánh xạ `TenantInfo` ↔ `tenants`. Không reference `UserIdentity`, `IdentityServer*` entity. Không cross-context navigation.
- **Bảng không trùng**: `tenants` (lowercase) là tên duy nhất hiện hữu của tenant registry. `AdminIdentityDbContext`, `IdentityServerConfigurationDbContext`, `IdentityServerPersistedGrantDbContext`, `IdentityServerDataProtectionDbContext`, `AdminLogDbContext`, `AdminAuditLogDbContext`, `AdminConfigurationDbContext` không sử dụng tên `tenants`. (Đã verify trong codebase.)
- **Migration history tách biệt**: `MasterDbContext` ghi lịch sử migration vào bảng riêng `__EFMigrationsHistory_TenantRegistry`. Lý do: nếu để mặc định `__EFMigrationsHistory`, EF của TenantInfrastructure sẽ thấy các migration của Admin/Identity và tưởng là pending → có thể try-apply nhầm hoặc báo schema không khớp. Tách history table là cách tiêu chuẩn (EF Core hỗ trợ `MigrationsHistoryTable(string, schema)`).

### Connection multiplexing

Cả `AdminIdentityDbContext` và `MasterDbContext` đều mở connection độc lập tới cùng DB vật lý. Connection pool của ADO.NET (mỗi provider) sẽ tự gộp pool theo connection string normalized → không có race trên transaction vì hai context không chia sẻ `DbConnection`. Đây là pattern an toàn, đã được Skoruba dùng cho `IdentityServerConfigurationDbContext` + `IdentityServerPersistedGrantDbContext` cùng database.

## Components and Interfaces

### `TenantInfrastructureOptions` (giữ nguyên tên, thêm field provider)

```csharp
namespace TenantInfrastructure.Wiring;

public sealed class TenantInfrastructureOptions
{
    // Giữ nguyên tên field để tránh lan tỏa thay đổi.
    public string MasterConnectionString { get; set; } = default!;
    public bool ApplyMasterDbMigrations { get; set; }
    public bool AllowMasterDbAutoMigration { get; set; } = true;

    // MỚI: provider được host truyền vào, mirror DatabaseProviderConfiguration:ProviderType.
    // Dùng string để TenantInfrastructure không phụ thuộc enum của Admin.EntityFramework.Configuration.
    // Giá trị hợp lệ: "SqlServer" | "PostgreSQL" | "MySql" (phân biệt giống enum DatabaseProviderType).
    public string DatabaseProvider { get; set; } = "MySql";

    public TenantResolutionOptions Resolution { get; set; } = new();
    public TimeSpan TenantCacheAbsolute { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan TenantCacheSliding { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan TenantCacheRefreshInterval { get; set; } = TimeSpan.FromHours(1);
    public string RedisConnectionString { get; set; } = string.Empty;
    public string RedisInstanceName { get; set; } = "tenant-registry:";
    public bool AllowMissingTenant { get; set; } = true;
    public string[] SkipTenantResolutionHosts { get; set; } = new[] { "localhost", "127.0.0.1" };
}
```

> Ghi chú: dùng `string` thay vì enum giúp `TenantInfrastructure` không phải reference `Skoruba.Duende.IdentityServer.Admin.EntityFramework.Configuration` (giữ ranh giới layer). Bên trong `ServiceCollectionExtensions` sẽ parse string này thành nhánh nội bộ. Các giá trị hợp lệ tương ứng 1:1 với enum `DatabaseProviderType` (`SqlServer`, `PostgreSQL`, `MySql`).

### STS_Identity wiring

Thay đổi trong `Skoruba.Duende.IdentityServer.STS.Identity.Startup.ConfigureServices`:

```csharp
var databaseProviderConfiguration =
    Configuration.GetSection(nameof(DatabaseProviderConfiguration)).Get<DatabaseProviderConfiguration>()
    ?? new DatabaseProviderConfiguration { ProviderType = DatabaseProviderType.MySql };

var identityDbConnectionString =
    Configuration.GetConnectionString(ConfigurationConsts.IdentityDbConnectionStringKey);

if (string.IsNullOrWhiteSpace(identityDbConnectionString))
{
    throw new InvalidOperationException(
        $"Connection string '{ConfigurationConsts.IdentityDbConnectionStringKey}' is required " +
        "for TenantInfrastructure. Set ConnectionStrings:IdentityDbConnection in configuration.");
}

services.AddTenantInfrastructure(opt =>
{
    opt.DatabaseProvider = databaseProviderConfiguration.ProviderType.ToString();
    opt.MasterConnectionString = databaseProviderConfiguration.ProviderType == DatabaseProviderType.MySql
        ? NormalizeMySqlConnectionStringForDevelopment(identityDbConnectionString, Environment.IsDevelopment())
        : identityDbConnectionString;

    opt.RedisConnectionString = Configuration.GetConnectionString("Redis") ?? string.Empty;
    opt.RedisInstanceName = Configuration.GetValue<string>("TenantInfrastructure:RedisInstanceName") ?? "tenant-registry:";
    opt.ApplyMasterDbMigrations = Configuration.GetValue<bool>("TenantInfrastructure:ApplyMasterDbMigrations");
    opt.AllowMasterDbAutoMigration = Configuration.GetValue<bool>("TenantInfrastructure:AllowMasterDbAutoMigration");

    opt.Resolution.MinHostParts = 3;
    opt.Resolution.ReservedSubdomains.Add("sts");
    opt.Resolution.ReservedSubdomains.Add("identity");
    opt.Resolution.ReservedSubdomains.Add("sso");
    opt.Resolution.TenantHeaderNames.Clear();
    opt.Resolution.TenantHeaderNames.Add("X-Tenant-Id");

    if (Uri.TryCreate(tenantIdentityDbResolution.CentralBaseUrl, UriKind.Absolute, out var centralUri))
    {
        opt.Resolution.SkipHosts.Add(centralUri.Host);
    }
});
```

Điểm khác trước:
- Không còn `Configuration.GetConnectionString("MasterDb")`.
- `NormalizeMySqlConnectionStringForDevelopment` chỉ áp dụng khi provider là MySql (giữ behaviour dev-only `AllowPublicKeyRetrieval` / `SslMode` hiện tại). Với SqlServer / PostgreSQL không xử lý chuỗi.
- Fail-fast: ném `InvalidOperationException` ngay khi thiếu `IdentityDbConnection`.

### Admin_Api wiring

Thay đổi tương tự trong `Skoruba.Duende.IdentityServer.Admin.Api.Startup.ConfigureServices`. Đoạn `services.AddTenantInfrastructure(...)` chuyển sang đọc `IdentityDbConnection` + truyền `databaseProviderConfiguration.ProviderType.ToString()`. Validate fail-fast giống STS.

### Configuration files (`appsettings.json`)

`appsettings.json` (STS_Identity và Admin_Api) sau thay đổi:

- **Loại bỏ** `ConnectionStrings:MasterDb` (nếu đang có trong file local).
- **Giữ nguyên** `DatabaseProviderConfiguration:ProviderType` (đã là `"MySql"` hôm nay).
- **Giữ nguyên** section `TenantInfrastructure` với các key: `ApplyMasterDbMigrations`, `AllowMasterDbAutoMigration`, `RedisInstanceName` (operator không phải đổi tên key khi nâng cấp).
- Sample `appsettings.Development.json` (nếu có) cũng phải bỏ `MasterDb`.

Lưu ý: file `appsettings.json` mà ta inspect ở repo hiện không khai báo `ConnectionStrings:MasterDb` (chỉ giữ `IdentityDbConnection`), nhưng vẫn có deployment site có thể đang override qua biến môi trường. Sau Requirement 1.5, code sẽ ignore `MasterDb` env vars nếu `IdentityDbConnection` có mặt.

### `docker-compose.yml`

- **Loại bỏ** mọi env `ConnectionStrings__MasterDb=...` cho 3 service (`skoruba.duende.identityserver.admin`, `skoruba.duende.identityserver.admin.api`, `skoruba.duende.identityserver.sts.identity`). Hôm nay file `docker-compose.yml` ở repo root đã không có `MasterDb` cho STS/Admin.Api, nhưng phải check kỹ và xác nhận remove ở mọi compose override (`docker-compose.override.yml`, `obj/Docker/docker-compose.vs.*.yml` nếu được commit).
- **Giữ nguyên** `ConnectionStrings__IdentityDbConnection=...` cho cả STS và Admin.Api. Hai service hiện đang trỏ vào DB vật lý `IdentityServerAdmin`, đó là giá trị đích cho tenant registry.
- Thêm document trong `README` hoặc compose comment: "Tenant registry now lives inside IdentityServerAdmin database via IdentityDbConnection."

### Design-time `MasterDbContextFactory`

Sau thay đổi:

```csharp
public sealed class MasterDbContextFactory : IDesignTimeDbContextFactory<MasterDbContext>
{
    private const string ConnectionEnvVar = "ConnectionStrings__IdentityDbConnection";
    private const string ProviderEnvVar   = "DatabaseProviderConfiguration__ProviderType";
    private const string ConnectionArgPrefix = "--connection=";

    public MasterDbContext CreateDbContext(string[] args)
    {
        var providerName = Environment.GetEnvironmentVariable(ProviderEnvVar);
        var provider = ParseProvider(providerName); // mặc định MySql nếu null/empty (giữ behaviour hiện tại của dev runs)

        var connectionString = ResolveConnectionString(args);
        if (provider == TenantDatabaseProvider.MySql)
        {
            connectionString = NormalizeMySqlConnectionStringForDevelopment(connectionString);
        }

        var optionsBuilder = new DbContextOptionsBuilder<MasterDbContext>();
        var migrationsAssembly = typeof(MasterDbContext).Assembly.GetName().Name;
        var historyTable = "__EFMigrationsHistory_TenantRegistry";

        switch (provider)
        {
            case TenantDatabaseProvider.SqlServer:
                optionsBuilder.UseSqlServer(connectionString, sql =>
                {
                    sql.MigrationsAssembly(migrationsAssembly);
                    sql.MigrationsHistoryTable(historyTable);
                });
                break;
            case TenantDatabaseProvider.PostgreSQL:
                optionsBuilder.UseNpgsql(connectionString, npg =>
                {
                    npg.MigrationsAssembly(migrationsAssembly);
                    npg.MigrationsHistoryTable(historyTable);
                });
                break;
            case TenantDatabaseProvider.MySql:
                optionsBuilder.UseMySQL(connectionString, my =>
                {
                    my.MigrationsAssembly(migrationsAssembly);
                    my.MigrationsHistoryTable(historyTable);
                }).UseLowerCaseNamingConvention();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported provider '{providerName}'. Set {ProviderEnvVar} to SqlServer, PostgreSQL, or MySql.");
        }

        return new MasterDbContext(optionsBuilder.Options);
    }

    private static string ResolveConnectionString(string[] args)
    {
        // 1) --connection=... ưu tiên cao nhất
        var fromArgs = args
            .Select(a => a is null ? null
                : (a.StartsWith(ConnectionArgPrefix, StringComparison.OrdinalIgnoreCase)
                    ? a[ConnectionArgPrefix.Length..]
                    : null))
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        if (!string.IsNullOrWhiteSpace(fromArgs)) return fromArgs!;

        // 2) ConnectionStrings__IdentityDbConnection
        var fromEnv = Environment.GetEnvironmentVariable(ConnectionEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

        throw new InvalidOperationException(
            "Tenant registry connection string is missing. " +
            $"Set {ConnectionEnvVar} or pass --connection=<value> to dotnet ef.");
    }
}
```

Điểm quan trọng:
- KHÔNG đọc `ConnectionStrings__MasterDb` nữa (Requirement 6.5).
- `--connection=...` luôn thắng env var (Requirement 6.2).
- Mặc định provider = MySql khi env var không có giá trị → giữ hành vi cũ cho developer chạy `dotnet ef migrations` mà không cần set thêm biến môi trường.
- Error message mới chỉ dẫn đặt `ConnectionStrings__IdentityDbConnection` hoặc `--connection=`.

### Runtime contracts giữ nguyên (preservation)

Các contract và hành vi runtime sau **KHÔNG được thay đổi**:

| Contract / behaviour | Lý do giữ nguyên |
| --- | --- |
| `ITenantStore`, `ITenantRepository`, `ITenantRegistryCache`, `ITenantContextAccessor` | Chỉ refactor connection-string + provider; consumer (Admin.Api, STS, Admin.UI.Api) phụ thuộc các interface này — đụng vào sẽ tăng blast radius. |
| `EfTenantRepository.GetByKeyAsync` per-call timeout 10s (`LookupTimeout = TimeSpan.FromSeconds(10)`) | Đảm bảo SLA gọi tenant lookup trên hot path subdomain resolution không thay đổi. |
| `TenantRegistryCacheRefreshService` interval & log | Log message giữ nguyên: `"Refreshed tenant registry cache for {TenantCount} tenant(s)."` Severity Information. Interval mặc định `TenantCacheRefreshInterval` (1h) không đổi. |
| `SubdomainTenantResolver`, `TenantResolutionMiddleware` | Tenant resolution semantics không đổi. |
| `StsIdentityDbConnectionStringResolver` | Vẫn đọc `ConnectionStrings:IdentityDbConnection` cho central STS connection (đã làm sẵn) và đọc tenant-specific connection từ `ConnectionSecrets` JSON qua `EfTenantRepository` (không đổi public API). |
| Token lifetimes, signing keys, authentication scheme registration ở STS | Không trong scope. |
| Bảng `tenants` schema ngoài những gì §"Data Models" quy định | Chỉ `ConnectionSecrets` `HasColumnType` đổi theo provider; tên cột `ConnectionSecrets` giữ nguyên (qua `HasColumnName("ConnectionSecrets")`). |

## Data Models

### `MasterDbContext.OnModelCreating`

```csharp
public sealed class MasterDbContext : DbContext
{
    private readonly TenantDatabaseProvider _provider; // small enum private to TenantInfrastructure

    public MasterDbContext(DbContextOptions<MasterDbContext> options) : base(options)
    {
        _provider = options.FindExtension<TenantProviderOptionsExtension>()?.Provider
                    ?? TenantDatabaseProvider.MySql;
    }

    public DbSet<TenantInfo> Tenants => Set<TenantInfo>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<TenantInfo>(e =>
        {
            e.ToTable("tenants");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TenantKey).IsUnique();
            e.Property(x => x.TenantKey).HasMaxLength(64).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(256).IsRequired();
            e.Property(x => x.RedirectUrl).HasMaxLength(2048);
            e.Property(x => x.IsActive).IsRequired();
            e.Property(x => x.CreatedUtc).IsRequired();
            e.Ignore(x => x.ConnectionSecrets);

            e.Property(x => x.ConnectionSecretsJson)
                .HasColumnName("ConnectionSecrets")
                .HasColumnType(MapJsonColumnType(_provider))
                .IsRequired();
        });

        if (_provider == TenantDatabaseProvider.MySql)
        {
            b.ApplyLowerCaseNames(); // chỉ áp dụng với MySql, theo định hướng người dùng
        }
    }

    private static string MapJsonColumnType(TenantDatabaseProvider provider) => provider switch
    {
        TenantDatabaseProvider.MySql      => "json",
        TenantDatabaseProvider.PostgreSQL => "jsonb",   // payload là dict<string,string>, jsonb phù hợp
        TenantDatabaseProvider.SqlServer  => "nvarchar(max)",
        _ => "json",
    };
}
```

Quyết định cụ thể:

- **Lower-case naming**: chỉ áp dụng khi provider là `MySql` (theo yêu cầu trong instruction). Postgres/SqlServer giữ tên gốc của model (đã chỉ định `e.ToTable("tenants")` nên tên bảng vẫn là `tenants` ở mọi provider, đảm bảo tương thích schema MySQL hiện hữu).
- **Cột `ConnectionSecrets`**:
  - MySql → `json` (như hiện nay).
  - PostgreSQL → `jsonb` (truy vấn nhanh, validate JSON, được Npgsql hỗ trợ chuẩn). Payload là `Dictionary<string,string>`, không cần text thuần.
  - SqlServer → `nvarchar(max)` (SqlServer 2019+ có hỗ trợ `JSON_VALUE` trên `nvarchar(max)`; kiểu `json` chỉ có ở SQL Server 2025 RC nên dùng `nvarchar(max)` là an toàn).
- **`TenantInfo.TenantKey` index**: giữ unique, max length 64 (đủ cho tenant slug).
- **`TenantInfo.LogoUrl`**: vẫn cho phép null, không giới hạn length (giữ MySQL `longtext` hôm nay; Postgres/Sql sẽ default `text`/`nvarchar(max)`).
- **`Id`**: `int identity` mặc định EF Core, không đổi.

> `TenantProviderOptionsExtension` là một `IDbContextOptionsExtension` đơn giản nội bộ trong assembly TenantInfrastructure để gắn `TenantDatabaseProvider` vào `DbContextOptions`. Cách này tránh phải đọc lại từ singleton/configuration trong runtime của `OnModelCreating`. Một option đơn giản hơn: đổi `MasterDbContext` thành abstract + tạo 3 subclass (SqlServer/Postgres/MySql), nhưng phá tương thích `using TenantInfrastructure.MasterDb;` ở mọi nơi → loại bỏ. Giải pháp `IDbContextOptionsExtension` giữ nguyên type name.

### `MasterDbContext` registration trong `AddTenantInfrastructure`

```csharp
services.AddDbContextFactory<MasterDbContext>((sp, db) =>
{
    var provider = ParseProvider(opt.DatabaseProvider); // SqlServer | PostgreSQL | MySql
    var migrationsAssembly = typeof(MasterDbContext).Assembly.GetName().Name;
    var historyTable = "__EFMigrationsHistory_TenantRegistry";

    switch (provider)
    {
        case TenantDatabaseProvider.SqlServer:
            db.UseSqlServer(opt.MasterConnectionString, sql =>
            {
                sql.MigrationsAssembly(migrationsAssembly);
                sql.MigrationsHistoryTable(historyTable);
            });
            break;
        case TenantDatabaseProvider.PostgreSQL:
            db.UseNpgsql(opt.MasterConnectionString, npg =>
            {
                npg.MigrationsAssembly(migrationsAssembly);
                npg.MigrationsHistoryTable(historyTable);
            });
            break;
        case TenantDatabaseProvider.MySql:
            db.UseMySQL(opt.MasterConnectionString, my =>
            {
                my.MigrationsAssembly(migrationsAssembly);
                my.MigrationsHistoryTable(historyTable);
            }).UseLowerCaseNamingConvention();
            break;
        default:
            throw new InvalidOperationException(
                $"DatabaseProvider '{opt.DatabaseProvider}' is not supported. " +
                "Supported values: SqlServer, PostgreSQL, MySql.");
    }

    db.AddInterceptors(new TenantProviderInjectionInterceptor(provider)); // hoặc UseExtension
});
```

> Lưu ý: pattern `db.UseSqlServer(...)`, `db.UseNpgsql(...)`, `db.UseMySQL(...)` mirror đúng cách Skoruba `RegisterMySqlDbContexts` / `RegisterSqlServerDbContexts` / `RegisterNpgSqlDbContexts` đang làm cho `AdminIdentityDbContext`. Ta KHÔNG import các helper đó để giữ ranh giới layer (`TenantInfrastructure` không reference `Skoruba.Duende.IdentityServer.Admin.EntityFramework.*`); thay vào đó copy đúng phong cách wiring.

> `UseLowerCaseNamingConvention()` (gói `EFCore.NamingConventions`) chỉ gọi cho MySql, tương ứng định hướng. Nếu trong tương lai operator muốn naming convention cho Postgres, sẽ thêm flag riêng.

### Migrations strategy

**Quyết định: tiếp cận (A-lite) — giữ migration MySQL hiện tại và bổ sung khi triển khai provider khác.**

Lý do:
- Migrations hiện tại trong `TenantInfrastructure/MasterDb/Migrations/` đều là MySQL (`MySQLValueGenerationStrategy.IdentityColumn`, `tinyint(1)`, `varchar(64)`...). Re-generate cross-provider sẽ đòi tooling phức tạp hơn (provider-specific migration assembly).
- Hôm nay deployment thực tế đang chạy `ProviderType: MySql` (ref `appsettings.json`). Operator dùng SqlServer/PostgreSQL chưa có.
- Default vận hành là `ApplyMasterDbMigrations: true, AllowMasterDbAutoMigration: false` → `InitializeTenantMasterDbAsync` sẽ skip apply (chỉ log "auto-migration disabled"). Operator quản lý bảng `tenants` thủ công bằng SQL được cung cấp ở phần "Data copy" bên dưới.

Hệ quả thiết kế:
- Tiếp tục giữ thư mục `TenantInfrastructure/MasterDb/Migrations` cho MySQL.
- Thêm `MigrationsHistoryTable("__EFMigrationsHistory_TenantRegistry")` ở chỗ register DbContext để history tách biệt.
- Mở rộng tương lai: khi cần SqlServer/PostgreSQL migrations, tạo project con (hoặc folder con `Migrations/SqlServer`, `Migrations/PostgreSQL`) + migration assembly riêng, và chuyển nhánh `case` ở phần register để chọn assembly tương ứng. Thêm note ở `README.md` của Migrations.
- **Idempotent**: nếu bảng `tenants` đã tồn tại trong `IdentityServerAdmin` đúng schema, `EnsureCreatedAsync` sẽ no-op (EF chỉ tạo bảng còn thiếu). `MigrateAsync` sẽ skip migration đã apply (entry đã có trong `__EFMigrationsHistory_TenantRegistry`). Operator có thể seed history bằng tay nếu copy schema sang DB mới.

### One-time data copy SQL snippets

Đây là **bước thủ công** cho operator khi nâng cấp từ deployment đang chạy `idsrv_master` qua deployment mới chỉ dùng `IdentityServerAdmin`. KHÔNG tự chạy ở startup. Khuyến nghị: chạy trong cửa sổ maintenance, sau khi đã backup cả hai DB.

#### MySQL

```sql
-- Giả định cùng MySQL server, hai schema 'idsrv_master' và 'IdentityServerAdmin'.
-- Bảng đích là `IdentityServerAdmin`.`tenants` (đã được tạo bởi migration của TenantInfrastructure).
-- Bảo toàn primary key Id để không phá link tham chiếu (nếu có ngoại hệ).

INSERT INTO `IdentityServerAdmin`.`tenants`
    (Id, TenantKey, DisplayName, IsActive, ConnectionSecrets, RedirectUrl, LogoUrl, CreatedUtc)
SELECT
    src.Id,
    src.TenantKey,
    src.DisplayName,
    src.IsActive,
    src.ConnectionSecrets,
    src.RedirectUrl,
    src.LogoUrl,
    src.CreatedUtc
FROM `idsrv_master`.`tenants` AS src
LEFT JOIN `IdentityServerAdmin`.`tenants` AS dst
    ON dst.TenantKey = src.TenantKey
WHERE dst.TenantKey IS NULL; -- idempotent: chỉ copy tenant chưa tồn tại

-- Reset auto_increment cho bảng đích nếu cần:
SELECT MAX(Id) + 1 INTO @next_id FROM `IdentityServerAdmin`.`tenants`;
SET @stmt = CONCAT('ALTER TABLE `IdentityServerAdmin`.`tenants` AUTO_INCREMENT = ', @next_id);
PREPARE s FROM @stmt; EXECUTE s; DEALLOCATE PREPARE s;
```

#### PostgreSQL

```sql
-- Cross-database copy trong PostgreSQL cần postgres_fdw hoặc dump/restore.
-- Phương án dump/restore (đơn giản, ít quyền):
--   pg_dump --data-only --table=tenants idsrv_master > tenants.sql
--   psql -d IdentityServerAdmin -f tenants.sql
--
-- Nếu cùng cluster và đã enable postgres_fdw:

CREATE EXTENSION IF NOT EXISTS postgres_fdw;

CREATE SERVER IF NOT EXISTS legacy_master
    FOREIGN DATA WRAPPER postgres_fdw
    OPTIONS (host 'localhost', dbname 'idsrv_master', port '5432');

CREATE USER MAPPING IF NOT EXISTS FOR CURRENT_USER
    SERVER legacy_master
    OPTIONS (user 'postgres', password '...');

IMPORT FOREIGN SCHEMA public
    LIMIT TO (tenants)
    FROM SERVER legacy_master
    INTO pg_temp;

INSERT INTO public.tenants
    (Id, "TenantKey", "DisplayName", "IsActive", "ConnectionSecrets",
     "RedirectUrl", "LogoUrl", "CreatedUtc")
SELECT src."Id", src."TenantKey", src."DisplayName", src."IsActive",
       src."ConnectionSecrets"::jsonb, src."RedirectUrl", src."LogoUrl", src."CreatedUtc"
FROM pg_temp.tenants AS src
LEFT JOIN public.tenants AS dst ON dst."TenantKey" = src."TenantKey"
WHERE dst."TenantKey" IS NULL;

SELECT setval(pg_get_serial_sequence('public.tenants', 'Id'),
              COALESCE((SELECT MAX("Id") FROM public.tenants), 1));
```

> Lưu ý: tên cột case-sensitive ở Postgres tuỳ naming convention (`UseLowerCaseNamingConvention` không bật cho Postgres ở thiết kế này → giữ PascalCase). Operator phải kiểm tra metadata thực tế trước khi chạy.

#### SQL Server

```sql
-- Trên cùng instance, hai database 'idsrv_master' và 'IdentityServerAdmin'.
USE IdentityServerAdmin;

SET IDENTITY_INSERT dbo.tenants ON;

INSERT INTO dbo.tenants
    (Id, TenantKey, DisplayName, IsActive, ConnectionSecrets, RedirectUrl, LogoUrl, CreatedUtc)
SELECT
    src.Id,
    src.TenantKey,
    src.DisplayName,
    src.IsActive,
    src.ConnectionSecrets,
    src.RedirectUrl,
    src.LogoUrl,
    src.CreatedUtc
FROM idsrv_master.dbo.tenants AS src
LEFT JOIN IdentityServerAdmin.dbo.tenants AS dst
    ON dst.TenantKey = src.TenantKey
WHERE dst.TenantKey IS NULL;

SET IDENTITY_INSERT dbo.tenants OFF;

-- Reseed identity:
DECLARE @maxId INT = (SELECT ISNULL(MAX(Id), 0) FROM dbo.tenants);
DBCC CHECKIDENT ('dbo.tenants', RESEED, @maxId);
```

Document chính thức (operator runbook) sẽ đặt 3 snippet này cùng với checklist:
1. Backup `idsrv_master` và `IdentityServerAdmin`.
2. Apply migration của TenantInfrastructure trên `IdentityServerAdmin` (hoặc dùng `EnsureCreated` nếu chấp nhận).
3. Chạy snippet phù hợp với provider.
4. Smoke-test STS + Admin.Api với chỉ `IdentityDbConnection`.
5. Sau khi xác nhận, có thể decommission `idsrv_master`.

## Error Handling

### Startup validation (fail-fast)

| Tình huống | Hành vi |
| --- | --- |
| `ConnectionStrings:IdentityDbConnection` thiếu / rỗng (cả STS và Admin.Api) | Throw `InvalidOperationException("Connection string 'IdentityDbConnection' is required ...")` ngay trước khi gọi `AddTenantInfrastructure`. Host fail-fast với exit code != 0 (Requirement 2.5, 8.5, 8.6). |
| `DatabaseProviderConfiguration:ProviderType` không thuộc `{ SqlServer, PostgreSQL, MySql }` | `Configuration.GetSection(...).Get<DatabaseProviderConfiguration>()` đã raise binding error nếu enum không parse được; nếu binding trả về default (SqlServer = 0) khi key thiếu hoàn toàn, ta validate sau và throw `InvalidOperationException` chỉ khi key trống. (Requirement 3.7). |
| `MasterDbContextFactory.CreateDbContext` không có cả `--connection=` và env var | Throw `InvalidOperationException` với message hướng dẫn đặt biến (Requirement 6.3). |

### `InitializeTenantMasterDbAsync` log branches

`ApplicationBuilderExtensions.InitializeTenantMasterDbAsync` giữ logic cũ nhưng bổ sung log:

```csharp
public static async Task InitializeTenantMasterDbAsync(this IApplicationBuilder app)
{
    using var scope = app.ApplicationServices.CreateScope();
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MasterDbContext>>();
    var options = scope.ServiceProvider.GetRequiredService<TenantInfrastructureOptions>();
    var logger = scope.ServiceProvider
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("TenantInfrastructure.Init");

    await using var db = await factory.CreateDbContextAsync();

    if (options.ApplyMasterDbMigrations)
    {
        if (!options.AllowMasterDbAutoMigration)
        {
            logger.LogInformation(
                "Tenant registry migrations are configured but auto-migration is disabled. " +
                "Skipping Database.Migrate(). Operator must apply migrations manually against IdentityServerAdmin.");
            return;
        }

        logger.LogInformation("Applying tenant registry migrations against IdentityServerAdmin database.");
        await db.Database.MigrateAsync();
        return;
    }

    logger.LogInformation("Tenant registry migrations disabled; calling EnsureCreatedAsync on IdentityServerAdmin database.");
    await db.Database.EnsureCreatedAsync();
}
```

Bảng phân nhánh log message:

| `ApplyMasterDbMigrations` | `AllowMasterDbAutoMigration` | Hành vi | Log (Information) |
| --- | --- | --- | --- |
| `true` | `true` | `MigrateAsync()` | `Applying tenant registry migrations against IdentityServerAdmin database.` |
| `true` | `false` | Skip | `Tenant registry migrations are configured but auto-migration is disabled. Skipping Database.Migrate()...` |
| `false` | (any) | `EnsureCreatedAsync()` | `Tenant registry migrations disabled; calling EnsureCreatedAsync on IdentityServerAdmin database.` |

(Đây là yêu cầu tracking trong Requirement 4.6 / 4.7 / 4.8.)

### Backward compatibility & rollback

#### Tương thích cấu hình cũ

- Nếu deployment cũ vẫn còn `ConnectionStrings:MasterDb` trong `appsettings`, env, hoặc K8s ConfigMap: code mới **không đọc** giá trị này nữa (Requirement 1.5). Operator nhận log warning một lần (optional design enhancement) khi runtime phát hiện key cũ:
  - Code đề xuất (nice-to-have, không bắt buộc): trong `Startup` của STS/Admin.Api, sau khi resolve `IdentityDbConnection`, kiểm tra `Configuration.GetConnectionString("MasterDb")` và log Warning nếu non-empty: `"ConnectionStrings:MasterDb is deprecated and will be ignored. Please remove it; tenant registry now lives in IdentityServerAdmin via IdentityDbConnection."`
- Section `TenantInfrastructure` (key `ApplyMasterDbMigrations`, `AllowMasterDbAutoMigration`, `RedisInstanceName`) giữ nguyên tên → không phải sửa Helm chart / ConfigMap (Requirement 5.7).

#### Rollback path

Đây là **one-way migration được khuyến nghị**. Nếu phải rollback:

1. Revert Pull Request về codebase trước.
2. Khôi phục connection string `ConnectionStrings:MasterDb` cho STS và Admin.Api (nếu đã xoá).
3. Nếu data đã được copy sang `IdentityServerAdmin.tenants` và có thay đổi trong môi trường vận hành sau cutover:
   - Replay các thay đổi sang `idsrv_master.tenants` bằng SQL diff. Idempotent JOIN trong snippet bên trên có thể đảo chiều.
4. Documentation pattern: rollback chỉ an toàn trong vòng `T_cutover + ngắn hạn`. Không khuyến nghị giữ song song hai DB trong production lâu dài (gấp đôi cost phát sinh do TenantRegistryCacheRefreshService cache + Admin tenant CRUD).

## Testing Strategy

### Phạm vi và tại sao không dùng property-based testing

Đây là refactor hạ tầng + DI wiring + migration plumbing. Hành vi **không biến thiên có ý nghĩa theo input**: hoặc connection string có hoặc không, hoặc provider thuộc 1 trong 3 enum value. Chạy 100 iteration với ngẫu nhiên hóa không phát hiện thêm bug so với 2-3 example. Tests phù hợp là **example-based unit test** + **integration test**, đúng theo chỉ dẫn workflow ("PBT NOT appropriate: Configuration validation, Side-effect-only operations").

> Ghi chú: vì PBT không áp dụng, file design này cố ý **không có section "Correctness Properties"**. Khi chuyển sang phase Tasks, các sub-task "Write property test" sẽ KHÔNG xuất hiện; chỉ có sub-task "Write unit test" gắn với từng nhánh quyết định.

### Unit tests (must-have)

Đặt trong project test mới hoặc bổ sung vào project test hiện có cho TenantInfrastructure (ví dụ: `tests/Skoruba.Duende.IdentityServer.TenantInfrastructure.UnitTests/`).

| Test class | Mục tiêu |
| --- | --- |
| `MasterDbContextFactoryTests` | (a) `--connection=` ưu tiên hơn env var. (b) khi chỉ env `ConnectionStrings__IdentityDbConnection` có → factory chạy được. (c) khi cả hai trống → throw `InvalidOperationException` với message chứa `ConnectionStrings__IdentityDbConnection`. (d) `DatabaseProviderConfiguration__ProviderType=SqlServer` chọn `UseSqlServer`. (e) `=PostgreSQL` chọn `UseNpgsql`. (f) `=MySql` chọn `UseMySQL` + lower-case naming. (g) value lạ → throw. |
| `AddTenantInfrastructureProviderSwitchTests` | Build `IServiceProvider` từ `AddTenantInfrastructure` với từng `DatabaseProvider`. Assert `IDbContextFactory<MasterDbContext>` resolve thành công và `ctx.Database.ProviderName` đúng (`Microsoft.EntityFrameworkCore.SqlServer`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `MySql.EntityFrameworkCore`). |
| `AddTenantInfrastructureFailFastTests` | Khi `MasterConnectionString` rỗng → tuỳ chọn fail trong `AddTenantInfrastructure` hoặc tại Startup (cần unit test ở STS/Admin.Api Startup, hoặc test wrapper). Đảm bảo message khớp Requirement 2.5. |
| `MasterDbContextModelTests` (optional) | Build `IModel` cho mỗi provider, assert `ConnectionSecrets` có `HasColumnType` đúng (`json` / `jsonb` / `nvarchar(max)`). Assert table name = `"tenants"` ở mọi provider. |

### Integration tests (existing harness)

- Các test hiện có cho tenant registry phải tiếp tục pass khi chạy với in-memory provider hoặc SQLite (nếu repo đã có infra). Không cần test thêm cho 3 provider thật trong CI.
- Nếu test harness của Admin.Api/STS có `WebApplicationFactory`, bổ sung 1 test khẳng định `ConnectionStrings:IdentityDbConnection` rỗng → host throw đúng thông điệp (Requirement 8.5, 8.6).

### Manual smoke test (operator runbook)

Không phải task code, chỉ document trong PR description / RUNBOOK:

1. Set `ConnectionStrings__IdentityDbConnection=...` cho cả STS_Identity và Admin_Api.
2. KHÔNG set `ConnectionStrings__MasterDb`.
3. Boot STS_Identity → kiểm tra log có `Applying tenant registry migrations...` hoặc `Tenant registry migrations are configured but auto-migration is disabled...` tương ứng với cấu hình.
4. Boot Admin_Api → request `/api/tenants` trả 200.
5. Đăng nhập subdomain tenant trên STS → resolve thành công (Requirement 8.3, 7.5).

## Implementation Impact Map

Danh sách file dự kiến cần sửa khi sang phase Tasks (giữ scope tối thiểu):

| File | Thay đổi |
| --- | --- |
| `src/Skoruba.Duende.IdentityServer.TenantInfrastructure/TenantInfrastructure/MasterDb/MasterDbContext.cs` | Bỏ unconditional `b.ApplyLowerCaseNames()`. Thêm provider-aware `OnModelCreating` cho cột `ConnectionSecrets` (`json` / `jsonb` / `nvarchar(max)`) và lower-case naming chỉ-MySql. Thêm `ToTable("tenants")` (đảm bảo tên ở Postgres/SqlServer cũng là `tenants`). |
| `src/Skoruba.Duende.IdentityServer.TenantInfrastructure/TenantInfrastructure/MasterDb/MasterDbContextFactory.cs` | Đổi env var sang `ConnectionStrings__IdentityDbConnection`, thêm `DatabaseProviderConfiguration__ProviderType`, switch provider, message lỗi mới, normalize MySQL chỉ khi provider MySql. |
| `src/Skoruba.Duende.IdentityServer.TenantInfrastructure/TenantInfrastructure/Wiring/TenantInfrastructureOptions.cs` | Thêm property `DatabaseProvider` (string). Tên các option khác giữ nguyên. |
| `src/Skoruba.Duende.IdentityServer.TenantInfrastructure/TenantInfrastructure/Wiring/ServiceCollectionExtensions.cs` | Thay `db.UseMySQL(...).UseLowerCaseNamingConvention()` bằng switch theo `opt.DatabaseProvider`. Cấu hình `MigrationsAssembly` + `MigrationsHistoryTable("__EFMigrationsHistory_TenantRegistry")` cho mỗi nhánh. Normalize MySQL connection string chỉ khi provider là MySql. |
| `src/Skoruba.Duende.IdentityServer.TenantInfrastructure/TenantInfrastructure/Wiring/ApplicationBuilderExtensions.cs` | Thêm log Information cho 3 nhánh (apply / skip / EnsureCreated). Logic phân nhánh giữ nguyên. |
| `src/Skoruba.Duende.IdentityServer.TenantInfrastructure/TenantInfrastructure/MasterDb/Migrations/README.md` | Cập nhật ghi chú về env var design-time (`ConnectionStrings__IdentityDbConnection` thay cho `ConnectionStrings__MasterDb`) và migrations history table. |
| `src/Skoruba.Duende.IdentityServer.STS.Identity/Startup.cs` | Đọc `IdentityDbConnection` thay cho `MasterDb`. Truyền `databaseProviderConfiguration.ProviderType.ToString()` vào `opt.DatabaseProvider`. Fail-fast nếu `IdentityDbConnection` rỗng. Áp dụng `NormalizeMySqlConnectionStringForDevelopment` chỉ khi provider MySql. |
| `src/Skoruba.Duende.IdentityServer.STS.Identity/appsettings.json` (và `appsettings.Development.json` nếu có chứa key) | Bỏ `ConnectionStrings:MasterDb` nếu hiện diện. Giữ `IdentityDbConnection` và section `TenantInfrastructure`. |
| `src/Skoruba.Duende.IdentityServer.Admin.Api/Startup.cs` | Tương tự STS: đổi sang `IdentityDbConnection`, truyền provider type, fail-fast, normalize MySql có điều kiện. |
| `src/Skoruba.Duende.IdentityServer.Admin.Api/appsettings.json` (và Development) | Bỏ `ConnectionStrings:MasterDb` nếu hiện diện. |
| `docker-compose.yml` (root) và `docker-compose.override.yml` nếu có | Loại bỏ tất cả env `ConnectionStrings__MasterDb=...`. Giữ `ConnectionStrings__IdentityDbConnection=...` cho STS và Admin.Api (đã có sẵn). |
| (Optional / follow-up) `.../MasterDb/Migrations/SqlServer/`, `.../Migrations/PostgreSQL/` | KHÔNG tạo trong PR đầu. Note follow-up khi có deployment thực tế cho 2 provider này. |

> Lưu ý: các project consumer như `Skoruba.Duende.IdentityServer.Admin.UI.Api` và Views chỉ dùng `using TenantInfrastructure.MasterDb;` để gọi `EfTenantRepository`, `EfTenantStore`, `MasterDbContext`. Vì giữ nguyên tên type, các file đó **không cần sửa**.

## Summary of Design Decisions

1. **Giữ tên type và tên option** (`MasterDbContext`, `MasterDbContextFactory`, `MasterConnectionString`, `ApplyMasterDbMigrations`, `AllowMasterDbAutoMigration`) → blast radius nhỏ nhất.
2. **Routing connection string**: STS và Admin.Api đọc `ConnectionStrings:IdentityDbConnection` rồi gán vào `TenantInfrastructureOptions.MasterConnectionString`. `TenantInfrastructure` không tự đọc `IConfiguration`.
3. **Provider parity**: thêm `TenantInfrastructureOptions.DatabaseProvider` (string), switch trong `AddTenantInfrastructure` + `MasterDbContextFactory`.
4. **Naming convention chỉ cho MySql**: theo định hướng user. Postgres/SqlServer giữ tên gốc `tenants` qua `ToTable`.
5. **`ConnectionSecrets` per-provider**: `json` (MySql), `jsonb` (Postgres), `nvarchar(max)` (SqlServer).
6. **Migration approach**: giữ migration MySQL hiện có, tách history table `__EFMigrationsHistory_TenantRegistry`, follow-up khi deploy provider khác.
7. **Data copy**: thủ công, có 3 SQL snippet sẵn cho operator. Không tự chạy.
8. **Fail-fast**: thiếu `IdentityDbConnection` hoặc provider không hợp lệ → throw ở Startup.
9. **Test**: unit test cho factory + provider switch + fail-fast; tận dụng integration test sẵn có; smoke test thủ công ở runbook. Không dùng PBT (refactor hạ tầng, không có universal property có ý nghĩa).
10. **Rollback**: one-way; document path để revert PR + restore old DB nếu cần.
