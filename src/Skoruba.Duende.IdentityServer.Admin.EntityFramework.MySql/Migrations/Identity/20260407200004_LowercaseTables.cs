using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.DbContexts;

#nullable disable

namespace Skoruba.Duende.IdentityServer.Admin.EntityFramework.MySql.Migrations.Identity
{
    [DbContext(typeof(AdminIdentityDbContext))]
    [Migration("20260407200004_LowercaseTables")]
    public partial class LowercaseTables : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            RenameTableIfExists(migrationBuilder, "ApplicationUser", "applicationuser");
            RenameTableIfExists(migrationBuilder, "Roles", "roles");
            RenameTableIfExists(migrationBuilder, "Users", "users");
            RenameTableIfExists(migrationBuilder, "RoleClaims", "roleclaims");
            RenameTableIfExists(migrationBuilder, "UserClaims", "userclaims");
            RenameTableIfExists(migrationBuilder, "UserLogins", "userlogins");
            RenameTableIfExists(migrationBuilder, "UserRoles", "userroles");
            RenameTableIfExists(migrationBuilder, "UserTokens", "usertokens");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RenameTableIfExists(migrationBuilder, "applicationuser", "ApplicationUser");
            RenameTableIfExists(migrationBuilder, "roles", "Roles");
            RenameTableIfExists(migrationBuilder, "users", "Users");
            RenameTableIfExists(migrationBuilder, "roleclaims", "RoleClaims");
            RenameTableIfExists(migrationBuilder, "userclaims", "UserClaims");
            RenameTableIfExists(migrationBuilder, "userlogins", "UserLogins");
            RenameTableIfExists(migrationBuilder, "userroles", "UserRoles");
            RenameTableIfExists(migrationBuilder, "usertokens", "UserTokens");
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
