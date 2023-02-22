using BifrostQL.Model;
using GraphQL;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BifrostQL.Core.Modules
{
    public class BasicAuditModule : IMutationModule
    {
        private readonly IDbModel _model;
        public BasicAuditModule(IDbModel model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            _model = model;
        }

        public void OnSave(IResolveFieldContext context)
        {
        }

        public string[] Insert(Dictionary<string, object?> data, TableDto table, IDictionary<string, object?> userContext)
        {
            var dateTime = DateTime.UtcNow;
            foreach (var column in table.Columns)
            {
                if (column.IsCreatedOnColumn) data[column.ColumnName] = dateTime;
                if (column.IsUpdatedOnColumn) data[column.ColumnName] = dateTime;
                if (column.IsCreatedByColumn && userContext.Keys.Contains(_model.UserAuditKey)) data[column.ColumnName] = userContext[_model.UserAuditKey];
                if (column.IsUpdatedByColumn && userContext.Keys.Contains(_model.UserAuditKey)) data[column.ColumnName] = userContext[_model.UserAuditKey];
            }
            return Array.Empty<string>();
        }

        public string[] Update(Dictionary<string, object?> data, TableDto table, IDictionary<string, object?> userContext)
        {
            var dateTime = DateTime.UtcNow;
            foreach (var column in table.Columns)
            {
                if (column.IsUpdatedOnColumn) data[column.ColumnName] = dateTime;
                if (column.IsUpdatedByColumn && userContext.Keys.Contains(_model.UserAuditKey)) data[column.ColumnName] = userContext[_model.UserAuditKey];
            }
            return Array.Empty<string>();
        }

        public string[] Delete(Dictionary<string, object?> data, TableDto table, IDictionary<string, object?> userContext)
        {
            return Array.Empty<string>();
        }
    }
}
