import { FormRow } from "@/components/FormRow/FormRow";
import { useTranslation } from "react-i18next";

const StandardRedirectUrisForm: React.FC = () => {
    const { t } = useTranslation();

    return (
        <>
            <FormRow
                name="signInCallbackUrl"
                label={t("Client.TenantRedirects.RedirectUrlLabel")}
                description={t(
                    "Client.TenantRedirects.SharedRedirectUrlDescription"
                )}
                type="input"
            />
            <FormRow
                name="signOutCallbackUrl"
                label={t("Client.TenantRedirects.PostLogoutRedirectUrlLabel")}
                description={t(
                    "Client.TenantRedirects.SharedPostLogoutRedirectUrlDescription"
                )}
                type="input"
                includeSeparator
            />
            <FormRow
                name="corsOrigin"
                label={t("Client.TenantRedirects.CorsOriginLabel")}
                description={t("Client.TenantRedirects.SharedCorsOriginDescription")}
                type="input"
                includeSeparator
            />
        </>
    );
};

export default StandardRedirectUrisForm;
