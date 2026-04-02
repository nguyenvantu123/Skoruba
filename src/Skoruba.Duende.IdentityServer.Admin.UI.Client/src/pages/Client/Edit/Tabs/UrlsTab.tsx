import { CardWrapper } from "@/components/CardWrapper/CardWrapper";
import { Globe } from "lucide-react";
import { useEffect } from "react";
import { useFormContext, useWatch } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { ClientEditFormData } from "../../ClientSchema";
import StandardRedirectUrisForm from "./Uris/StandardRedirectUrisForm";
import TenantRedirectPairsForm from "./Uris/TenantRedirectPairsForm";

const UrlsTab: React.FC = () => {
    const { t } = useTranslation();
    const { control, getValues, setValue } = useFormContext<ClientEditFormData>();
    const useTenantRedirectPairs =
        useWatch({
            control,
            name: "useTenantRedirectPairs",
        }) ?? false;
    const tenantRedirectPairs = useWatch({
        control,
        name: "tenantRedirectPairs",
    });

    useEffect(() => {
        if (useTenantRedirectPairs) {
            return;
        }

        const currentSignInCallbackUrl = getValues("signInCallbackUrl")?.trim();
        const currentSignOutCallbackUrl = getValues("signOutCallbackUrl")?.trim();
        const currentCorsOrigin = getValues("corsOrigin")?.trim();

        if (
            currentSignInCallbackUrl ||
            currentSignOutCallbackUrl ||
            currentCorsOrigin
        ) {
            return;
        }

        const firstConfiguredTenant = (tenantRedirectPairs ?? []).find(
            (pair) =>
                pair.signInCallbackUrl?.trim() ||
                pair.signOutCallbackUrl?.trim() ||
                pair.corsOrigin?.trim()
        );

        if (!firstConfiguredTenant) {
            return;
        }

        setValue(
            "signInCallbackUrl",
            firstConfiguredTenant.signInCallbackUrl ?? "",
            { shouldDirty: false }
        );
        setValue(
            "signOutCallbackUrl",
            firstConfiguredTenant.signOutCallbackUrl ?? "",
            { shouldDirty: false }
        );
        setValue("corsOrigin", firstConfiguredTenant.corsOrigin ?? "", {
            shouldDirty: false,
        });
    }, [getValues, setValue, tenantRedirectPairs, useTenantRedirectPairs]);

    return (
        <CardWrapper
            title={t("Client.Tabs.Urls")}
            description={t("Client.Tabs.UrlsDescription")}
            icon={Globe}
        >
            <div className="space-y-6">
                {useTenantRedirectPairs ? (
                    <TenantRedirectPairsForm />
                ) : (
                    <StandardRedirectUrisForm />
                )}
            </div>
        </CardWrapper>
    );
};

export default UrlsTab;
