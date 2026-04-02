using Duende.IdentityServer.EntityFramework.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Admin.Storage.Entities.Configuration;

namespace Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.EntityConfigurations;

public sealed class ClientTenantRedirectUriConfiguration : IEntityTypeConfiguration<ClientTenantRedirectUri>
{
    public void Configure(EntityTypeBuilder<ClientTenantRedirectUri> builder)
    {
        builder.ToTable("ClientTenantRedirectUris");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.TenantKey)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.SignInCallbackUrl)
            .HasMaxLength(2000);

        builder.Property(x => x.SignOutCallbackUrl)
            .HasMaxLength(2000);

        builder.Property(x => x.CorsOrigin)
            .HasMaxLength(150);

        builder.HasIndex(x => new { x.ClientId, x.TenantKey })
            .IsUnique();

        builder.HasIndex(x => x.ClientId);

        builder.HasOne<Client>(x => x.Client)
            .WithMany()
            .HasForeignKey(x => x.ClientId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
