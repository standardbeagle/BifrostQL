﻿using System;
using System.Collections.Generic;
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

        public string GetTableColumnEnumDefinition()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"enum {_table.GraphQlName}_columns {{");
            foreach (var column in _table.Columns)
            {
                sb.AppendLine($"    {column.GraphQlName},");
            }
            sb.AppendLine("}");
            return sb.ToString();
        }

        public string GetTableEnumDefinition()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"enum {_table.GraphQlName}Enum {{");
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
            sb.AppendLine($"enum {_table.GraphQlName}SortEnum {{");
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
            return $"{_table.GraphQlName}_columns";
        }

        public string GetInputType(string action, IdentityType identityType, bool isDelete = false)
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
                result.AppendLine($"\t{column.GraphQlName} : {DbSchema.GetGraphQlInsertTypeName(column.DataType, isNullable)}");
            }
            result.AppendLine("}");
            return result.ToString();
        }

        public string GetJoinDefinitions(IDbModel model)
        {
            var builder = new StringBuilder();
            foreach (var joinTable in model.Tables)
            {
                builder.AppendLine($"input TableOn{_table.GraphQlName}{joinTable.GraphQlName} {{");
                foreach (var column in _table.Columns)
                {
                    builder.AppendLine($"\t{column.GraphQlName} : FilterType{joinTable.GraphQlName}EnumInput");
                }

                builder.AppendLine($"and: [TableOn{_table.GraphQlName}{joinTable.GraphQlName}!]");
                builder.AppendLine($"or: [TableOn{_table.GraphQlName}{joinTable.GraphQlName}!]");

                //foreach (var link in table.SingleLinks)
                //{
                //    builder.AppendLine($"\t{link.Value.ParentTable.GraphQlName} : {link.Value.ParentTable.GraphQlName}");
                //}
                builder.AppendLine("}");
            }

            return builder.ToString();
        }

        public string GetTableFilterDefinition()
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine($"input TableFilter{_table.GraphQlName}Input {{");
            foreach (var column in _table.Columns)
            {
                builder.AppendLine($"\t{column.GraphQlName} : FilterType{DbSchema.GetSimpleGraphQlTypeName(column.DataType)}Input");
            }
            foreach (var link in _table.SingleLinks)
            {
                builder.AppendLine($"\t{link.Value.ParentTable.GraphQlName} : TableFilter{link.Value.ParentTable.GraphQlName}Input");
            }
            builder.AppendLine($"and: [TableFilter{_table.GraphQlName}Input!]");
            builder.AppendLine($"or: [TableFilter{_table.GraphQlName}Input!]");
            builder.AppendLine("}");

            return builder.ToString();
        }
    }
}
