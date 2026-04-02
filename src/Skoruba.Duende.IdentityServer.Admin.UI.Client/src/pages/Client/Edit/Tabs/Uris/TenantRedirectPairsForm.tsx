import {
    FormControl,
    FormDescription,
    FormField,
    FormItem,
    FormLabel,
    FormMessage,
} from "@/components/ui/form";
import { Input } from "@/components/ui/input";
import { useTenantsList } from "@/services/TenantService";
import { useEffect, useMemo } from "react";
import { useFormContext, useWatch } from "react-hook-form";
import { useTranslation } from "react-i18next";

type TenantRedirectPairValue = {
    tenantKey: string;
    signInCallbackUrl: string;
    signOutCallbackUrl: string;
    corsOrigin: string;
};

type TenantRedirectPairsFormValues = {
    tenantRedirectPairs?: TenantRedirectPairValue[];
    redirectUris?: string[];
    postLogoutRedirectUris?: string[];
    allowedCorsOrigins?: string[];
};

const emptyPair = (tenantKey: string): TenantRedirectPairValue => ({
    tenantKey,
    signInCallbackUrl: "",
    signOutCallbackUrl: "",
    corsOrigin: "",
});

const normalizeTenantKey = (tenantKey: string) => tenantKey.trim().toLowerCase();

const matchesTenantHost = (value: string, tenantKey: string) => {
    try {
        const url = new URL(value);
        const parts = url.hostname.split(".");
        return parts.length > 0 && parts[0].toLowerCase() === normalizeTenantKey(tenantKey);
    } catch {
        return false;
    }
};

const findTenantMatch = (values: string[] | undefined, tenantKey: string) =>
    (values ?? []).find((value) => matchesTenantHost(value, tenantKey)) ?? "";

