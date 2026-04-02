import { client } from "@skoruba/duende.identityserver.admin.api.client";

export type PublicTenant = client.PublicTenantApiDto;

const publicTenantApiBaseUrl = "https://localhost:7397";
const tenantsClient = new client.TenantsClient(publicTenantApiBaseUrl);

export async function searchTenants(search: string): Promise<PublicTenant[]> {
    const normalized = search.trim();

    if (normalized.length < 2) {
        return [];
    }

    return tenantsClient.getPublicTenants(normalized);
}

// Example usage in a debounced search box:
// const tenants = await searchTenants("branch");
// tenants.map(x => x.displayName);
