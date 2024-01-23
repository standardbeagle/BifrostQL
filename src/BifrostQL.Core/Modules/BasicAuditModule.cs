using BifrostQL.Model;
using GraphQL;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules
{
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
            foreach (var column in table.Columns)
            {
                if (column.CompareMetadata("populate", "created-on")) data[column.ColumnName] = dateTime;
                if (column.CompareMetadata("populate", "created-by") && hasAuditKey) data[column.ColumnName] = userContext[auditKey!];
                if (column.CompareMetadata("populate", "updated-on")) data[column.ColumnName] = dateTime;
                if (column.CompareMetadata("populate", "updated-by") && hasAuditKey) data[column.ColumnName] = userContext[auditKey!];
            }
            return Array.Empty<string>();
        }

        public string[] Update(Dictionary<string, object?> data, IDbTable table, IDictionary<string, object?> userContext, IDbModel model)
        {
            var dateTime = DateTime.UtcNow;
            var auditKey = model.GetMetadataValue("user-audit-key");
            var hasAuditKey = !string.IsNullOrWhiteSpace(auditKey);
            foreach (var column in table.Columns)
            {
                if (column.CompareMetadata("populate", "updated-on")) data[column.ColumnName] = dateTime;
                if (column.CompareMetadata("populate", "updated-by") && hasAuditKey) data[column.ColumnName] = userContext[auditKey!];
            }
            return Array.Empty<string>();
        }

        public string[] Delete(Dictionary<string, object?> data, IDbTable table, IDictionary<string, object?> userContext, IDbModel model)
        {
            var dateTime = DateTime.UtcNow;
            var auditKey = model.GetMetadataValue("user-audit-key");
            var hasAuditKey = !string.IsNullOrWhiteSpace(auditKey);
            foreach (var column in table.Columns)
            {
                if (column.CompareMetadata("populate", "updated-on")) data[column.ColumnName] = dateTime;
                if (column.CompareMetadata("populate", "updated-by") && hasAuditKey) data[column.ColumnName] = userContext[auditKey!];
                if (column.CompareMetadata("populate", "deleted-on")) data[column.ColumnName] = dateTime;
                if (column.CompareMetadata("populate", "deleted-by") && hasAuditKey) data[column.ColumnName] = userContext[auditKey!];
            }
            return Array.Empty<string>();
        }
    }
}
