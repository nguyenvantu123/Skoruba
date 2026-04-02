import PropertiesForm from "@/components/Properties/PropertiesForm";
import { ClientTenantRedirectPairsPropertyKey } from "@/pages/Client/Common/TenantRedirectPairs";

const ClientPropertiesTab = () => {
  return <PropertiesForm hiddenKeys={[ClientTenantRedirectPairsPropertyKey]} />;
};

export default ClientPropertiesTab;
