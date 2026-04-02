-- Tenant user API MySQL migration template
-- Goal:
-- 1. Move ASP.NET Identity default tables (AspNet*) to Skoruba naming
-- 2. Add TenantKey and BranchCode to Users
-- 3. Drop old AspNet* table names by renaming them
--
-- IMPORTANT:
-- - Run this on ONE tenant database at a time.
-- - Replace __TENANT_KEY__ and __BRANCH_CODE__ before execution.
-- - Take a backup before running.

START TRANSACTION;

SET @tenant_key := '__TENANT_KEY__';
SET @branch_code := '__BRANCH_CODE__';

-- Users
ALTER TABLE `AspNetUsers`
    ADD COLUMN `TenantKey` varchar(64) NULL,
    ADD COLUMN `BranchCode` varchar(64) NULL;

UPDATE `AspNetUsers`
SET
    `TenantKey` = COALESCE(NULLIF(TRIM(`TenantKey`), ''), @tenant_key),
    `BranchCode` = COALESCE(NULLIF(TRIM(`BranchCode`), ''), @branch_code);

ALTER TABLE `AspNetUsers`
    MODIFY COLUMN `TenantKey` varchar(64) NOT NULL,
    MODIFY COLUMN `BranchCode` varchar(64) NOT NULL;

CREATE INDEX `IX_Users_TenantKey_tmp` ON `AspNetUsers` (`TenantKey`);
CREATE INDEX `IX_Users_BranchCode_tmp` ON `AspNetUsers` (`BranchCode`);

-- Rename Identity tables to Skoruba naming
RENAME TABLE `AspNetUsers` TO `Users`;
RENAME TABLE `AspNetRoles` TO `Roles`;
RENAME TABLE `AspNetUserRoles` TO `UserRoles`;
RENAME TABLE `AspNetUserClaims` TO `UserClaims`;
RENAME TABLE `AspNetUserLogins` TO `UserLogins`;
RENAME TABLE `AspNetUserTokens` TO `UserTokens`;
RENAME TABLE `AspNetRoleClaims` TO `RoleClaims`;

-- Normalize index names to match Skoruba/Identity expectations more closely
ALTER TABLE `Users`
    DROP INDEX `IX_Users_TenantKey_tmp`,
    DROP INDEX `IX_Users_BranchCode_tmp`,
    ADD INDEX `IX_Users_TenantKey` (`TenantKey`),
    ADD INDEX `IX_Users_BranchCode` (`BranchCode`);

-- Best-effort rename of standard Identity indexes if they still use AspNet names
SET @sql := (
    SELECT IF(
        EXISTS(
            SELECT 1
            FROM information_schema.statistics
            WHERE table_schema = DATABASE()
              AND table_name = 'Users'
              AND index_name = 'UserNameIndex'
        ),
        'SELECT 1',
        'ALTER TABLE `Users` ADD UNIQUE INDEX `UserNameIndex` (`NormalizedUserName`)'
    )
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @sql := (
    SELECT IF(
        EXISTS(
            SELECT 1
            FROM information_schema.statistics
            WHERE table_schema = DATABASE()
              AND table_name = 'Users'
              AND index_name = 'EmailIndex'
        ),
        'SELECT 1',
        'ALTER TABLE `Users` ADD INDEX `EmailIndex` (`NormalizedEmail`)'
    )
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

COMMIT;
