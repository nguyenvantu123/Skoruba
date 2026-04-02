export type TenantRedirectPair = {
  tenantKey: string;
  redirectUrl: string;
};

export const ClientTenantRedirectPairsPropertyKey =
  "skoruba_tenant_redirect_pairs";

const normalizePair = (
  pair: Partial<TenantRedirectPair> | null | undefined
): TenantRedirectPair | null => {
  const tenantKey = pair?.tenantKey?.trim() ?? "";
  const redirectUrl = pair?.redirectUrl?.trim() ?? "";

  if (!tenantKey || !redirectUrl) {
    return null;
  }

  return {
    tenantKey,
    redirectUrl,
  };
};

export const normalizeTenantRedirectPairs = (
  pairs?: Array<Partial<TenantRedirectPair> | null | undefined> | null
): TenantRedirectPair[] => {
  const seen = new Set<string>();

  return (pairs ?? [])
    .map(normalizePair)
    .filter((pair): pair is TenantRedirectPair => pair !== null)
    .filter((pair) => {
      const key = `${pair.tenantKey.toLowerCase()}|${pair.redirectUrl.toLowerCase()}`;
      if (seen.has(key)) {
        return false;
      }

      seen.add(key);
      return true;
    });
};

export const parseTenantRedirectPairsProperty = (
  rawValue?: string | null
): TenantRedirectPair[] => {
  if (!rawValue) {
    return [];
  }

  try {
    const parsedValue = JSON.parse(rawValue);
    if (!Array.isArray(parsedValue)) {
      return [];
    }

    return normalizeTenantRedirectPairs(parsedValue);
  } catch {
    return [];
  }
};

export const serializeTenantRedirectPairs = (
  pairs?: Array<Partial<TenantRedirectPair> | null | undefined> | null
): string | undefined => {
  const normalizedPairs = normalizeTenantRedirectPairs(pairs);
  if (normalizedPairs.length === 0) {
    return undefined;
  }

  return JSON.stringify(normalizedPairs);
};
