import { useTranslation } from "react-i18next";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import Loading from "@/components/Loading/Loading";
import { Input } from "@/components/ui/input";
import { Search, UserPlus } from "lucide-react";
import { useQuery } from "react-query";
import { getUsers } from "@/services/UserServices";
import { usePaginationTable } from "@/components/DataTable/usePaginationTable";
import { DataTable } from "@/components/DataTable/DataTable";
import { UserData } from "@/models/Users/UserModels";
import useSearch from "@/hooks/useSearch";
import { queryKeys } from "@/services/QueryKeys";

type Props = {
  isOpen: boolean;
  onClose: () => void;
  onSubmit: (userId: string) => void;
};

const TenantAdminPickerModal: React.FC<Props> = ({
  isOpen,
  onClose,
  onSubmit,
}) => {
  const { t } = useTranslation();
  const { pagination, setPagination } = usePaginationTable(0, 5);

  const {
    inputValue,
    handleInputChange,
    handleSearch,
    handleInputKeyDown,
    searchTerm,
  } = useSearch({
    onSearchComplete: () => {
      setPagination({ ...pagination, pageIndex: 0 });
    },
  });

  const users = useQuery(
    [queryKeys.users, "tenantAdminPicker", pagination, searchTerm],
    () => getUsers(searchTerm, pagination.pageIndex, pagination.pageSize),
    { keepPreviousData: true, enabled: isOpen }
  );

  const columns = [
    {
      accessorKey: "userName",
      header: t("TenantAdmins.Fields.UserName"),
    },
    {
      accessorKey: "email",
      header: t("TenantAdmins.Fields.Email"),
    },
    {
      id: "actions",
      cell: ({ row }: { row: { original: UserData } }) => (
        <Button
          type="button"
          size="sm"
          onClick={() => onSubmit(row.original.id)}
        >
          <UserPlus className="h-4 w-4 mr-2" />
          {t("TenantAdmins.Actions.Assign")}
        </Button>
      ),
    },
  ];

  return (
    <Dialog open={isOpen} onOpenChange={onClose}>
      <DialogContent className="max-w-3xl">
        <DialogHeader>
          <DialogTitle>{t("TenantAdmins.Actions.Add")}</DialogTitle>
        </DialogHeader>

        <div className="flex flex-col space-y-3 md:flex-row md:items-center md:space-x-3 md:space-y-0">
          <div className="relative w-full">
            <Input
              type="text"
              placeholder={t("TenantAdmins.SearchPlaceholder")}
              className="pr-10"
              value={inputValue}
              onChange={handleInputChange}
              onKeyDown={handleInputKeyDown}
            />
            <Search className="pointer-events-none absolute right-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
          </div>

          <Button variant="secondary" onClick={handleSearch}>
            {t("Actions.Search")}
          </Button>
        </div>

        {users.isLoading ? (
          <Loading />
        ) : (
          <DataTable
            columns={columns}
            data={users.data?.items ?? []}
            totalCount={users.data?.totalCount ?? 0}
            pagination={pagination}
            setPagination={setPagination}
          />
        )}
      </DialogContent>
    </Dialog>
  );
};

export default TenantAdminPickerModal;
