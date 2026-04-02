import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { Form } from "@/components/ui/form";
import { Button } from "@/components/ui/button";
import { useTranslation } from "react-i18next";
import { toast } from "@/components/ui/use-toast";
import Hoorey from "@/components/Hoorey/Hoorey";
import {
  useConfirmUnsavedChanges,
  useNavigateWithBlocker,
} from "@/hooks/useConfirmUnsavedChanges";
import { TenantEditUrl, TenantsUrl } from "@/routing/Urls";
import { useCreateTenant, useUpdateTenant } from "@/services/TenantService";
import TenantBasics from "./TenantBasics";
import { TenantFormData, tenantSchema } from "./TenantSchema";

export enum TenantFormMode {
  Create = "create",
  Edit = "edit",
}

type TenantFormProps = {
  mode: TenantFormMode;
  tenantId?: number;
  defaultValues: TenantFormData;
};

const TenantForm: React.FC<TenantFormProps> = ({
  mode,
  tenantId,
  defaultValues,
}) => {
  const { t } = useTranslation();

  const form = useForm<TenantFormData>({
    resolver: zodResolver(tenantSchema),
    defaultValues,
  });

  const navigate = useNavigateWithBlocker(form);
  const { DialogCmp } = useConfirmUnsavedChanges(form.formState.isDirty);

  const createMutation = useCreateTenant();
  const updateMutation = useUpdateTenant();

  const handleSubmit = async (data: TenantFormData) => {
    if (mode === TenantFormMode.Create) {
      const created = await createMutation.mutateAsync(data);
      toast({
        title: <Hoorey />,
        description: t("Tenant.Actions.Created"),
      });
      if (created?.id) {
        navigate(TenantEditUrl.replace(":tenantId", created.id.toString()));
      } else {
        navigate(TenantsUrl);
      }
      return;
    }

    if (!tenantId) return;

    await updateMutation.mutateAsync({ id: tenantId, data });

    toast({
      title: <Hoorey />,
      description: t("Tenant.Actions.Updated"),
    });
    navigate(TenantsUrl);
  };

  const handleInvalidSubmit = () => {
    toast({
      title: "Validation error",
      description: "Please fix the tenant connection secret errors before saving.",
      variant: "destructive",
    });
  };

  return (
    <>
      <Form {...form}>
        <form onSubmit={form.handleSubmit(handleSubmit, handleInvalidSubmit)}>
          <TenantBasics mode={mode} />
          <div className="flex gap-4 justify-start mt-4">
            <Button type="submit">
              {mode === TenantFormMode.Create
                ? t("Actions.Create")
                : t("Actions.Save")}
            </Button>
          </div>
        </form>
      </Form>
      {DialogCmp}
    </>
  );
};

export default TenantForm;

