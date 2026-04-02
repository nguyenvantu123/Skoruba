import { useForm, SubmitHandler } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useFormState } from "@/contexts/FormContext";
import { Button } from "@/components/ui/button";
import { Form } from "@/components/ui/form";
import {
  useDirtyFormState,
  useDirtyReset,
  useTrackErrorState,
} from "@/components/hooks/useTrackDirtyState";
import { Trans, useTranslation } from "react-i18next";
import { urlValidationSchema } from "../../Common/UrlListValidatorSchema";
import { useClientWizard } from "@/contexts/ClientWizardContext";
import { TFunction } from "i18next";
import { Tip } from "@/components/Tip/Tip";
import StandardRedirectUrisForm from "../../Edit/Tabs/Uris/StandardRedirectUrisForm";
import TenantRedirectPairsForm from "../../Edit/Tabs/Uris/TenantRedirectPairsForm";
import { BasicsFormData } from "./ClientBasicsStep";

const optionalUrlSchema = (t: TFunction) =>
  z.union([z.literal(""), urlValidationSchema(t)]);

const tenantRedirectPairSchema = (t: TFunction) =>
  z.object({
    tenantKey: z.string(),
    signInCallbackUrl: optionalUrlSchema(t),
    signOutCallbackUrl: optionalUrlSchema(t),
    corsOrigin: optionalUrlSchema(t),
  });

const formSchema = (t: TFunction, useTenantRedirectPairs: boolean) =>
  z.object({
    signInCallbackUrl: optionalUrlSchema(t).optional(),
    signOutCallbackUrl: optionalUrlSchema(t).optional(),
    corsOrigin: optionalUrlSchema(t).optional(),
    redirectUris: z.array(urlValidationSchema(t)).optional(),
    postLogoutRedirectUris: z.array(urlValidationSchema(t)).optional(),
    allowedCorsOrigins: z.array(z.string()).optional(),
    tenantRedirectPairs: z.array(tenantRedirectPairSchema(t)).optional(),
  })
    .superRefine((data, ctx) => {
      if (useTenantRedirectPairs) {
        const hasTenantSignInCallback = (data.tenantRedirectPairs ?? []).some(
          (pair) => pair.signInCallbackUrl?.trim()
        );

        if (!hasTenantSignInCallback) {
          ctx.addIssue({
            code: z.ZodIssueCode.custom,
            path: ["tenantRedirectPairs"],
            message: t("Client.Wizard.Validation.TenantRedirectUrisRequired"),
          });
        }

        return;
      }

      if (!data.signInCallbackUrl?.trim()) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          path: ["signInCallbackUrl"],
          message: t("Client.Wizard.Validation.RedirectUrisRequired"),
        });
      }
    });

export type UrisFormData = z.infer<ReturnType<typeof formSchema>>;

const defaultValues = {
  signInCallbackUrl: "",
  signOutCallbackUrl: "",
  corsOrigin: "",
  redirectUris: [],
  postLogoutRedirectUris: [],
  allowedCorsOrigins: [],
  tenantRedirectPairs: [],
};

export const ClientUrisStep = () => {
  const { onHandleNext, setFormData, formData, onHandleBack } =
    useFormState<UrisFormData & Pick<BasicsFormData, "useTenantRedirectPairs">>();

  const { onValidation } = useClientWizard();

  const { t } = useTranslation();
  const useTenantRedirectPairs = formData.useTenantRedirectPairs ?? false;

  const form = useForm<UrisFormData>({
    defaultValues,
    resolver: zodResolver(formSchema(t, useTenantRedirectPairs)),
    mode: "onChange",
  });

  useDirtyReset(form, formData, defaultValues);
  useTrackErrorState(onValidation, form.formState.errors, form.getValues());
  useDirtyFormState(form, "uris");

  const onSubmit: SubmitHandler<UrisFormData> = (data) => {
    setFormData((prev) => ({ ...prev, ...data }));
    onHandleNext();
  };

  return (
    <Form {...form}>
      <form onSubmit={form.handleSubmit(onSubmit)}>
        <Tip>
          <Trans
            i18nKey="Client.Tips.RedirectUris"
            components={{
              strong: <strong />,
              code: <code />,
              br: <br />,
            }}
          />
        </Tip>

        {useTenantRedirectPairs ? (
          <TenantRedirectPairsForm />
        ) : (
          <StandardRedirectUrisForm />
        )}
        <div className="flex justify-between mt-4">
          <Button onClick={onHandleBack} variant="outline">
            {t("Actions.Back")}
          </Button>
          <Button type="submit">{t("Actions.Next")}</Button>
        </div>
      </form>
    </Form>
  );
};
