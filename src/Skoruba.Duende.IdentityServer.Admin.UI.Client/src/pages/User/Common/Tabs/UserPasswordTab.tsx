import { useState } from "react";
import { useTranslation } from "react-i18next";
import { KeyRound } from "lucide-react";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Button } from "@/components/ui/button";
import { toast } from "@/components/ui/use-toast";
import { CardWrapper } from "@/components/CardWrapper/CardWrapper";
import Loading from "@/components/Loading/Loading";
import { useMutation } from "react-query";
import { Roles, hasRole } from "@/constants/roles";
import { setUserPassword, useUserRoles } from "@/services/UserServices";

type Props = {
  userId: string;
};

const UserPasswordTab: React.FC<Props> = ({ userId }) => {
  const { t } = useTranslation();
  const [password, setPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");

  const { data: rolesData, isLoading } = useUserRoles(userId, 0, 200);
  const targetRoleNames = (rolesData?.roles ?? [])
    .map((x) => x.name ?? "")
    .filter((x) => !!x);
  const isTenantAdmin = hasRole(targetRoleNames, Roles.TenantAdmin);

  const changePasswordMutation = useMutation(
    async () => setUserPassword(userId, password, confirmPassword),
    {
      onSuccess: () => {
        setPassword("");
        setConfirmPassword("");
        toast({
          title: t("Actions.Hooray"),
          description: t("User.Actions.PasswordUpdated"),
        });
      },
    }
  );

  if (isLoading) {
    return <Loading />;
  }

  if (!isTenantAdmin) {
    return (
      <CardWrapper
        title={t("User.Tabs.Password")}
        description={t("User.Tabs.PasswordDescription")}
        icon={KeyRound}
      >
        <p className="text-sm text-muted-foreground">
          {t("User.Password.NotTenantAdmin")}
        </p>
      </CardWrapper>
    );
  }

  const onSubmit = () => {
    if (!password || !confirmPassword) {
      toast({
        title: t("Errors.Failed"),
        description: t("Validation.Required"),
      });
      return;
    }

    if (password !== confirmPassword) {
      toast({
        title: t("Errors.Failed"),
        description: t("User.Password.Mismatch"),
      });
      return;
    }

    changePasswordMutation.mutate();
  };

  return (
    <CardWrapper
      title={t("User.Tabs.Password")}
      description={t("User.Tabs.PasswordDescription")}
      icon={KeyRound}
    >
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        <div className="space-y-2">
          <Label htmlFor="tenant-admin-password">
            {t("User.Section.Label.Password_Label")}
          </Label>
          <Input
            id="tenant-admin-password"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="new-password"
          />
        </div>
        <div className="space-y-2">
          <Label htmlFor="tenant-admin-confirm-password">
            {t("User.Section.Label.ConfirmPassword_Label")}
          </Label>
          <Input
            id="tenant-admin-confirm-password"
            type="password"
            value={confirmPassword}
            onChange={(e) => setConfirmPassword(e.target.value)}
            autoComplete="new-password"
          />
        </div>
      </div>

      <div className="mt-4">
        <Button
          type="button"
          onClick={onSubmit}
          disabled={changePasswordMutation.isLoading}
        >
          {t("User.Actions.SetPassword")}
        </Button>
      </div>
    </CardWrapper>
  );
};

export default UserPasswordTab;
