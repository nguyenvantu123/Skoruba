import ApiHelper from "@/helpers/ApiHelper";
import { client } from "@skoruba/duende.identityserver.admin.api.client";
import { TenantData, TenantModel } from "@/models/Common/TenantModel";
import { TenantFormData } from "@/pages/Tenant/Common/TenantSchema";
import { useMutation, useQuery, useQueryClient } from "react-query";
import { queryKeys } from "./QueryKeys";

const getClient = () => new client.TenantsClient(ApiHelper.getApiBaseUrl());

const mapConnectionSecretsToForm = (
  connectionSecrets?: Record<string, string> | null
) => Object.entries(connectionSecrets ?? {}).map(([key, value]) => ({ key, value }));

const mapConnectionSecretsToRequest = (
  connectionSecrets: TenantFormData["connectionSecrets"]
): Record<string, string> =>
  Object.fromEntries(
    connectionSecrets.map((item) => [item.key.trim(), item.value.trim()])
  );

type TenantApiLike = {
  id?: number;
  tenantKey?: string;
  displayName?: string;
  isActive?: boolean;
  connectionSecrets?: Record<string, string> | null;
  redirectUrl?: string | null;
  logoUrl?: string | null;
  createdUtc?: Date | { toISOString: () => string } | string | null;
};

export type TenantLogoUploadResult = {
  logoUrl: string;
};

const mapCreatedUtc = (createdUtc: TenantApiLike["createdUtc"]): string => {
  if (!createdUtc) {
    return "";
  }

  if (typeof createdUtc === "string") {
    return createdUtc;
  }

  if (createdUtc instanceof Date) {
    return createdUtc.toISOString();
  }

  return createdUtc.toISOString();
};

export const uploadTenantLogo = async (
  tenantKey: string,
  file: File
): Promise<TenantLogoUploadResult> => {
  const formData = new FormData();
  formData.append("tenantKey", tenantKey);
  formData.append("file", file);

  const response = await fetch(`${ApiHelper.getApiBaseUrl()}/api/tenants/logo`, {
    method: "POST",
    headers: {
      "X-ANTI-CSRF": "1",
    },
    body: formData,
  });

  if (!response.ok) {
    const responseText = await response.text();
    throw new client.SwaggerException(
      "An unexpected server error occurred.",
      response.status,
      responseText,
      {},
      null
    );
  }

  return (await response.json()) as TenantLogoUploadResult;
};

const mapTenant = (tenant: TenantApiLike): TenantModel => ({
  id: tenant.id ?? 0,
  tenantKey: tenant.tenantKey ?? "",
  displayName: tenant.displayName ?? "",
  isActive: tenant.isActive ?? false,
  connectionSecrets: tenant.connectionSecrets ?? {},
  redirectUrl: tenant.redirectUrl ?? null,
  logoUrl: tenant.logoUrl ?? null,
  createdUtc: mapCreatedUtc(tenant.createdUtc),
});

const cloneTenant = async (data: TenantFormData): Promise<TenantModel> => {
  const tenantsClient = getClient();
  const created = await tenantsClient.cloneTenant(
    new client.TenantCloneApiDto({
      sourceTenantId: Number(data.cloneFromTenantId),
      tenantKey: data.tenantKey,
      displayName: data.displayName,
      connectionSecrets: mapConnectionSecretsToRequest(data.connectionSecrets),
      redirectUrl: data.redirectUrl || undefined,
      logoUrl: data.logoUrl || undefined,
      isActive: data.isActive,
    })
  );

  return mapTenant(created);
};

export const useUploadTenantLogo = () => {
  return useMutation(
    (input: { tenantKey: string; file: File }) =>
      uploadTenantLogo(input.tenantKey, input.file)
  );
};

export const getTenants = async (
  search: string,
  pageIndex: number,
  pageSize: number
): Promise<TenantData> => {
  const tenantsClient = getClient();
  const tenants = await tenantsClient.getTenants(search || null);

  const items = (tenants ?? []).map(mapTenant);
  const start = pageIndex * pageSize;
  const pagedItems = items.slice(start, start + pageSize);

  return {
    items: pagedItems,
    totalCount: items.length,
  };
};