const TenantRedirectPairsForm: React.FC = () => {
    const { t } = useTranslation();
    const { control, setValue } = useFormContext<TenantRedirectPairsFormValues>();
    const tenantRedirectPairs = useWatch({
        control,
        name: "tenantRedirectPairs",
    });
    const redirectUris = useWatch({
        control,
        name: "redirectUris",
    });
    const postLogoutRedirectUris = useWatch({
        control,
        name: "postLogoutRedirectUris",
    });
    const allowedCorsOrigins = useWatch({
        control,
        name: "allowedCorsOrigins",
    });
    const tenantsQuery = useTenantsList();

    const currentPairsMap = useMemo(
        () =>
            new Map(
                (tenantRedirectPairs ?? []).map((pair) => [
                    pair.tenantKey.trim().toLowerCase(),
                    {
                        signInCallbackUrl: pair.signInCallbackUrl ?? "",
                        signOutCallbackUrl: pair.signOutCallbackUrl ?? "",
                        corsOrigin: pair.corsOrigin ?? "",
                    },
                ])
            ),
        [tenantRedirectPairs]
    );

    useEffect(() => {
        if (!tenantsQuery.data) {
            return;
        }

        const nextPairs = tenantsQuery.data.map((tenant) => {
            const existingPair =
                currentPairsMap.get(tenant.tenantKey.trim().toLowerCase()) ??
                emptyPair(tenant.tenantKey);

            return {
                tenantKey: tenant.tenantKey,
                signInCallbackUrl:
                    existingPair.signInCallbackUrl ||
                    findTenantMatch(redirectUris, tenant.tenantKey),
                signOutCallbackUrl:
                    existingPair.signOutCallbackUrl ||
                    findTenantMatch(postLogoutRedirectUris, tenant.tenantKey),
                corsOrigin:
                    existingPair.corsOrigin ||
                    findTenantMatch(allowedCorsOrigins, tenant.tenantKey),
            };
        });

        const currentSignature = JSON.stringify(
            (tenantRedirectPairs ?? []).map((pair) => ({
                tenantKey: pair.tenantKey,
                signInCallbackUrl: pair.signInCallbackUrl ?? "",
                signOutCallbackUrl: pair.signOutCallbackUrl ?? "",
                corsOrigin: pair.corsOrigin ?? "",
            }))
        );
        const nextSignature = JSON.stringify(nextPairs);

        if (currentSignature === nextSignature) {
            return;
        }

        setValue("tenantRedirectPairs", nextPairs, { shouldDirty: false });
    }, [
        allowedCorsOrigins,
        currentPairsMap,
        postLogoutRedirectUris,
        redirectUris,
        setValue,
        tenantRedirectPairs,
        tenantsQuery.data,
    ]);

    if (tenantsQuery.isLoading) {
        return (
            <div className="rounded-lg border border-dashed p-4 text-sm text-muted-foreground">
                {t("Components.Loading.Loading")}
            </div>
        );
    }

    if (!tenantsQuery.data || tenantsQuery.data.length === 0) {
        return (
            <div className="rounded-lg border border-dashed p-4 text-sm text-muted-foreground">
                {t("Client.TenantRedirects.Empty")}
            </div>
        );
    }

    return (
        <div className="space-y-4">
            <div className="space-y-1">
                <h4 className="text-sm font-medium">
                    {t("Client.TenantRedirects.Title")}
                </h4>
                <p className="text-sm text-muted-foreground">
                    {t("Client.TenantRedirects.Description")}
                </p>
            </div>

            <div className="space-y-4">
                {tenantsQuery.data.map((tenant, index) => (
                    <div
                        key={tenant.tenantKey}
                        className="rounded-lg border p-4 lg:grid lg:grid-cols-[220px_minmax(0,1fr)] lg:gap-6"
                    >
                        <div className="space-y-1 border-b border-border pb-4 lg:border-b-0 lg:border-r lg:pb-0 lg:pr-6">
                            <div className="text-sm font-medium">{tenant.displayName}</div>
                            <div className="text-xs text-muted-foreground">
                                {tenant.tenantKey}
                            </div>
                        </div>

                        <div className="mt-4 space-y-4 lg:mt-0">
                            <FormField
                                control={control}
                                name={`tenantRedirectPairs.${index}.signInCallbackUrl`}
                                render={({ field }) => (
                                    <FormItem>
                                        <FormLabel>
                                            {t("Client.TenantRedirects.RedirectUrlLabel")}
                                        </FormLabel>
                                        <FormControl>
                                            <Input
                                                {...field}
                                                autoComplete="off"
                                            />
                                        </FormControl>
                                        <FormDescription>
                                            {t("Client.TenantRedirects.RedirectUrlDescription")}
                                        </FormDescription>
                                        <FormMessage />
                                    </FormItem>
                                )}
                            />

                            <FormField
                                control={control}
                                name={`tenantRedirectPairs.${index}.signOutCallbackUrl`}
                                render={({ field }) => (
                                    <FormItem>
                                        <FormLabel>
                                            {t("Client.TenantRedirects.PostLogoutRedirectUrlLabel")}
                                        </FormLabel>
                                        <FormControl>
                                            <Input
                                                {...field}
                                                autoComplete="off"
                                            />
                                        </FormControl>
                                        <FormDescription>
                                            {t(
                                                "Client.TenantRedirects.PostLogoutRedirectUrlDescription"
                                            )}
                                        </FormDescription>
                                        <FormMessage />
                                    </FormItem>
                                )}
                            />

                            <FormField
                                control={control}
                                name={`tenantRedirectPairs.${index}.corsOrigin`}
                                render={({ field }) => (
                                    <FormItem>
                                        <FormLabel>
                                            {t("Client.TenantRedirects.CorsOriginLabel")}
                                        </FormLabel>
                                        <FormControl>
                                            <Input
                                                {...field}
                                                autoComplete="off"
                                            />
                                        </FormControl>
                                        <FormDescription>
                                            {t("Client.TenantRedirects.CorsOriginDescription")}
                                        </FormDescription>
                                        <FormMessage />
                                    </FormItem>
                                )}
                            />
                        </div>
                    </div>
                ))}
            </div>
        </div>
    );
};

export default TenantRedirectPairsForm;
