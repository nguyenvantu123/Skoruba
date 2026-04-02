import { DataTable } from "@/components/DataTable/DataTable";
import { useTranslation } from "react-i18next";
import { TenantData, TenantModel } from "@/models/Common/TenantModel";
import { PaginationState } from "@tanstack/react-table";
import { Dispatch, SetStateAction } from "react";
import { TenantEditUrl } from "@/routing/Urls";
import { Link } from "react-router-dom";
import { Badge } from "@/components/ui/badge";

type TenantsTableProps = {
  data: TenantData;
  pagination: PaginationState;
  setPagination: Dispatch<SetStateAction<PaginationState>>;
};

const TenantsTable = ({ data, pagination, setPagination }: TenantsTableProps) => {
  const { t } = useTranslation();

  const columns = [
    {
      accessorKey: "tenantKey",
      header: t("Tenant.Section.Label.TenantKey_Label"),
      cell: ({ row }: { row: { original: TenantModel } }) => (
        <Link
          to={TenantEditUrl.replace(":tenantId", row.original.id.toString())}
          className="underline"
        >
          {row.original.tenantKey}
        </Link>
      ),
    },
    {
      accessorKey: "displayName",
      header: t("Tenant.Section.Label.DisplayName_Label"),
    },
    {
      accessorKey: "isActive",
      header: t("Tenant.Section.Label.IsActive_Label"),
      cell: ({ row }: { row: { original: TenantModel } }) => (
        <Badge variant={row.original.isActive ? "default" : "secondary"}>
          {row.original.isActive
            ? t("Tenant.Status.Active")
            : t("Tenant.Status.Inactive")}
        </Badge>
      ),
    },
  ];

  return (
    <DataTable
      columns={columns}
      data={data.items}
      totalCount={data.totalCount}
      pagination={pagination}
      setPagination={setPagination}
    />
  );
};

export default TenantsTable;
