using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using TenantInfrastructure.Wiring;

namespace TenantInfrastructure.MasterDb.Internal;

/// <summary>
/// EF Core options extension that carries the configured <see cref="TenantDatabaseProvider"/>
/// across <see cref="Microsoft.EntityFrameworkCore.DbContextOptions"/> so that
/// <see cref="MasterDbContext.OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder)"/>
/// can branch its mapping (column types, naming convention) per provider without taking a
/// runtime dependency on <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>.
/// <para>
/// This extension intentionally does NOT register any services and is NOT itself a database
/// provider. It is metadata-only (<see cref="ExtensionInfo.IsDatabaseProvider"/> returns
/// <c>false</c>). The actual provider (<c>UseSqlServer</c>, <c>UseNpgsql</c>, <c>UseMySQL</c>)
/// is configured separately in <c>ServiceCollectionExtensions.AddTenantInfrastructure</c>
/// and <c>MasterDbContextFactory</c>.
/// </para>
/// </summary>
internal sealed class TenantProviderOptionsExtension : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public TenantProviderOptionsExtension(TenantDatabaseProvider provider)
    {
        Provider = provider;
    }

    public TenantDatabaseProvider Provider { get; }

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
        // No services to register: this extension only carries metadata.
    }

    public void Validate(IDbContextOptions options)
    {
        // No validation required.
    }

    private sealed class ExtensionInfo : DbContextOptionsExtensionInfo
    {
        public ExtensionInfo(TenantProviderOptionsExtension extension) : base(extension)
        {
        }

        private new TenantProviderOptionsExtension Extension => (TenantProviderOptionsExtension)base.Extension;

        public override bool IsDatabaseProvider => false;

        public override string LogFragment => $"TenantProvider={Extension.Provider} ";

        public override int GetServiceProviderHashCode() => Extension.Provider.GetHashCode();

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo otherInfo && otherInfo.Extension.Provider == Extension.Provider;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            debugInfo["TenantInfrastructure:TenantProvider"] = Extension.Provider.ToString();
        }
    }
}
