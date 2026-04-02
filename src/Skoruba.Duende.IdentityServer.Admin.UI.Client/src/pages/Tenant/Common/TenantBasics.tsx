import { useEffect, useMemo, useState } from "react";
import { CardWrapper } from "@/components/CardWrapper/CardWrapper";
import { FormRow } from "@/components/FormRow/FormRow";
import { TenantFormMode } from "./TenantForm";
import { useTranslation } from "react-i18next";
import { Info } from "lucide-react";
import TenantConnectionSecretsForm from "./TenantConnectionSecretsForm";
import { useFormContext } from "react-hook-form";
import { TenantFormData } from "./TenantSchema";
import {
  getTenant,
  useTenantsList,
  useUploadTenantLogo,
} from "@/services/TenantService";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { toast } from "@/components/ui/use-toast";

type TenantBasicsProps = {
  mode: TenantFormMode;
};

const TenantBasics: React.FC<TenantBasicsProps> = ({ mode }) => {
  const { t } = useTranslation();
  const isEdit = mode === TenantFormMode.Edit;
  const isCreate = mode === TenantFormMode.Create;
  const { watch, setValue } = useFormContext<TenantFormData>();
  const tenantKey = watch("tenantKey");
  const selectedCloneTenantId = watch("cloneFromTenantId");
  const logoUrl = watch("logoUrl");
  const { data: tenants, isLoading: isTenantsLoading } = useTenantsList();
  const uploadLogoMutation = useUploadTenantLogo();
  const [selectedLogoFile, setSelectedLogoFile] = useState<File | null>(null);

  const cloneTenantOptions = useMemo(
    () => [
      { value: "none", label: t("Tenant.Clone.None") },
      ...(tenants ?? []).map((tenant) => ({
        value: tenant.id.toString(),
        label: `${tenant.displayName} (${tenant.tenantKey})`,
      })),
    ],
    [t, tenants]
  );

  useEffect(() => {
    if (!isCreate || !selectedCloneTenantId || selectedCloneTenantId === "none") {
      return;
    }

    const cloneTenantId = Number(selectedCloneTenantId);
    if (!Number.isFinite(cloneTenantId) || cloneTenantId <= 0) {
      return;
    }

    let isCancelled = false;

    const applyClone = async () => {
      const sourceTenant = await getTenant(cloneTenantId);
      if (isCancelled) {
        return;
      }

      setValue("displayName", sourceTenant.displayName, {
        shouldDirty: true,
        shouldValidate: true,
      });
      setValue("connectionSecrets", sourceTenant.connectionSecrets, {
        shouldDirty: true,
        shouldValidate: true,
      });
      setValue("redirectUrl", sourceTenant.redirectUrl ?? "", {
        shouldDirty: true,
        shouldValidate: true,
      });
      setValue("logoUrl", sourceTenant.logoUrl ?? "", {
        shouldDirty: true,
        shouldValidate: true,
      });
      setValue("isActive", sourceTenant.isActive, {
        shouldDirty: true,
        shouldValidate: true,
      });
    };

    void applyClone();

    return () => {
      isCancelled = true;
    };
  }, [isCreate, selectedCloneTenantId, setValue]);

  const handleLogoFileChange = (event: React.ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0] ?? null;
    setSelectedLogoFile(file);
  };

  const handleUploadLogo = async () => {
    if (!tenantKey?.trim()) {
      toast({
        title: t("Tenant.LogoUpload.Errors.TenantKeyRequired"),
        variant: "destructive",
      });
      return;
    }

    if (!selectedLogoFile) {
      toast({
        title: t("Tenant.LogoUpload.Errors.FileRequired"),
        variant: "destructive",
      });
      return;
    }

    const uploaded = await uploadLogoMutation.mutateAsync({
      tenantKey: tenantKey.trim(),
      file: selectedLogoFile,
    });

    setValue("logoUrl", uploaded.logoUrl, {
      shouldDirty: true,
      shouldValidate: true,
    });

    toast({
      title: t("Tenant.LogoUpload.Actions.Uploaded"),
    });
  };

  return (
    <CardWrapper
      title={t("Tenant.Tabs.Basics")}
      description={t("Tenant.Tabs.BasicsDescription")}
      icon={Info}
    >
      {isCreate && (
        <FormRow
          name="cloneFromTenantId"
          label={t("Tenant.Section.Label.CloneFromTenant_Label")}
          description={t("Tenant.Section.Label.CloneFromTenant_Info")}
          type="select"
          selectSettings={{ options: cloneTenantOptions }}
          disabled={isTenantsLoading}
        />
      )}

      <FormRow
        name="tenantKey"
        label={t("Tenant.Section.Label.TenantKey_Label")}
        description={t("Tenant.Section.Label.TenantKey_Info")}
        placeholder={t("Tenant.Section.Label.TenantKey_Label")}
        type="input"
        required
        disabled={isEdit}
      />

      <FormRow
        name="displayName"
        label={t("Tenant.Section.Label.DisplayName_Label")}
        description={t("Tenant.Section.Label.DisplayName_Info")}
        placeholder={t("Tenant.Section.Label.DisplayName_Label")}
        type="input"
        required
      />

      <div className="mt-4">
        <div className="flex items-center gap-2">
          <h4 className="text-sm font-medium">
            {t("Tenant.Section.Label.ConnectionSecrets_Label")}
          </h4>
        </div>
        <p className="text-sm text-muted-foreground mt-1">
          {t("Tenant.Section.Label.ConnectionSecrets_Info")}
        </p>
        <TenantConnectionSecretsForm />
      </div>


      <FormRow
        name="logoUrl"
        label={t("Tenant.Section.Label.LogoUrl_Label")}
        description={t("Tenant.Section.Label.LogoUrl_Info")}
        placeholder="https://tenant.example.com/logo.png"
        type="input"
      />

      <div className="mt-4 space-y-2">
        <h4 className="text-sm font-medium">
          {t("Tenant.LogoUpload.Title")}
        </h4>
        <p className="text-sm text-muted-foreground">
          {t("Tenant.LogoUpload.Description")}
        </p>
        <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
          <Input
            type="file"
            accept=".png,.jpg,.jpeg,.webp,image/png,image/jpeg,image/webp"
            onChange={handleLogoFileChange}
            disabled={uploadLogoMutation.isLoading}
            className="max-w-md"
          />
          <Button
            type="button"
            variant="outline"
            onClick={() => void handleUploadLogo()}
            disabled={uploadLogoMutation.isLoading}
          >
            {t("Tenant.LogoUpload.Actions.Upload")}
          </Button>
        </div>
        {logoUrl && (
          <div className="h-16 w-16 overflow-hidden rounded-md border border-border bg-background">
            <img
              src={logoUrl}
              alt={t("Tenant.LogoUpload.PreviewAlt")}
              className="h-full w-full object-cover"
            />
          </div>
        )}
      </div>

      <FormRow
        name="isActive"
        label={t("Tenant.Section.Label.IsActive_Label")}
        description={t("Tenant.Section.Label.IsActive_Info")}
        type="switch"
      />
    </CardWrapper>
  );
};

export default TenantBasics;
