using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Schema
{
    internal sealed class TableSchemaGenerator
    {
        private readonly IDbTable _table;
        public TableSchemaGenerator(IDbTable table)
        {
            _table = table;
        }

        public string GetTableFieldDefinition()
        {
            return
                $"{_table.GraphQlName}(limit: Int, offset: Int, sort: [{_table.TableColumnSortEnumName}!] filter: {_table.TableFilterTypeName}): {_table.GraphQlName}_paged";
        }

        public string GetDynamicJoinDefinition(IDbModel model, bool single)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"type {_table.GraphQlName}_{(single ? "single" : "join")} {{");
            foreach (var joinTable in model.Tables)
            {
                //if (single)
                //{
                    builder.AppendLine(
                        $"\t{joinTable.GraphQlName}(on: [String!]) : {joinTable.GraphQlName}");
                //}
                //else
                //{
                //    builder.AppendLine(
                //        $"\t{joinTable.GraphQlName}(on: TableOn{_table.GraphQlName}{joinTable.GraphQlName}, filter: {joinTable.TableFilterTypeName}, sort: [{joinTable.TableColumnSortEnumName}!]) : [{joinTable.GraphQlName}!]!");
                //}
            }
            builder.AppendLine("}");
            return builder.ToString();
        }

        public string GetTableTypeDefinition(IDbModel model, bool includeDynamicJoins)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"type {_table.GraphQlName} {{");
            foreach (var column in _table.Columns)
            {
                builder.AppendLine($"\t{column.GraphQlName} : {SchemaGenerator.GetGraphQlTypeName(column.DataType, column.IsNullable)}");
            }

            builder.AppendLine($"_agg(operation: AggregateOperations! value: {_table.AggregateValueTypeName}!) : Float");
            foreach (var link in _table.SingleLinks)
            {
                builder.AppendLine($"\t{link.Value.ParentTable.GraphQlName} : {link.Value.ParentTable.GraphQlName}");
            }
            foreach (var link in _table.MultiLinks)
            {
                builder.AppendLine($"\t{link.Value.ChildTable.GraphQlName}(filter: {link.Value.ChildTable.TableFilterTypeName}) : [{link.Value.ChildTable.GraphQlName}]");
            }

            if (includeDynamicJoins)
            {
                builder.AppendLine($"\t_single : {_table.GraphQlName}_single");
                builder.AppendLine($"\t_join : {_table.GraphQlName}_join");
                //foreach (var joinTable in model.Tables)
                //{
                //    builder.AppendLine(
                //        $"\t_join_{joinTable.GraphQlName}(on: TableOn{_table.GraphQlName}{joinTable.GraphQlName}, filter: {joinTable.TableFilterTypeName}, sort: [{joinTable.TableColumnSortEnumName}!]) : [{joinTable.GraphQlName}!]!");
                //    builder.AppendLine(
                //        $"\t_single_{joinTable.GraphQlName}(on: [String!]) : {joinTable.GraphQlName}");
                //}
            }

            builder.AppendLine("}");

            return builder.ToString();
        }

        public string GetPagedTableTypeDefinition()
        {
            var builder = new StringBuilder();
            builder.AppendLine($"type {_table.GraphQlName}_paged {{");
            builder.AppendLine($"\tdata:[{_table.GraphQlName}]");
            builder.AppendLine("\ttotal: Int!");
            builder.AppendLine("\toffset: Int");
            builder.AppendLine("\tlimit: Int");
            builder.AppendLine("}");
            return builder.ToString();
        }

        public string GetTableColumnEnumDefinition()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"enum {_table.ColumnEnumTypeName} {{");
            foreach (var column in _table.Columns)
            {
                sb.AppendLine($"    {column.GraphQlName},");
            }
            sb.AppendLine("}");
            return sb.ToString();
        }

        public string GetTableSortEnumDefinition()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"enum {_table.TableColumnSortEnumName} {{");
            foreach (var column in _table.Columns)
            {
                sb.AppendLine($"    {column.GraphQlName}_asc,");
                sb.AppendLine($"    {column.GraphQlName}_desc,");
            }
            sb.AppendLine("}");
            return sb.ToString();
        }

        public string GetFieldEnumReference()
        {
            return $"{_table.ColumnEnumTypeName}";
        }

        public string GetMutationParameterType(string action, IdentityType identityType, bool isDelete = false)
        {
            var result = new StringBuilder();
            var name = action + _table.GraphQlName;
            result.AppendLine($"input {name} {{");
            foreach (var column in _table.Columns)
            {
                if (identityType == IdentityType.None && column.IsIdentity)
                    continue;

                var isNullable = column.IsNullable;
                if (column.IsCreatedOnColumn || column.IsCreatedByColumn || column.IsUpdatedByColumn || column.IsUpdatedOnColumn)
                    isNullable = true;
                if (identityType == IdentityType.Optional && column.IsIdentity)
                    isNullable = true;
                if (identityType == IdentityType.Required && column.IsIdentity)
                    isNullable = false;

                if (isDelete) isNullable = true;
                result.AppendLine($"\t{column.GraphQlName} : {SchemaGenerator.GetGraphQlInsertTypeName(column.DataType, isNullable)}");
            }
            result.AppendLine("}");
            return result.ToString();
        }

        public string GetJoinDefinitions(IDbModel model)
        {
            var builder = new StringBuilder();
            foreach (var joinTable in model.Tables)
            {
                builder.AppendLine($"input {_table.GetJoinTypeName(joinTable)} {{");
                foreach (var column in _table.Columns)
                {
                    builder.AppendLine($"\t{column.GraphQlName} : {joinTable.ColumnFilterTypeName}");
                }

                builder.AppendLine($"and: [{_table.GetJoinTypeName(joinTable)}!]");
                builder.AppendLine($"or: [{_table.GetJoinTypeName(joinTable)}!]");

                foreach (var link in _table.SingleLinks)
                {
                    builder.AppendLine($"\t{link.Value.ParentTable.GraphQlName} : {_table.GetJoinTypeName(link.Value.ParentTable)}");
                }
                builder.AppendLine("}");
            }

            return builder.ToString();
        }
        public string GetAggregateLinkDefinitions()
        {
            var builder = new StringBuilder();
            builder.AppendLine($"input {_table.AggregateValueTypeName} {{");
            builder.AppendLine($"column : {_table.ColumnEnumTypeName}");
            foreach (var link in _table.MultiLinks)
            {
                //For multi-links _table is the ParentTable
                builder.AppendLine($"\t{link.Value.ChildTable.GraphQlName} : {link.Value.ChildTable.AggregateValueTypeName}");
            }
            foreach (var link in _table.SingleLinks)
            {
                //For single links _table is the ChildTable
                builder.AppendLine($"\t{link.Value.ParentTable.GraphQlName} : {link.Value.ParentTable.AggregateValueTypeName}");
            }
            builder.AppendLine("}");

            return builder.ToString();
        }

        public string GetTableFilterDefinition()
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine($"input {_table.TableFilterTypeName} {{");
            foreach (var column in _table.Columns)
            {
                builder.AppendLine($"\t{column.GraphQlName} : {SchemaGenerator.GetFilterInputTypeName(column.DataType)}");
            }
            foreach (var link in _table.SingleLinks)
            {
                builder.AppendLine($"\t{link.Value.ParentTable.GraphQlName} : {link.Value.ParentTable.TableFilterTypeName}");
            }
            builder.AppendLine($"and: [{_table.TableFilterTypeName}!]");
            builder.AppendLine($"or: [{_table.TableFilterTypeName}!]");
            builder.AppendLine("}");

            return builder.ToString();
        }

        public string GetInputFieldDefinition()
        {
            return
                $"\t{_table.GraphQlName}(insert: Insert{_table.GraphQlName}, update: Update{_table.GraphQlName}, upsert: Upsert{_table.GraphQlName}, delete: Delete{_table.GraphQlName}) : Int";
        }

        public string GetTableJoinType()
        {
            return SchemaGenerator.GetOnType(_table);
        }
    }
}
