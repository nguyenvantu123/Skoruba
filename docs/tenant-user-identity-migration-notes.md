# Tenant User Identity Migration Notes

This workspace does not include the tenant user API runtime project, so the actual DbContext registration and EF migration cannot be executed here end-to-end.

To align tenant user authentication with Skoruba STS:

1. Use a tenant-side `IdentityDbContext<UserIdentity, ...>` equivalent.
2. Map tables to:
   - `Users`
   - `Roles`
   - `UserRoles`
   - `UserClaims`
   - `UserLogins`
   - `UserTokens`
   - `RoleClaims`
3. Ensure the `Users` table contains:
   - `TenantKey` (`varchar(64)`, required)
   - `BranchCode` (`varchar(64)`, required)
4. Migrate existing data from default ASP.NET Identity `AspNet*` tables using:
   - [tenant-user-identity-mysql-migration.sql](/E:/Duende.IdentityServer.Admin/docs/tenant-user-identity-mysql-migration.sql)
5. After tenant user API is updated, STS tenant-host login can safely resolve tenant DB connection strings without breaking on table/schema mismatch.

Files provided in this repo:

- Sample DbContext:
  - [tenant-user-identity-dbcontext-sample.cs](/E:/Duende.IdentityServer.Admin/docs/tenant-user-identity-dbcontext-sample.cs)
- MySQL migration template:
  - [tenant-user-identity-mysql-migration.sql](/E:/Duende.IdentityServer.Admin/docs/tenant-user-identity-mysql-migration.sql)
