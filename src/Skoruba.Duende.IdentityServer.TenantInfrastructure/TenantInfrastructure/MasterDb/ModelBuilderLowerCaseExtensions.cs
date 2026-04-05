using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace TenantInfrastructure.MasterDb;

internal static class ModelBuilderLowerCaseExtensions
{
    public static void ApplyLowerCaseNames(this ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            if (entity.IsOwned())
            {
                continue;
            }

            var tableName = entity.GetTableName();
            var schema = entity.GetSchema();

            if (!string.IsNullOrWhiteSpace(tableName))
            {
                entity.SetTableName(tableName.ToLowerInvariant());
            }

            if (!string.IsNullOrWhiteSpace(schema))
            {
                entity.SetSchema(schema.ToLowerInvariant());
            }

            var normalizedTableName = entity.GetTableName();
            if (string.IsNullOrWhiteSpace(normalizedTableName))
            {
                continue;
            }

            var storeObject = StoreObjectIdentifier.Table(normalizedTableName, entity.GetSchema());

            foreach (var property in entity.GetProperties())
            {
                var columnName = property.GetColumnName(storeObject);
                if (!string.IsNullOrWhiteSpace(columnName))
                {
                    property.SetColumnName(columnName.ToLowerInvariant());
                }
            }

            foreach (var key in entity.GetKeys())
            {
                var keyName = key.GetName();
                if (!string.IsNullOrWhiteSpace(keyName))
                {
                    key.SetName(keyName.ToLowerInvariant());
                }
            }

            foreach (var foreignKey in entity.GetForeignKeys())
            {
                var constraintName = foreignKey.GetConstraintName();
                if (!string.IsNullOrWhiteSpace(constraintName))
                {
                    foreignKey.SetConstraintName(constraintName.ToLowerInvariant());
                }
            }

            foreach (var index in entity.GetIndexes())
            {
                var indexName = index.GetDatabaseName();
                if (!string.IsNullOrWhiteSpace(indexName))
                {
                    index.SetDatabaseName(indexName.ToLowerInvariant());
                }
            }
        }
    }
}
