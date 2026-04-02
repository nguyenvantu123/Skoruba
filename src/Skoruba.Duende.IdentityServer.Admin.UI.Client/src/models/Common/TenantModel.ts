export interface TenantModel {
  id: number;
  tenantKey: string;
  displayName: string;
  isActive: boolean;
  connectionSecrets: Record<string, string>;
  redirectUrl?: string | null;
  logoUrl?: string | null;
  createdUtc: string;
}

export interface TenantData {
  items: TenantModel[];
  totalCount: number;
}
