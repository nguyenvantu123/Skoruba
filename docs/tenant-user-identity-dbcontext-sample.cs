using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.Configuration.Schema;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.Entities.Identity;

namespace TenantUserApi.Identity;

public sealed class TenantUserIdentityDbContext
    : IdentityDbContext<
        UserIdentity,
        UserIdentityRole,
        string,
        UserIdentityUserClaim,
        UserIdentityUserRole,
        UserIdentityUserLogin,
        UserIdentityRoleClaim,
        UserIdentityUserToken>
{
    private readonly IdentityTableConfiguration _schemaConfiguration;

    public TenantUserIdentityDbContext(
        DbContextOptions<TenantUserIdentityDbContext> options,
        IdentityTableConfiguration? schemaConfiguration = null)
        : base(options)
    {
        _schemaConfiguration = schemaConfiguration ?? new IdentityTableConfiguration();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<UserIdentityRole>().ToTable(_schemaConfiguration.IdentityRoles);
        builder.Entity<UserIdentityRoleClaim>().ToTable(_schemaConfiguration.IdentityRoleClaims);
        builder.Entity<UserIdentityUserRole>().ToTable(_schemaConfiguration.IdentityUserRoles);
        builder.Entity<UserIdentity>().ToTable(_schemaConfiguration.IdentityUsers);
        builder.Entity<UserIdentityUserLogin>().ToTable(_schemaConfiguration.IdentityUserLogins);
        builder.Entity<UserIdentityUserClaim>().ToTable(_schemaConfiguration.IdentityUserClaims);
        builder.Entity<UserIdentityUserToken>().ToTable(_schemaConfiguration.IdentityUserTokens);

        builder.Entity<UserIdentity>(entity =>
        {
            entity.Property(x => x.TenantKey)
                .HasMaxLength(64)
                .IsRequired();

            entity.Property(x => x.BranchCode)
                .HasMaxLength(64)
                .IsRequired();

            entity.HasIndex(x => x.TenantKey);
            entity.HasIndex(x => x.BranchCode);
        });
    }
}
