import { CardWrapper } from "@/components/CardWrapper/CardWrapper";
import { Building2 } from "lucide-react";
import { useTranslation } from "react-i18next";
import TenantRedirectPairsForm from "./Uris/TenantRedirectPairsForm";

const TenantTab: React.FC = () => {
  const { t } = useTranslation();

  return (
    <CardWrapper
      title={t("Client.Tabs.Tenant")}
      description={t("Client.Tabs.TenantDescription")}
      icon={Building2}
    >
      <TenantRedirectPairsForm />
    </CardWrapper>
  );
};

export default TenantTab;
