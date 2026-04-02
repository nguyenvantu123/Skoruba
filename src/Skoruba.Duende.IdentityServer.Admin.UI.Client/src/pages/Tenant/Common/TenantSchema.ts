import { z } from "zod";
import { t } from "i18next";
import { urlValidationSchema } from "@/pages/Client/Common/UrlListValidatorSchema";

const connectionSecretSchema = z.object({
  key: z.string().trim().min(
    1,
    t("Validation.FieldRequired", {
      field: t("Tenant.ConnectionSecrets.Fields.Service"),
    })
  ),
  value: z.string().trim().min(
    1,
    t("Validation.FieldRequired", {
      field: t("Tenant.ConnectionSecrets.Fields.Secret"),
    })
  ),
});

export const tenantSchema = z.object({
  id: z.number().optional(),
  cloneFromTenantId: z.string().default("none"),
  tenantKey: z.string().min(
    1,
    t("Validation.FieldRequired", {
      field: t("Tenant.Section.Label.TenantKey_Label"),
    })
  ),
  displayName: z.string().min(
    1,
    t("Validation.FieldRequired", {
      field: t("Tenant.Section.Label.DisplayName_Label"),
    })
  ),
  connectionSecrets: z
    .array(connectionSecretSchema)
    .min(
      1,
      t("Validation.FieldRequired", {
        field: t("Tenant.Section.Label.ConnectionSecrets_Label"),
      })
    )
    .superRefine((items, ctx) => {
      const seen = new Set<string>();
      items.forEach((item, index) => {
        const normalizedKey = item.key.trim().toLowerCase();
        if (seen.has(normalizedKey)) {
          ctx.addIssue({
            code: z.ZodIssueCode.custom,
            path: [index, "key"],
            message: t("Tenant.ConnectionSecrets.Validation.DuplicateService"),
          });
          return;
        }

        seen.add(normalizedKey);
      });
    }),
  redirectUrl: urlValidationSchema(t).or(z.literal("")).optional(),
  logoUrl: urlValidationSchema(t).or(z.literal("")).optional(),
  isActive: z.boolean().default(true),
});

export type TenantFormData = z.infer<typeof tenantSchema>;

export const defaultTenantFormData: TenantFormData = {
  cloneFromTenantId: "none",
  tenantKey: "",
  displayName: "",
  connectionSecrets: [],
  redirectUrl: "",
  logoUrl: "",
  isActive: true,
};
