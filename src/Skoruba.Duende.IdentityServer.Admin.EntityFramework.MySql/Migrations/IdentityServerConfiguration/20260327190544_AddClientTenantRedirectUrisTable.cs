using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Skoruba.Duende.IdentityServer.Admin.EntityFramework.MySql.Migrations.IdentityServerConfiguration
{
    /// <inheritdoc />
    public partial class AddClientTenantRedirectUrisTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"
CREATE TABLE IF NOT EXISTS `ClientTenantRedirectUris` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `ClientId` int NOT NULL,
    `TenantKey` varchar(200) NOT NULL,
    `RedirectUrl` varchar(2000) NOT NULL,
    PRIMARY KEY (`Id`),
    KEY `IX_ClientTenantRedirectUris_ClientId` (`ClientId`),
    UNIQUE KEY `IX_ClientTenantRedirectUris_ClientId_TenantKey` (`ClientId`, `TenantKey`),
    CONSTRAINT `FK_ClientTenantRedirectUris_Clients_ClientId`
        FOREIGN KEY (`ClientId`) REFERENCES `Clients` (`Id`) ON DELETE CASCADE
) CHARSET=utf8mb4;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS `ClientTenantRedirectUris`;");
        }
    }
}
