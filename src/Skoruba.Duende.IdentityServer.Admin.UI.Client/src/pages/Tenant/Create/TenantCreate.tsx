import { useTranslation } from "react-i18next";
import Page from "@/components/Page/Page";
import { Breadcrumbs } from "@/components/Breadcrumbs/Breadcrumbs";
import { TenantsUrl } from "@/routing/Urls";
import TenantForm, { TenantFormMode } from "../Common/TenantForm";
import { defaultTenantFormData } from "../Common/TenantSchema";
import { Building2 } from "lucide-react";

const TenantCreate = () => {
  const { t } = useTranslation();

  return (
    <Page
      title={t("Tenant.Create.PageTitle")}
      icon={Building2}
      accentKind="management"
      breadcrumb={
        <Breadcrumbs
          items={[
            { url: TenantsUrl, name: t("Tenants.PageTitle") },
            { name: t("Tenant.Create.PageTitle") },
          ]}
        />
      }
    >
      <TenantForm
        mode={TenantFormMode.Create}
        defaultValues={defaultTenantFormData}
      />
    </Page>
  );
};

export default TenantCreate;
