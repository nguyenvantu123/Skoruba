import { useEffect, useMemo, useState } from "react";
import { z } from "zod";
import { useTranslation } from "react-i18next";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";

type TenantConnectionSecretModalProps = {
  isOpen: boolean;
  initialValue?: { key: string; value: string };
  existingKeys: string[];
  editingOriginalKey?: string;
  onClose: () => void;
  onSubmit: (data: { key: string; value: string }) => void;
};

const TenantConnectionSecretModal = ({
  isOpen,
  initialValue,
  existingKeys,
  editingOriginalKey,
  onClose,
  onSubmit,
}: TenantConnectionSecretModalProps) => {
  const { t } = useTranslation();
  const normalizedExistingKeys = useMemo(
    () =>
      new Set(
        existingKeys
          .filter((key) => key.trim().length > 0)
          .map((key) => key.trim().toLowerCase())
          .filter(
            (key) =>
              key !== (editingOriginalKey ?? "").trim().toLowerCase()
          )
      ),
    [editingOriginalKey, existingKeys]
  );

  const schema = z
    .object({
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
    })
    .superRefine((data, ctx) => {
      if (normalizedExistingKeys.has(data.key.trim().toLowerCase())) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          path: ["key"],
          message: t("Tenant.ConnectionSecrets.Validation.DuplicateService"),
        });
      }
    });

  const [formData, setFormData] = useState({ key: "", value: "" });
  const [errors, setErrors] = useState<{ key?: string; value?: string }>({});

  useEffect(() => {
    if (isOpen) {
      setFormData({
        key: initialValue?.key ?? "",
        value: initialValue?.value ?? "",
      });
      setErrors({});
    }
  }, [initialValue, isOpen]);

  const handleInputChange =
    (field: keyof typeof formData) =>
    (e: React.ChangeEvent<HTMLInputElement>) => {
      setFormData((prev) => ({ ...prev, [field]: e.target.value }));
      setErrors((prev) => ({ ...prev, [field]: undefined }));
    };

  const handleSubmit = () => {
    const parsed = schema.safeParse(formData);
    if (!parsed.success) {
      const validationErrors = parsed.error.flatten().fieldErrors;
      setErrors({
        key: validationErrors.key?.[0],
        value: validationErrors.value?.[0],
      });
      return;
    }

    onSubmit(parsed.data);
  };

  return (
    <Dialog open={isOpen} onOpenChange={(open) => !open && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>
            {initialValue
              ? t("Tenant.ConnectionSecrets.Actions.Edit")
              : t("Tenant.ConnectionSecrets.Actions.Add")}
          </DialogTitle>
        </DialogHeader>

        <div className="space-y-4">
          <div>
            <Label htmlFor="service-key" className={errors.key ? "text-destructive" : ""}>
              {t("Tenant.ConnectionSecrets.Fields.Service")}
            </Label>
            <Input
              id="service-key"
              value={formData.key}
              onChange={handleInputChange("key")}
              placeholder={t("Tenant.ConnectionSecrets.Placeholders.Service")}
              autoComplete="off"
            />
            {errors.key && <p className="text-sm text-destructive mt-1">{errors.key}</p>}
          </div>

          <div>
            <Label htmlFor="secret-name" className={errors.value ? "text-destructive" : ""}>
              {t("Tenant.ConnectionSecrets.Fields.Secret")}
            </Label>
            <Input
              id="secret-name"
              value={formData.value}
              onChange={handleInputChange("value")}
              placeholder={t("Tenant.ConnectionSecrets.Placeholders.Secret")}
              autoComplete="off"
            />
            {errors.value && <p className="text-sm text-destructive mt-1">{errors.value}</p>}
          </div>

          <Button type="button" onClick={handleSubmit}>
            {initialValue ? t("Actions.Save") : t("Tenant.ConnectionSecrets.Actions.Add")}
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  );
};

export default TenantConnectionSecretModal;
