import { useFormContext, useFieldArray } from "react-hook-form";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/DataTable/DataTable";
import { Trash } from "lucide-react";
import { usePaginationTable } from "@/components/DataTable/usePaginationTable";
import useModal from "@/hooks/modalHooks";
import { useTranslation } from "react-i18next";
import PropertyModal from "@/components/Properties/PropertyModal";

type FormData = {
  properties: Array<{
    id: number;
    key: string;
    value: string;
  }>;
};

type PropertiesFormProps = {
  hiddenKeys?: string[];
};

const PropertiesForm = ({ hiddenKeys = [] }: PropertiesFormProps) => {
  const { t } = useTranslation();
  const { isOpen, openModal, closeModal } = useModal();

  const { control } = useFormContext<FormData>();
  const { fields, append, remove } = useFieldArray({
    control,
    name: "properties",
  });

  const { pagination, setPagination } = usePaginationTable(0, 5);

  const visibleFields = fields
    .map((field, index) => ({
      ...field,
      sourceIndex: index,
    }))
    .filter((field) => !hiddenKeys.includes(field.key));

  const handleAddProperty = (data: { key: string; value: string }) => {
    append({ id: 0, key: data.key, value: data.value });
    closeModal();
  };

  const handleRemoveProperty = (index: number) => {
    remove(index);
    if (
      pagination.pageIndex > 0 &&
      visibleFields.length % pagination.pageSize === 1
    ) {
      setPagination((prev) => ({
        ...prev,
        pageIndex: prev.pageIndex - 1,
      }));
    }
  };

  const paginatedData = visibleFields.slice(
    pagination.pageIndex * pagination.pageSize,
    (pagination.pageIndex + 1) * pagination.pageSize
  );

  const columns = [
    {
      accessorKey: "key",
      header: t("Property.Key"),
    },
    {
      accessorKey: "value",
      header: t("Property.Value"),
    },
    {
      id: "actions",
      cell: ({ row }: { row: { original: { sourceIndex: number } } }) => (
        <Button
          variant="ghost"
          onClick={() => handleRemoveProperty(row.original.sourceIndex)}
          className="text-red-500"
        >
          <Trash className="h-4 w-4" />
        </Button>
      ),
    },
  ];

  return (
    <>
      <div>
        <Button
          type="button"
          onClick={() => {
            openModal();
          }}
          className="mb-4"
        >
          {t("Property.Actions.Add")}
        </Button>

        <DataTable
          columns={columns}
          data={paginatedData}
          totalCount={visibleFields.length}
          pagination={pagination}
          setPagination={setPagination}
        />
      </div>

      <PropertyModal
        isOpen={isOpen}
        onClose={closeModal}
        onSubmit={handleAddProperty}
      />
    </>
  );
};

export default PropertiesForm;
