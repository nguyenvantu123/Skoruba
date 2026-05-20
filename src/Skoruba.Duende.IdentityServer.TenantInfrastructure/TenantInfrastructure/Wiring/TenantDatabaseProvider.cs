namespace TenantInfrastructure.Wiring;

/// <summary>
/// Internal enumeration of EF Core providers supported by
/// <see cref="TenantInfrastructure.MasterDb.MasterDbContext"/>. Mirrors 1:1 the
/// <c>DatabaseProviderType</c> enum exposed by
/// <c>Skoruba.Duende.IdentityServer.Admin.EntityFramework.Configuration</c>, but kept
/// internal here so this assembly does not take a reference on the Admin EntityFramework
/// configuration project.
/// </summary>
internal enum TenantDatabaseProvider
{
    SqlServer,
    PostgreSQL,
    MySql,
}
