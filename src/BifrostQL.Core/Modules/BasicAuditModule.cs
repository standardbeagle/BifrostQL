using System;
using System.Collections.Generic;
using BifrostQL.Core.Model;
using GraphQL;

namespace BifrostQL.Core.Modules
{
    /// <summary>
    /// Automatically populates audit columns based on column metadata configuration.
    ///
    /// Column metadata is configured via the metadata rules system using the "populate" property:
    ///
    ///   Timestamp columns (auto-populated with UTC server time):
    ///     "dbo.*.created_at { populate: created-on }"   - Set on INSERT only
    ///     "dbo.*.updated_at { populate: updated-on }"   - Set on INSERT and UPDATE
    ///     "dbo.*.deleted_at { populate: deleted-on }"   - Set on DELETE only
    ///
    ///   User columns (auto-populated from BifrostContext user claims):
    ///     "dbo.*.created_by { populate: created-by }"   - Set on INSERT only
    ///     "dbo.*.updated_by { populate: updated-by }"   - Set on INSERT and UPDATE
    ///     "dbo.*.deleted_by { populate: deleted-by }"   - Set on DELETE only
    ///
    /// User columns require the "user-audit-key" database metadata to identify which
    /// claim to read from the user context:
    ///     ":root { user-audit-key: id }"
    ///
    /// The module overwrites any client-provided values for audit columns, preventing
    /// clients from spoofing audit data. Audit columns are marked as optional in
    /// GraphQL input types so clients do not need to supply them.
    ///
    /// Example full configuration:
    ///   ":root { user-audit-key: id }"
    ///   "dbo.*.created_at { populate: created-on }"
    ///   "dbo.*.created_by_user_id { populate: created-by }"
    ///   "dbo.*.updated_at { populate: updated-on }"
    ///   "dbo.*.updated_by_user_id { populate: updated-by }"
    /// </summary>
    public class BasicAuditModule : IMutationModule
    {
        public BasicAuditModule()
        {
        }

        public void OnSave(IResolveFieldContext context)
        {
        }

        public string[] Insert(Dictionary<string, object?> data, IDbTable table, IDictionary<string, object?> userContext, IDbModel model)
        {
            var dateTime = DateTime.UtcNow;
            var auditKey = model.GetMetadataValue("user-audit-key");
            var hasAuditKey = !string.IsNullOrWhiteSpace(auditKey);
            var auditValue = ResolveAuditUser(auditKey, userContext, hasAuditKey);
            foreach (var column in table.Columns)
            {
                if (column.CompareMetadata("populate", "created-on")) data[column.ColumnName] = dateTime;
                if (column.CompareMetadata("populate", "created-by") && hasAuditKey) data[column.ColumnName] = auditValue;
                if (column.CompareMetadata("populate", "updated-on")) data[column.ColumnName] = dateTime;
                if (column.CompareMetadata("populate", "updated-by") && hasAuditKey) data[column.ColumnName] = auditValue;
            }
            return Array.Empty<string>();
        }

        public string[] Update(Dictionary<string, object?> data, IDbTable table, IDictionary<string, object?> userContext, IDbModel model)
        {
            var dateTime = DateTime.UtcNow;
            var auditKey = model.GetMetadataValue("user-audit-key");
            var hasAuditKey = !string.IsNullOrWhiteSpace(auditKey);
            var auditValue = ResolveAuditUser(auditKey, userContext, hasAuditKey);
            foreach (var column in table.Columns)
            {
                if (column.CompareMetadata("populate", "updated-on")) data[column.ColumnName] = dateTime;
                if (column.CompareMetadata("populate", "updated-by") && hasAuditKey) data[column.ColumnName] = auditValue;
            }
            return Array.Empty<string>();
        }

        public string[] Delete(Dictionary<string, object?> data, IDbTable table, IDictionary<string, object?> userContext, IDbModel model)
        {
            var dateTime = DateTime.UtcNow;
            var auditKey = model.GetMetadataValue("user-audit-key");
            var hasAuditKey = !string.IsNullOrWhiteSpace(auditKey);
            var auditValue = ResolveAuditUser(auditKey, userContext, hasAuditKey);
            foreach (var column in table.Columns)
            {
                if (column.CompareMetadata("populate", "updated-on")) data[column.ColumnName] = dateTime;
                if (column.CompareMetadata("populate", "updated-by") && hasAuditKey) data[column.ColumnName] = auditValue;
                if (column.CompareMetadata("populate", "deleted-on")) data[column.ColumnName] = dateTime;
                if (column.CompareMetadata("populate", "deleted-by") && hasAuditKey) data[column.ColumnName] = auditValue;
            }
            return Array.Empty<string>();
        }

        private static object? ResolveAuditUser(string? auditKey, IDictionary<string, object?> userContext, bool hasAuditKey)
        {
            if (!hasAuditKey || auditKey == null)
                return null;

            if (userContext.TryGetValue(auditKey, out var value))
                return value;

            return null;
        }
    }
}
