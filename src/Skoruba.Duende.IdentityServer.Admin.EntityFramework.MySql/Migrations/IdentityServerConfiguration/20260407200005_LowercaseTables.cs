using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.DbContexts;

#nullable disable

namespace Skoruba.Duende.IdentityServer.Admin.EntityFramework.MySql.Migrations.IdentityServerConfiguration
{
    [DbContext(typeof(IdentityServerConfigurationDbContext))]
    [Migration("20260407200005_LowercaseTables")]
    public partial class LowercaseTables : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            RenameTableIfExists(migrationBuilder, "ApiResourceClaims", "apiresourceclaims");
            RenameTableIfExists(migrationBuilder, "ApiResourceProperties", "apiresourceproperties");
            RenameTableIfExists(migrationBuilder, "ApiResources", "apiresources");
            RenameTableIfExists(migrationBuilder, "ApiResourceScopes", "apiresourcescopes");
            RenameTableIfExists(migrationBuilder, "ApiResourceSecrets", "apiresourcesecrets");
            RenameTableIfExists(migrationBuilder, "ApiScopeClaims", "apiscopeclaims");
            RenameTableIfExists(migrationBuilder, "ApiScopeProperties", "apiscopeproperties");
            RenameTableIfExists(migrationBuilder, "ApiScopes", "apiscopes");
            RenameTableIfExists(migrationBuilder, "ClientClaims", "clientclaims");
            RenameTableIfExists(migrationBuilder, "ClientCorsOrigins", "clientcorsorigins");
            RenameTableIfExists(migrationBuilder, "ClientGrantTypes", "clientgranttypes");
            RenameTableIfExists(migrationBuilder, "ClientIdPRestrictions", "clientidprestrictions");
            RenameTableIfExists(migrationBuilder, "ClientPostLogoutRedirectUris", "clientpostlogoutredirecturis");
            RenameTableIfExists(migrationBuilder, "ClientProperties", "clientproperties");
            RenameTableIfExists(migrationBuilder, "ClientRedirectUris", "clientredirecturis");
            RenameTableIfExists(migrationBuilder, "Clients", "clients");
            RenameTableIfExists(migrationBuilder, "ClientScopes", "clientscopes");
            RenameTableIfExists(migrationBuilder, "ClientSecrets", "clientsecrets");
            RenameTableIfExists(migrationBuilder, "ClientTenantRedirectUris", "clienttenantredirecturis");
            RenameTableIfExists(migrationBuilder, "IdentityProviders", "identityproviders");
            RenameTableIfExists(migrationBuilder, "IdentityResourceClaims", "identityresourceclaims");
            RenameTableIfExists(migrationBuilder, "IdentityResourceProperties", "identityresourceproperties");
            RenameTableIfExists(migrationBuilder, "IdentityResources", "identityresources");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RenameTableIfExists(migrationBuilder, "apiresourceclaims", "ApiResourceClaims");
            RenameTableIfExists(migrationBuilder, "apiresourceproperties", "ApiResourceProperties");
            RenameTableIfExists(migrationBuilder, "apiresources", "ApiResources");
            RenameTableIfExists(migrationBuilder, "apiresourcescopes", "ApiResourceScopes");
            RenameTableIfExists(migrationBuilder, "apiresourcesecrets", "ApiResourceSecrets");
            RenameTableIfExists(migrationBuilder, "apiscopeclaims", "ApiScopeClaims");
            RenameTableIfExists(migrationBuilder, "apiscopeproperties", "ApiScopeProperties");
            RenameTableIfExists(migrationBuilder, "apiscopes", "ApiScopes");
            RenameTableIfExists(migrationBuilder, "clientclaims", "ClientClaims");
            RenameTableIfExists(migrationBuilder, "clientcorsorigins", "ClientCorsOrigins");
            RenameTableIfExists(migrationBuilder, "clientgranttypes", "ClientGrantTypes");
            RenameTableIfExists(migrationBuilder, "clientidprestrictions", "ClientIdPRestrictions");
            RenameTableIfExists(migrationBuilder, "clientpostlogoutredirecturis", "ClientPostLogoutRedirectUris");
            RenameTableIfExists(migrationBuilder, "clientproperties", "ClientProperties");
            RenameTableIfExists(migrationBuilder, "clientredirecturis", "ClientRedirectUris");
            RenameTableIfExists(migrationBuilder, "clients", "Clients");
            RenameTableIfExists(migrationBuilder, "clientscopes", "ClientScopes");
            RenameTableIfExists(migrationBuilder, "clientsecrets", "ClientSecrets");
            RenameTableIfExists(migrationBuilder, "clienttenantredirecturis", "ClientTenantRedirectUris");
            RenameTableIfExists(migrationBuilder, "identityproviders", "IdentityProviders");
            RenameTableIfExists(migrationBuilder, "identityresourceclaims", "IdentityResourceClaims");
            RenameTableIfExists(migrationBuilder, "identityresourceproperties", "IdentityResourceProperties");
            RenameTableIfExists(migrationBuilder, "identityresources", "IdentityResources");
        }

        private static void RenameTableIfExists(MigrationBuilder migrationBuilder, string sourceName, string targetName)
        {
            migrationBuilder.Sql($"SET @rename_sql := (SELECT IF(EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = '{sourceName}'), 'RENAME TABLE `{sourceName}` TO `{targetName}`', 'SELECT 1')); ");
            migrationBuilder.Sql("PREPARE stmt FROM @rename_sql;");
            migrationBuilder.Sql("EXECUTE stmt;");
            migrationBuilder.Sql("DEALLOCATE PREPARE stmt;");
        }
    }
}