export const getTenant = async (id: number): Promise<TenantFormData> => {
  const tenantsClient = getClient();
  const tenant = await tenantsClient.getTenant(id);

  return {
    id: tenant.id ?? 0,
    cloneFromTenantId: "none",
    tenantKey: tenant.tenantKey ?? "",
    displayName: tenant.displayName ?? "",
    connectionSecrets: mapConnectionSecretsToForm(tenant.connectionSecrets ?? {}),
    redirectUrl: tenant.redirectUrl ?? "",
    logoUrl: tenant.logoUrl ?? "",
    isActive: tenant.isActive ?? true,
  };
};

export const createTenant = async (
  data: TenantFormData
):     Promise<TenantModel> => {
  if (data.cloneFromTenantId && data.cloneFromTenantId !== "none") {
    return cloneTenant(data);
  }

  const tenantsClient = getClient();
  const created = await tenantsClient.createTenant(
    new client.TenantCreateApiDto({
      tenantKey: data.tenantKey,
      displayName: data.displayName,
      connectionSecrets: mapConnectionSecretsToRequest(data.connectionSecrets),
      connectionStringSecretName:
        data.connectionSecrets[0]?.value.trim() || undefined,
      redirectUrl: data.redirectUrl || undefined,
      logoUrl: data.logoUrl || undefined,
      isActive: data.isActive,
    })
  );

  return mapTenant(created);
};

export const updateTenant = async (
  id: number,
  data: TenantFormData
): Promise<TenantModel> => {
  const tenantsClient = getClient();
  const updated = await tenantsClient.updateTenant(
    id,
    new client.TenantUpdateApiDto({
      displayName: data.displayName,
      connectionSecrets: mapConnectionSecretsToRequest(data.connectionSecrets),
      connectionStringSecretName:
        data.connectionSecrets[0]?.value.trim() || undefined,
      redirectUrl: data.redirectUrl || undefined,
      logoUrl: data.logoUrl || undefined,
      isActive: data.isActive,
    })
  );

  return mapTenant(updated);
};

export const useCreateTenant = () => {
  const queryClient = useQueryClient();

  return useMutation((data: TenantFormData) => createTenant(data), {
    onSuccess: () => {
      queryClient.invalidateQueries(queryKeys.tenants);
      queryClient.invalidateQueries(queryKeys.tenant);
    },
  });
};

export const useUpdateTenant = () => {
  const queryClient = useQueryClient();

  return useMutation(
    (input: { id: number; data: TenantFormData }) =>
      updateTenant(input.id, input.data),
    {
      onSuccess: (_data, variables) => {
        queryClient.invalidateQueries(queryKeys.tenants);
        queryClient.invalidateQueries([queryKeys.tenant, variables.id]);
      },
    }
  );
};

export const useTenantAdmins = (
  tenantId: number,
  pageIndex: number = 0,
  pageSize: number = 10
) => {
  return useQuery(
    [queryKeys.tenantAdmins, tenantId, pageIndex, pageSize],
    async () => {
      const tenantsClient = getClient();
      const admins = await tenantsClient.getTenantAdmins(tenantId);
      const items = admins ?? [];
      const start = pageIndex * pageSize;
      const pagedItems = items.slice(start, start + pageSize);

      return {
        items: pagedItems,
        totalCount: items.length,
      };
    },
    {
      enabled: Number.isFinite(tenantId) && tenantId > 0,
      keepPreviousData: true,
    }
  );
};

export const useTenantsList = () => {
  return useQuery([queryKeys.tenants, "list"], async () => {
    const tenantsClient = getClient();
    const tenants = await tenantsClient.getTenants(null);
    return (tenants ?? []).map(mapTenant);
  });
};

export const useAssignTenantAdmin = (tenantId: number) => {
  const queryClient = useQueryClient();

  return useMutation(
    async (userId: string) => {
      const tenantsClient = getClient();
      await tenantsClient.assignTenantAdmin(
        tenantId,
        new client.TenantAdminAssignApiDto({ userId })
      );
    },
    {
      onSuccess: () => {
        queryClient.invalidateQueries([queryKeys.tenantAdmins, tenantId]);
      },
    }
  );
};

export const useUnassignTenantAdmin = (tenantId: number) => {
  const queryClient = useQueryClient();

  return useMutation(
    async (userId: string) => {
      const tenantsClient = getClient();
      await tenantsClient.unassignTenantAdmin(tenantId, userId);
    },
    {
      onSuccess: () => {
        queryClient.invalidateQueries([queryKeys.tenantAdmins, tenantId]);
      },
    }
  );
};
