using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.DbContexts;

#nullable disable

namespace Skoruba.Duende.IdentityServer.Admin.EntityFramework.MySql.Migrations.DataProtection
{
    [DbContext(typeof(IdentityServerDataProtectionDbContext))]
    [Migration("20260407200007_LowercaseTables")]
    public partial class LowercaseTables : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            RenameTableIfExists(migrationBuilder, "DataProtectionKeys", "dataprotectionkeys");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RenameTableIfExists(migrationBuilder, "dataprotectionkeys", "DataProtectionKeys");
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
