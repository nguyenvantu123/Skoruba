using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Skoruba.Duende.IdentityServer.Admin.EntityFramework.MySql.Migrations.IdentityServerConfiguration
{
    /// <inheritdoc />
    public partial class AddTenantPostLogoutAndCorsToClientTenantRedirectUris : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "RedirectUrl",
                table: "ClientTenantRedirectUris",
                type: "varchar(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(2000)",
                oldMaxLength: 2000);

            migrationBuilder.AddColumn<string>(
                name: "CorsOrigin",
                table: "ClientTenantRedirectUris",
                type: "varchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PostLogoutRedirectUrl",
                table: "ClientTenantRedirectUris",
                type: "varchar(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CorsOrigin",
                table: "ClientTenantRedirectUris");

            migrationBuilder.DropColumn(
                name: "PostLogoutRedirectUrl",
                table: "ClientTenantRedirectUris");

            migrationBuilder.AlterColumn<string>(
                name: "RedirectUrl",
                table: "ClientTenantRedirectUris",
                type: "varchar(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "varchar(2000)",
                oldMaxLength: 2000,
                oldNullable: true);
        }
    }
}
