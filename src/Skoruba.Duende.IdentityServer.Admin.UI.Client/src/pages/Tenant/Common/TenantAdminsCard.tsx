import { useTranslation } from "react-i18next";
import { Button } from "@/components/ui/button";
import { Trash, Users } from "lucide-react";
import { DataTable } from "@/components/DataTable/DataTable";
import { usePaginationTable } from "@/components/DataTable/usePaginationTable";
import useModal from "@/hooks/modalHooks";
import {
  useTenantAdmins,
  useAssignTenantAdmin,
  useUnassignTenantAdmin,
} from "@/services/TenantService";
import { toast } from "@/components/ui/use-toast";
import { useState } from "react";
import DeleteDialog from "@/components/DeleteDialog/DeleteDialog";
import Loading from "@/components/Loading/Loading";
import { CardWrapper } from "@/components/CardWrapper/CardWrapper";
import TenantAdminPickerModal from "./TenantAdminPickerModal";
import { client } from "@skoruba/duende.identityserver.admin.api.client";

const TenantAdminsCard: React.FC<{ tenantId: number }> = ({ tenantId }) => {
  const { t } = useTranslation();
  const { pagination, setPagination } = usePaginationTable(0, 5);
  const { isOpen, openModal, closeModal } = useModal();

  const [isAlertOpen, setIsAlertOpen] = useState(false);
  const [selectedUserId, setSelectedUserId] = useState<string | null>(null);

  const { data, isLoading } = useTenantAdmins(
    tenantId,
    pagination.pageIndex,
    pagination.pageSize
  );
  const addMutation = useAssignTenantAdmin(tenantId);
  const deleteMutation = useUnassignTenantAdmin(tenantId);

  const handleAssign = (userId: string) => {
    addMutation.mutate(userId, {
      onSuccess: () => {
        toast({
          title: t("Actions.Hooray"),
          description: t("TenantAdmins.Actions.Assigned"),
        });
        closeModal();
      },
    });
  };

  const openDeleteDialog = (userId: string) => {
    setSelectedUserId(userId);
    setIsAlertOpen(true);
  };

  const confirmDelete = () => {
    if (selectedUserId) {
      deleteMutation.mutate(selectedUserId, {
        onSuccess: () => {
          toast({ title: t("TenantAdmins.Actions.Unassigned") });
          setIsAlertOpen(false);
          setSelectedUserId(null);
        },
      });
    }
  };

  const columns = [
    {
      accessorKey: "userName",
      header: t("TenantAdmins.Fields.UserName"),
    },
    {
      accessorKey: "email",
      header: t("TenantAdmins.Fields.Email"),
      cell: ({ row }: { row: { original: client.TenantAdminApiDto } }) =>
        row.original.email ?? "-",
    },
    {
      id: "actions",
      cell: ({ row }: { row: { original: client.TenantAdminApiDto } }) => {
        const userId = row.original.userId;

        return (
          <Button
            variant="ghost"
            onClick={() => userId && openDeleteDialog(userId)}
            className="text-red-500"
            disabled={!userId}
          >
            <Trash className="h-4 w-4" />
          </Button>
        );
      },
    },
  ];

  if (isLoading) {
    return <Loading />;
  }

  return (
    <CardWrapper
      title={t("TenantAdmins.Title")}
      description={t("TenantAdmins.Description")}
      icon={Users}
    >
      <Button onClick={openModal} className="mb-4" type="button">
        {t("TenantAdmins.Actions.Add")}
      </Button>

      <DataTable
        columns={columns}
        data={data?.items ?? []}
        totalCount={data?.totalCount ?? 0}
        pagination={pagination}
        setPagination={setPagination}
      />

      <TenantAdminPickerModal
        isOpen={isOpen}
        onClose={closeModal}
        onSubmit={handleAssign}
      />

      <DeleteDialog
        isAlertOpen={isAlertOpen}
        setIsAlertOpen={setIsAlertOpen}
        title={t("TenantAdmins.Actions.UnassignConfirmTitle")}
        message={t("TenantAdmins.Actions.UnassignConfirmDescription")}
        handleDelete={confirmDelete}
      />
    </CardWrapper>
  );
};

export default TenantAdminsCard;
