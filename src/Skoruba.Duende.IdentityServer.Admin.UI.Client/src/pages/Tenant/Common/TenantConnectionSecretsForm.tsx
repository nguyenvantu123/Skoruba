import { useState } from "react";
import { useFieldArray, useFormContext } from "react-hook-form";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/DataTable/DataTable";
import { usePaginationTable } from "@/components/DataTable/usePaginationTable";
import { Trash, Pencil } from "lucide-react";
import { useTranslation } from "react-i18next";
import { TenantFormData } from "./TenantSchema";
import TenantConnectionSecretModal from "./TenantConnectionSecretModal";

type TenantConnectionSecretsFormProps = {
  disabled?: boolean;
};

const TenantConnectionSecretsForm: React.FC<TenantConnectionSecretsFormProps> = ({
  disabled = false,
}) => {
  const { t } = useTranslation();
  const {
    control,
    formState: { errors },
  } = useFormContext<TenantFormData>();
  const { fields, append, update, remove } = useFieldArray({
    control,
    name: "connectionSecrets",
    keyName: "fieldId",
  });

  const { pagination, setPagination } = usePaginationTable(0, 5);
  const [isModalOpen, setModalOpen] = useState(false);
  const [editIndex, setEditIndex] = useState<number | null>(null);

  const absoluteIndex = (rowIndex: number) =>
    pagination.pageIndex * pagination.pageSize + rowIndex;

  const handleAdd = (data: { key: string; value: string }) => {
    if (editIndex === null) {
      append({ key: data.key, value: data.value });
    } else {
      update(editIndex, {
        key: data.key,
        value: data.value,
      });
    }

    setEditIndex(null);
    setModalOpen(false);
  };

  const handleEdit = (index: number) => {
    setEditIndex(index);
    setModalOpen(true);
  };

  const handleRemove = (index: number) => {
    remove(index);
    if (pagination.pageIndex > 0 && fields.length % pagination.pageSize === 1) {
      setPagination((prev) => ({
        ...prev,
        pageIndex: prev.pageIndex - 1,
      }));
    }
  };

  const paginatedData = fields.slice(
    pagination.pageIndex * pagination.pageSize,
    (pagination.pageIndex + 1) * pagination.pageSize
  );

  const nestedErrorMessages = Array.isArray(errors.connectionSecrets)
    ? errors.connectionSecrets
        .flatMap((item) => [item?.key?.message, item?.value?.message])
        .filter((message): message is string => typeof message === "string")
    : [];

  const columns = [
    {
      accessorKey: "key",
      header: t("Tenant.ConnectionSecrets.Fields.Service"),
    },
    {
      accessorKey: "value",
      header: t("Tenant.ConnectionSecrets.Fields.Secret"),
    },
    {
      id: "actions",
      cell: ({ row }: { row: { index: number } }) => {
        const index = absoluteIndex(row.index);
        return (
          <div className="flex gap-2">
            <Button
              type="button"
              variant="ghost"
              onClick={() => handleEdit(index)}
              disabled={disabled}
            >
              <Pencil className="h-4 w-4" />
            </Button>
            <Button
              type="button"
              variant="ghost"
              onClick={() => handleRemove(index)}
              className="text-red-500"
              disabled={disabled}
            >
              <Trash className="h-4 w-4" />
            </Button>
          </div>
        );
      },
    },
  ];

  return (
    <>
      <div className="space-y-4 mt-4">
        <Button type="button" onClick={() => setModalOpen(true)} disabled={disabled}>
          {t("Tenant.ConnectionSecrets.Actions.Add")}
        </Button>

        <DataTable
          columns={columns}
          data={paginatedData}
          totalCount={fields.length}
          pagination={pagination}
          setPagination={setPagination}
        />

        {typeof errors.connectionSecrets?.message === "string" && (
          <p className="text-sm text-destructive">{errors.connectionSecrets.message}</p>
        )}

        {nestedErrorMessages.map((message, index) => (
          <p key={`${message}-${index}`} className="text-sm text-destructive">
            {message}
          </p>
        ))}
      </div>

      <TenantConnectionSecretModal
        isOpen={isModalOpen}
        initialValue={editIndex !== null ? fields[editIndex] : undefined}
        existingKeys={fields.map((field) => field.key)}
        editingOriginalKey={editIndex !== null ? fields[editIndex]?.key : undefined}
        onClose={() => {
          setEditIndex(null);
          setModalOpen(false);
        }}
        onSubmit={handleAdd}
      />
    </>
  );
};

export default TenantConnectionSecretsForm;
