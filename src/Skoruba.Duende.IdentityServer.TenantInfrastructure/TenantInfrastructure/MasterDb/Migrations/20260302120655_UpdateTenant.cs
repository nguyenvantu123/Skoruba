using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantInfrastructure.MasterDb.Migrations
{
    /// <inheritdoc />
    public partial class UpdateTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConnectionSecrets",
                table: "Tenants",
                type: "json",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.Sql(@"
UPDATE `Tenants`
SET `ConnectionSecrets` = JSON_OBJECT('BlazorApiUser', `ConnectionStringSecretName`)
WHERE `ConnectionStringSecretName` IS NOT NULL AND `ConnectionStringSecretName` <> '';");

            migrationBuilder.DropColumn(
                name: "ConnectionStringSecretName",
                table: "Tenants");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConnectionStringSecretName",
                table: "Tenants",
                type: "varchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(@"
UPDATE `Tenants`
SET `ConnectionStringSecretName` = COALESCE(
    JSON_UNQUOTE(JSON_EXTRACT(`ConnectionSecrets`, '$.BlazorApiUser')),
    ''
);");

            migrationBuilder.DropColumn(
                name: "ConnectionSecrets",
                table: "Tenants");
        }
    }
}
