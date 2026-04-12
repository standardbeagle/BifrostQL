using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Schema
{
    internal static class SchemaGenerator
    {
        public static string SchemaTextFromModel(IDbModel model, bool includeDynamicJoins = true)
        {
            var builder = new StringBuilder();
            var typeMapper = model.TypeMapper;
            var tableGenerators = model.Tables.Select(t => new TableSchemaGenerator(t, typeMapper)).ToList();
            var spGenerators = model.StoredProcedures.Select(p => new StoredProcedureSchemaGenerator(p)).ToList();
            var readOnlySpGenerators = model.StoredProcedures
                .Where(p => p.IsReadOnly)
                .Select(p => new StoredProcedureSchemaGenerator(p)).ToList();
            var mutatingSpGenerators = model.StoredProcedures
                .Where(p => !p.IsReadOnly)
                .Select(p => new StoredProcedureSchemaGenerator(p)).ToList();

            builder.AppendLine("schema { query: database mutation: databaseInput }");
            builder.AppendLine("type database {");
            foreach (var generator in tableGenerators)
            {
                builder.AppendLine(generator.GetTableFieldDefinition());
            }
            foreach (var generator in readOnlySpGenerators)
            {
                builder.AppendLine(generator.GetFieldDefinition());
            }
            builder.AppendLine("_dbSchema(graphQlName: String): [dbTableSchema!]!");
            if (IsRawSqlEnabled(model))
            {
                builder.AppendLine("_rawQuery(sql: String!, params: JSON, timeout: Int): [JSON]!");
            }
            if (IsGenericTableEnabled(model))
            {
                builder.AppendLine("_table(name: String!, limit: Int, offset: Int, filter: JSON): GenericTableResult!");
            }
            if (FileStorageSchemaExtensions.IsFileStorageEnabled(model))
            {
                builder.Append(FileStorageSchemaExtensions.GetFileStorageQueryFields());
            }
            builder.AppendLine("}");

            foreach (var generator in tableGenerators)
            {
                if (includeDynamicJoins)
                {
                    builder.AppendLine(generator.GetDynamicJoinDefinition(model, false));
                    builder.AppendLine(generator.GetDynamicJoinDefinition(model, true));
                }
                builder.AppendLine(generator.GetTableTypeDefinition(model, includeDynamicJoins));
                builder.AppendLine(generator.GetPagedTableTypeDefinition());
            }

            builder.Append(GetInputAndArgumentTypes(model, tableGenerators, mutatingSpGenerators));

            foreach (var generator in spGenerators)
            {
                builder.AppendLine(generator.GetResultTypeDefinition());
                var inputType = generator.GetInputTypeDefinition();
                if (!string.IsNullOrEmpty(inputType))
                    builder.AppendLine(inputType);
            }

            //Define the filter types of all the columns in the database, needs to be specific to the connected database, and distinct because of GraphQL.
            foreach (var gqlType in model.Tables.SelectMany(t => t.Columns).Select<ColumnDto, string>(c => typeMapper.GetGraphQlType(c.EffectiveDataType)).Distinct())
            {
                builder.AppendLine(FilterTypeGenerator.Generate(gqlType));
            }

            builder.AppendLine(MetadataSchemaGenerator.Generate());

            if (IsGenericTableEnabled(model))
            {
                builder.AppendLine(GetGenericTableTypes());
            }

            if (FileStorageSchemaExtensions.IsFileStorageEnabled(model))
            {
                builder.AppendLine(FileStorageSchemaExtensions.GetFileStorageTypeDefinitions());
            }

            return builder.ToString();

        }

        private static StringBuilder GetInputAndArgumentTypes(IDbModel model, List<TableSchemaGenerator> tableGenerators, List<StoredProcedureSchemaGenerator> mutatingSpGenerators)
        {
            var builder = new StringBuilder();

            // Only generate databaseInput type if there are mutations
            if (tableGenerators.Count > 0 || mutatingSpGenerators.Count > 0)
            {
                builder.AppendLine("type databaseInput {");
                foreach (var generator in tableGenerators)
                {
                    builder.AppendLine(generator.GetInputFieldDefinition());
                }
                foreach (var generator in mutatingSpGenerators)
                {
                    builder.AppendLine(generator.GetFieldDefinition());
                }
                if (FileStorageSchemaExtensions.IsFileStorageEnabled(model))
                {
                    builder.Append(FileStorageSchemaExtensions.GetFileStorageMutationFields());
                }
                builder.AppendLine("}");
            }

            foreach (var generator in tableGenerators)
            {
                builder.AppendLine(generator.GetMutationParameterType(MutateActions.Insert, IdentityType.None));
                builder.AppendLine(generator.GetMutationParameterType(MutateActions.Update, IdentityType.Required));
                builder.AppendLine(generator.GetMutationParameterType(MutateActions.Upsert, IdentityType.Optional));
                builder.AppendLine(generator.GetMutationParameterType(MutateActions.Delete, IdentityType.Optional, true));
                builder.AppendLine(generator.GetBatchMutationParameterType());

                builder.AppendLine(generator.GetTableFilterDefinition());

                builder.AppendLine(generator.GetJoinDefinitions(model));

                builder.AppendLine(generator.GetTableJoinType());
                builder.AppendLine(generator.GetAggregateLinkDefinitions());

                builder.AppendLine(generator.GetTableColumnEnumDefinition());
                builder.AppendLine(generator.GetTableSortEnumDefinition());
            }

            return builder;
        }

        /// <summary>
        /// Default type mapper used when no dialect-specific mapper is available.
        /// </summary>
        internal static ITypeMapper DefaultTypeMapper => SqlServerTypeMapper.Instance;

        public static string GetGraphQlInsertTypeName(string dataType, bool isNullable = false)
            => GetGraphQlInsertTypeName(dataType, isNullable, DefaultTypeMapper);

        public static string GetGraphQlInsertTypeName(string dataType, bool isNullable, ITypeMapper typeMapper)
            => typeMapper.GetGraphQlInsertTypeName(dataType, isNullable);

        public static string GetGraphQlTypeName(string dataType, bool isNullable = false)
            => GetGraphQlTypeName(dataType, isNullable, DefaultTypeMapper);

        public static string GetGraphQlTypeName(string dataType, bool isNullable, ITypeMapper typeMapper)
            => typeMapper.GetGraphQlTypeName(dataType, isNullable);

        public static string GetFilterInputTypeName(string dataType)
            => GetFilterInputTypeName(dataType, DefaultTypeMapper);

        public static string GetFilterInputTypeName(string dataType, ITypeMapper typeMapper)
            => typeMapper.GetFilterInputTypeName(dataType);

        internal static string GetSimpleGraphQlTypeName(string dataType)
            => DefaultTypeMapper.GetGraphQlType(dataType);

        internal static bool IsRawSqlEnabled(IDbModel model)
        {
            var value = model.GetMetadataValue("raw-sql");
            return string.Equals(value, "enabled", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsGenericTableEnabled(IDbModel model)
        {
            var value = model.GetMetadataValue(Model.GenericTableConfig.MetadataKey);
            return string.Equals(value, Model.GenericTableConfig.MetadataEnabled, StringComparison.OrdinalIgnoreCase);
        }

        internal static string GetGenericTableTypes()
        {
            var sb = new StringBuilder();

            sb.AppendLine("type GenericTableResult {");
            sb.AppendLine("tableName: String!");
            sb.AppendLine("columns: [GenericColumnMetadata!]!");
            sb.AppendLine("rows: [JSON]!");
            sb.AppendLine("totalCount: Int!");
            sb.AppendLine("}");

            sb.AppendLine("type GenericColumnMetadata {");
            sb.AppendLine("name: String!");
            sb.AppendLine("dataType: String!");
            sb.AppendLine("isNullable: Boolean!");
            sb.AppendLine("isPrimaryKey: Boolean!");
            sb.AppendLine("}");

            return sb.ToString();
        }

        public static string GetOnType(IDbTable dbTable)
        {
            var columnEnum = dbTable.ColumnEnumTypeName;
            var result = new StringBuilder();
            var name = dbTable.ColumnFilterTypeName;
            var filters = new (string fieldName, string type)[] {
                ("_eq", columnEnum),
                ("_neq", columnEnum),
                ("_gt", columnEnum),
                ("_gte", columnEnum),
                ("_lt", columnEnum),
                ("_lte", columnEnum),
                ("_in", $"[{columnEnum}]"),
                ("_nin", $"[{columnEnum}]"),
                ("_between", $"[{columnEnum}]"),
                ("_nbetween", $"[{columnEnum}]"),
            };
            var stringFilters = new (string fieldName, string type)[] {
                ("_contains", columnEnum),
                ("_ncontains", columnEnum),
                ("_starts_with", columnEnum),
                ("_nstarts_with", columnEnum),
                ("_ends_with", columnEnum),
                ("_nends_with", columnEnum),
                ("_like", columnEnum),
                ("_nlike", columnEnum),
            };

            result.AppendLine($"input {name} {{");
            foreach (var (fieldName, type) in filters)
            {
                result.AppendLine($"\t{fieldName} : {type}");
            }
            foreach (var (fieldName, type) in stringFilters)
            {
                result.AppendLine($"\t{fieldName} : {type}");
            }
            result.AppendLine("}");
            return result.ToString();
        }
    }
    public enum AggregateOperationType
    {
        None,
        Count,
        Sum,
        Avg,
        Max,
        Min,
    }

}
