import { useParams } from "react-router-dom";
import { useTranslation } from "react-i18next";
import Page from "@/components/Page/Page";
import Loading from "@/components/Loading/Loading";
import { Breadcrumbs } from "@/components/Breadcrumbs/Breadcrumbs";
import { TenantsUrl } from "@/routing/Urls";
import TenantForm, { TenantFormMode } from "../Common/TenantForm";
import { useQuery } from "react-query";
import { getTenant } from "@/services/TenantService";
import { queryKeys } from "@/services/QueryKeys";
import { Building2 } from "lucide-react";
import TenantAdminsCard from "../Common/TenantAdminsCard";

const TenantEdit = () => {
  const { tenantId } = useParams<{ tenantId: string }>();
  const { t } = useTranslation();

  const id = Number(tenantId);

  const { data, isLoading } = useQuery(
    [queryKeys.tenant, id],
    () => getTenant(id),
    { enabled: Number.isFinite(id) }
  );

  if (isLoading || !data) {
    return <Loading fullscreen />;
  }

  return (
    <Page
      title={t("Tenant.Edit.PageTitle")}
      icon={Building2}
      accentKind="management"
      breadcrumb={
        <Breadcrumbs
          items={[
            { url: TenantsUrl, name: t("Tenants.PageTitle") },
            { name: data.displayName || data.tenantKey },
          ]}
        />
      }
    >
      <TenantForm
        mode={TenantFormMode.Edit}
        tenantId={id}
        defaultValues={data}
      />

      <div className="mt-6">
        <TenantAdminsCard tenantId={id} />
      </div>
    </Page>
  );
};

export default TenantEdit;
