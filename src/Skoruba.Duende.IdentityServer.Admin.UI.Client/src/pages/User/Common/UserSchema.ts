import { z } from "zod";
import { t } from "i18next";

const baseUserSchema = z.object({
    userName: z.string().min(
        1,
        t("Validation.FieldRequired", {
            field: t("User.Section.Label.UserUserName_Label"),
        })
    ),
    password: z.string(),
    confirmPassword: z.string(),
    email: z.string().email(t("Validation.InvalidEmail")),
    emailConfirmed: z.boolean().optional(),
    phoneNumber: z.string().optional(),
    phoneNumberConfirmed: z.boolean().optional(),
    lockoutEnabled: z.boolean().optional(),
    twoFactorEnabled: z.boolean().optional(),
    accessFailedCount: z.number().optional(),
    lockoutEndDate: z.date().nullable().optional(),
    lockoutEndTime: z.string().nullable().optional(),
    tenantKey: z.string().optional(),
    initialRoleId: z.string().optional(),
});

const passwordRule = z
    .string()
    .min(6, "Password must be at least 6 characters.")
    .regex(/[A-Z]/, "Password must contain at least 1 uppercase letter.")
    .regex(/[0-9]/, "Password must contain at least 1 number.")
    .regex(/[^A-Za-z0-9]/, "Password must contain at least 1 special character.");

export const createUserFormSchema = baseUserSchema.extend({
    tenantKey: z.string().min(
        1,
        t("Validation.FieldRequired", {
            field: t("Tenant.Section.Label.TenantKey_Label"),
        })
    ),
    password: passwordRule,
    confirmPassword: passwordRule,
}).refine((data) => data.password === data.confirmPassword, {
    message: "Password and confirm password do not match.",
    path: ["confirmPassword"],
});

export const editUserFormSchema = baseUserSchema.extend({
    tenantKey: z.string().optional(),
});

export type UserFormData = z.infer<typeof baseUserSchema>;

export const defaultUserFormData: UserFormData = {
    userName: "",
    email: "",
    emailConfirmed: false,
    phoneNumber: "",
    phoneNumberConfirmed: false,
    lockoutEnabled: false,
    twoFactorEnabled: false,
    accessFailedCount: 0,
    lockoutEndDate: null,
    lockoutEndTime: null,
    tenantKey: "",
    initialRoleId: "",
    password: "",
    confirmPassword: ""
};
