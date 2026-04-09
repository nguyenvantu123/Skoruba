using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TenantInfrastructure.MasterDb;

#nullable disable

namespace TenantInfrastructure.MasterDb.Migrations
{
    [DbContext(typeof(MasterDbContext))]
    [Migration("20260407200008_LowercaseTables")]
    public partial class LowercaseTables : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            RenameTableIfExists(migrationBuilder, "Tenants", "tenants");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RenameTableIfExists(migrationBuilder, "tenants", "Tenants");
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
