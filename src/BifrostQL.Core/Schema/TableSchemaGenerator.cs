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
        private readonly ITypeMapper _typeMapper;

        public TableSchemaGenerator(IDbTable table) : this(table, SchemaGenerator.DefaultTypeMapper)
        {
        }

        public TableSchemaGenerator(IDbTable table, ITypeMapper typeMapper)
        {
            _table = table;
            _typeMapper = typeMapper;
        }

        public string GetTableFieldDefinition()
        {
            var hasSoftDelete = _table.Metadata.TryGetValue("soft-delete", out var sdVal) && sdVal != null;
            var includeDeletedArg = hasSoftDelete ? " _includeDeleted: Boolean" : "";
            return
                $"{_table.GraphQlName}(limit: Int, offset: Int, sort: [{_table.TableColumnSortEnumName}!] filter: {_table.TableFilterTypeName} _primaryKey: [String]{includeDeletedArg}): {_table.GraphQlName}_paged";
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
                builder.AppendLine($"\t{column.GraphQlName} : {SchemaGenerator.GetGraphQlTypeName(column.EffectiveDataType, column.IsNullable, _typeMapper)}");
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
            foreach (var link in _table.ManyToManyLinks)
            {
                if (_table.SingleLinks.ContainsKey(link.Key) || _table.MultiLinks.ContainsKey(link.Key))
                    continue;
                builder.AppendLine($"\t{link.Value.TargetTable.GraphQlName}(filter: {link.Value.TargetTable.TableFilterTypeName}) : [{link.Value.TargetTable.GraphQlName}]");
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

        public string GetMutationParameterType(MutateActions action, IdentityType identityType, bool isDelete = false)
        {
            var result = new StringBuilder();
            var name = _table.GetActionTypeName(action);
            result.AppendLine($"input {name} {{");
            foreach (var column in _table.Columns)
            {
                if (identityType == IdentityType.None && column.IsIdentity)
                    continue;

                var isNullable = column.IsNullable;
                if (column.CompareMetadata("populate", "created-on") || column.CompareMetadata("populate", "created-by") ||
                    column.CompareMetadata("populate", "updated-on") || column.CompareMetadata("populate", "updated-by") ||
                    column.CompareMetadata("populate", "deleted-on") || column.CompareMetadata("populate", "deleted-by")
                    )
                    isNullable = true;
                if (column.IsIdentity)
                    isNullable = identityType switch
                    {
                        IdentityType.Optional => true,
                        IdentityType.Required => false,
                        IdentityType.None => true,
                        _ => throw new ArgumentOutOfRangeException(nameof(identityType), identityType, null)
                    };

                //All columns except primary keys are nullable for delete
                if (isDelete) isNullable = column.IsPrimaryKey == false;

                result.AppendLine($"\t{column.GraphQlName} : {SchemaGenerator.GetGraphQlInsertTypeName(column.EffectiveDataType, isNullable, _typeMapper)}");
            }
            result.AppendLine("}");
            return result.ToString();
        }

        public string GetBatchMutationParameterType()
        {
            var result = new StringBuilder();
            var name = "batch_" + _table.GraphQlName;
            result.AppendLine($"input {name} {{");
            result.AppendLine($"insert: {_table.GetActionTypeName(MutateActions.Insert)}");
            result.AppendLine($"update: {_table.GetActionTypeName(MutateActions.Update)}");
            result.AppendLine($"upsert: {_table.GetActionTypeName(MutateActions.Upsert)}");
            result.AppendLine($"delete: {_table.GetActionTypeName(MutateActions.Delete)}");
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
                builder.AppendLine($"\t{column.GraphQlName} : {SchemaGenerator.GetFilterInputTypeName(column.EffectiveDataType, _typeMapper)}");
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
            var result = new StringBuilder();

            result.AppendLine(
                $"\t{_table.GraphQlName}(insert: {_table.GetActionTypeName(MutateActions.Insert)}, update: {_table.GetActionTypeName(MutateActions.Update)}, upsert: {_table.GetActionTypeName(MutateActions.Upsert)}, delete: {_table.GetActionTypeName(MutateActions.Delete)}, _primaryKey: [String]) : Int");

            result.AppendLine($"{_table.GraphQlName}_batch(actions: [batch_{_table.GraphQlName}!]!) : Int");
            return result.ToString();
        }

        public string GetTableJoinType()
        {
            return SchemaGenerator.GetOnType(_table);
        }
    }
}
