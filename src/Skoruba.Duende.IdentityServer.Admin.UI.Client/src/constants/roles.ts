export const Roles = {
  SuperAdmin: "SkorubaIdentityAdminAdministrator",
  TenantAdmin: "SkorubaIdentityTenantAdministrator",
} as const;

export type RoleName = (typeof Roles)[keyof typeof Roles];

export const hasRole = (roles: string[] | undefined, role: RoleName) =>
  !!roles?.some((r) => r.toLowerCase() === role.toLowerCase());
