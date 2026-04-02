using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantInfrastructure.Identity;

// namespace theo project bạn
namespace Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.EntityConfigurations
{
    public sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
    {
        public void Configure(EntityTypeBuilder<ApplicationUser> e)
        {
            e.Property(x => x.TenantKey)
                .HasMaxLength(64)
                .IsRequired();

            e.Property(x => x.BranchCode)
                .HasMaxLength(64)
                .IsRequired();

            e.Property(x => x.AdminLevel)
                .HasMaxLength(32)
                .IsRequired();

            e.HasIndex(x => x.TenantKey);
            e.HasIndex(x => x.BranchCode);
        }
    }
}
