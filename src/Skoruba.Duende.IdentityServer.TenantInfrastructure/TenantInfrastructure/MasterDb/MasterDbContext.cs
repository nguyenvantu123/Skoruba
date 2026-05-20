using Microsoft.EntityFrameworkCore;
using TenantInfrastructure.MasterDb.Internal;
using TenantInfrastructure.Wiring;

namespace TenantInfrastructure.MasterDb;

public sealed class MasterDbContext : DbContext
{
    private readonly TenantDatabaseProvider _provider;

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
            e.Property(x => x.ConnectionSecretsJson)
                .HasColumnName("ConnectionSecrets")
                .HasColumnType(MapJsonColumnType(_provider))
                .IsRequired();
            e.Ignore(x => x.ConnectionSecrets);
            e.Property(x => x.RedirectUrl).HasMaxLength(2048);
            e.Property(x => x.IsActive).IsRequired();
            e.Property(x => x.CreatedUtc).IsRequired();
        });

        if (_provider == TenantDatabaseProvider.MySql)
        {
            b.ApplyLowerCaseNames();
        }
    }

    private static string MapJsonColumnType(TenantDatabaseProvider provider) => provider switch
    {
        TenantDatabaseProvider.MySql => "json",
        TenantDatabaseProvider.PostgreSQL => "jsonb",
        TenantDatabaseProvider.SqlServer => "nvarchar(max)",
        _ => "json",
    };
}
