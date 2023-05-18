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
            foreach (var column in table.Columns)
            {
                if (column.IsCreatedOnColumn) data[column.ColumnName] = dateTime;
                if (column.IsUpdatedOnColumn) data[column.ColumnName] = dateTime;
                if (column.IsCreatedByColumn && userContext.Keys.Contains(model.UserAuditKey)) data[column.ColumnName] = userContext[model.UserAuditKey];
                if (column.IsUpdatedByColumn && userContext.Keys.Contains(model.UserAuditKey)) data[column.ColumnName] = userContext[model.UserAuditKey];
            }
            return Array.Empty<string>();
        }

        public string[] Update(Dictionary<string, object?> data, IDbTable table, IDictionary<string, object?> userContext, IDbModel model)
        {
            var dateTime = DateTime.UtcNow;
            foreach (var column in table.Columns)
            {
                if (column.IsUpdatedOnColumn) data[column.ColumnName] = dateTime;
                if (column.IsUpdatedByColumn && userContext.Keys.Contains(model.UserAuditKey)) data[column.ColumnName] = userContext[model.UserAuditKey];
            }
            return Array.Empty<string>();
        }

        public string[] Delete(Dictionary<string, object?> data, IDbTable table, IDictionary<string, object?> userContext, IDbModel model)
        {
            return Array.Empty<string>();
        }
    }
}
