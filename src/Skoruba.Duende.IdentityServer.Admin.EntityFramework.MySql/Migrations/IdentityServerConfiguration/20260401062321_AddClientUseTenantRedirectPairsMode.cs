using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Skoruba.Duende.IdentityServer.Admin.EntityFramework.MySql.Migrations.IdentityServerConfiguration
{
    /// <inheritdoc />
    public partial class AddClientUseTenantRedirectPairsMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UseTenantRedirectPairs",
                table: "Clients",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(@"
UPDATE `Clients` AS c
SET `UseTenantRedirectPairs` = 1
WHERE EXISTS (
    SELECT 1
    FROM `ClientTenantRedirectUris` AS t
    WHERE t.`ClientId` = c.`Id`
)
OR EXISTS (
    SELECT 1
    FROM `ClientProperties` AS p
    WHERE p.`ClientId` = c.`Id`
      AND p.`Key` = 'skoruba_tenant_redirect_pairs'
      AND p.`Value` IS NOT NULL
      AND TRIM(p.`Value`) <> ''
);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UseTenantRedirectPairs",
                table: "Clients");
        }
    }
}
