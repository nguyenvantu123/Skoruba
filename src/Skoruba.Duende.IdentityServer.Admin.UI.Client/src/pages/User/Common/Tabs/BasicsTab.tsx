import { FormRow } from "@/components/FormRow/FormRow";
import { useTranslation } from "react-i18next";
import { UserFormMode } from "../UserForm";
import { CardWrapper } from "@/components/CardWrapper/CardWrapper";
import { Info } from "lucide-react";
import { useRolesList } from "@/services/RoleService";
import { useTenantsList } from "@/services/TenantService";

type BasicsTabProps = {
    mode: UserFormMode;
};

const BasicsTab: React.FC<BasicsTabProps> = ({ mode }) => {
    const { t } = useTranslation();
    const { data: roles } = useRolesList();
    const { data: tenants } = useTenantsList();
    const roleOptions =
        roles?.map((role) => ({
            value: role.id ?? "",
            label: role.name ?? "",
        })) ?? [];
    const tenantOptions =
        tenants?.map((tenant) => ({
            value: tenant.tenantKey,
            label: `${tenant.displayName} (${tenant.tenantKey})`,
        })) ?? [];

    return (
        <CardWrapper
            title={t("User.Tabs.Basics")}
            description={t("User.Tabs.BasicsDescription")}
            icon={Info}
        >
            <FormRow
                name="userName"
                label={t("User.Section.Label.UserUserName_Label")}
                description={t("User.Section.Label.UserUserName_Info")}
                type="input"
                required
            />

            <FormRow
                name="tenantKey"
                label={t("Tenant.Section.Label.TenantKey_Label")}
                description={t("Tenant.Section.Label.TenantKey_Info")}
                type="select"
                required={mode === UserFormMode.Create}
                selectSettings={{ options: tenantOptions }}
                disabled={mode === UserFormMode.Edit}
            />

            {mode === UserFormMode.Create && (
                <FormRow
                    name="initialRoleId"
                    label={t("User.Tabs.Roles")}
                    description={t("User.Tabs.RolesDescription")}
                    type="select"
                    selectSettings={{ options: roleOptions }}
                />


            )}

            {mode === UserFormMode.Create && (<FormRow
                name="password"
                label={t("User.Tabs.Password")}
                description={t("User.Tabs.PasswordDescription")}
                type="input"
                required
                inputType="password"
            />)}

            {mode === UserFormMode.Create && (<FormRow
                name="confirmPassword"
                label={t("User.Section.Label.ConfirmPassword_Label")}
                description={t("User.Tabs.PasswordDescription")}
                type="input"
                required
                inputType="password"
            />)}

            <div
                className={
                    mode === UserFormMode.Edit
                        ? "grid grid-cols-1 md:grid-cols-2 gap-4"
                        : "grid grid-cols-1 gap-4"
                }
            >
                <FormRow
                    name="email"
                    label={t("User.Section.Label.UserEmail_Label")}
                    description={t("User.Section.Label.UserEmail_Info")}
                    type="input"
                    required
                />
                {mode === UserFormMode.Edit && (
                    <FormRow
                        name="emailConfirmed"
                        label={t("User.Section.Label.UserEmailConfirmed_Label")}
                        description={t("User.Section.Label.UserEmailConfirmed_Info")}
                        type="switch"
                    />
                )}
            </div>

            {mode === UserFormMode.Edit && (
                <>
                    <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                        <FormRow
                            name="phoneNumber"
                            label={t("User.Section.Label.UserPhoneNumber_Label")}
                            description={t("User.Section.Label.UserPhoneNumber_Info")}
                            type="input"
                        />
                        <FormRow
                            name="phoneNumberConfirmed"
                            label={t("User.Section.Label.UserPhoneNumberConfirmed_Label")}
                            description={t(
                                "User.Section.Label.UserPhoneNumberConfirmed_Info"
                            )}
                            type="switch"
                        />
                    </div>

                    <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                        <FormRow
                            name="lockoutEnabled"
                            label={t("User.Section.Label.UserLockoutEnabled_Label")}
                            description={t("User.Section.Label.UserLockoutEnabled_Info")}
                            type="switch"
                        />
                        <FormRow
                            name="twoFactorEnabled"
                            label={t("User.Section.Label.UserTwoFactorEnabled_Label")}
                            description={t("User.Section.Label.UserTwoFactorEnabled_Info")}
                            type="switch"
                        />
                    </div>

                    <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                        <FormRow
                            name="accessFailedCount"
                            label={t("User.Section.Label.UserAccessFailedCount_Label")}
                            description={t("User.Section.Label.UserAccessFailedCount_Info")}
                            type="number"
                            numberSettings={{ showFormattedTime: false }}
                        />

                        <div className="flex gap-4">
                            <FormRow
                                name="lockoutEndDate"
                                label={t("User.Section.Label.UserLockoutEnd_Label")}
                                description={t("User.Section.Label.UserLockoutEnd_Info")}
                                type="date"
                                className="w-1/2"
                            />
                            <FormRow
                                name="lockoutEndTime"
                                label={t("User.Section.Label.UserLockoutEndTime_Label")}
                                description={t("User.Section.Label.UserLockoutEndTime_Info")}
                                type="time"
                                className="w-1/2"
                            />
                        </div>
                    </div>
                </>
            )}
        </CardWrapper>
    );
};

export default BasicsTab;
