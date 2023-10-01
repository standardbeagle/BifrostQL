using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Schema
{
    internal sealed class DbAggregate
    {
        private readonly IDbModel _model;
        public DbAggregate(IDbModel model)
        {
            this._model = model;
        }
        public string Generate()
        {
            var sb = new StringBuilder();
            foreach (var table in _model.Tables)
            {
                var tableSchema = new TableSchemaGenerator(table);
                sb.AppendLine($"{table.GraphQlName}_agg(field: {tableSchema.GetFieldEnumReference()}_enum function : AggregateType) : (Int | Float | Boolean | String)");
            }
            return sb.ToString();
        }

        public string GetTypes()
        {
            var sb = new StringBuilder();
            foreach (var table in _model.Tables)
            {
                var tableSchema = new TableSchemaGenerator(table);
                sb.AppendLine(tableSchema.GetTableColumnEnumDefinition());
            }
            sb.AppendLine("enum AggregateType { SUM, AVG, COUNT, MAX, MIN, STDEV}");
            return sb.ToString();
        }
    }
}
