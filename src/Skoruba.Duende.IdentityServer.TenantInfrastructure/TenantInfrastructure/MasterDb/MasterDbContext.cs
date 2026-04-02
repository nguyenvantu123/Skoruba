using Microsoft.EntityFrameworkCore;

namespace TenantInfrastructure.MasterDb;

public sealed class MasterDbContext : DbContext
{
    public MasterDbContext(DbContextOptions<MasterDbContext> options) : base(options) { }

    public DbSet<TenantInfo> Tenants => Set<TenantInfo>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<TenantInfo>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TenantKey).IsUnique();
            e.Property(x => x.TenantKey).HasMaxLength(64).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(256).IsRequired();
            e.Property(x => x.ConnectionSecretsJson)
                .HasColumnName("ConnectionSecrets")
                .HasColumnType("json")
                .IsRequired();
            e.Ignore(x => x.ConnectionSecrets);
            e.Property(x => x.RedirectUrl).HasMaxLength(2048);
            e.Property(x => x.IsActive).IsRequired();
        });
    }
}
