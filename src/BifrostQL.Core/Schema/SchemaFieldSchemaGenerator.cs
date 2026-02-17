using System.Text;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Schema
{
    /// <summary>
    /// Generates GraphQL schema text for the Field display mode where
    /// database schemas become top-level query fields grouping their tables.
    /// In this mode: query { sales { orders(...) { ... } } hr { employees(...) { ... } } }
    /// Tables in the default schema remain directly on the root query type.
    /// Cross-schema __join fields reference tables across all schemas.
    /// </summary>
    public static class SchemaFieldSchemaGenerator
    {
        /// <summary>
        /// Generates the full GraphQL schema text for field mode.
        /// Default-schema tables appear on the root query type.
        /// Non-default schemas get their own query/mutation types.
        /// All table type definitions, join definitions, etc. remain global (not nested).
        /// </summary>
        public static string SchemaTextFromModel(IDbModel model, SchemaFieldConfig config, bool includeDynamicJoins = true)
        {
            var builder = new StringBuilder();
            var typeMapper = model.TypeMapper;
            var allTableGenerators = model.Tables.Select(t => new TableSchemaGenerator(t, typeMapper)).ToList();
            var spGenerators = model.StoredProcedures.Select(p => new StoredProcedureSchemaGenerator(p)).ToList();
            var readOnlySpGenerators = model.StoredProcedures
                .Where(p => p.IsReadOnly)
                .Select(p => new StoredProcedureSchemaGenerator(p)).ToList();
            var mutatingSpGenerators = model.StoredProcedures
                .Where(p => !p.IsReadOnly)
                .Select(p => new StoredProcedureSchemaGenerator(p)).ToList();

            var grouped = config.GroupTablesBySchema(model.Tables);
            var defaultTables = grouped.TryGetValue("", out var dt) ? dt : Array.Empty<IDbTable>();
            var nonDefaultSchemas = grouped
                .Where(kvp => kvp.Key != "")
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Schema declaration
            builder.AppendLine("schema { query: database mutation: databaseInput }");

            // Root query type: default-schema tables + schema fields
            builder.AppendLine("type database {");
            foreach (var table in defaultTables)
            {
                var gen = new TableSchemaGenerator(table, typeMapper);
                builder.AppendLine(gen.GetTableFieldDefinition());
            }
            foreach (var kvp in nonDefaultSchemas)
            {
                var schemaFieldName = kvp.Key.ToGraphQl();
                var schemaTypeName = SchemaFieldConfig.GetSchemaQueryTypeName(kvp.Key);
                builder.AppendLine($"{schemaFieldName}: {schemaTypeName}");
            }
            foreach (var generator in readOnlySpGenerators)
            {
                builder.AppendLine(generator.GetFieldDefinition());
            }
            builder.AppendLine("_dbSchema(graphQlName: String): [dbTableSchema!]!");
            if (SchemaGenerator.IsRawSqlEnabled(model))
            {
                builder.AppendLine("_rawQuery(sql: String!, params: JSON, timeout: Int): [JSON]!");
            }
            builder.AppendLine("}");

            // Per-schema query types
            foreach (var kvp in nonDefaultSchemas)
            {
                var schemaTypeName = SchemaFieldConfig.GetSchemaQueryTypeName(kvp.Key);
                builder.AppendLine($"type {schemaTypeName} {{");
                foreach (var table in kvp.Value)
                {
                    var gen = new TableSchemaGenerator(table, typeMapper);
                    builder.AppendLine(gen.GetTableFieldDefinition());
                }
                builder.AppendLine("}");
            }

            // All table type definitions remain global (for cross-schema join support)
            foreach (var generator in allTableGenerators)
            {
                if (includeDynamicJoins)
                {
                    builder.AppendLine(generator.GetDynamicJoinDefinition(model, false));
                    builder.AppendLine(generator.GetDynamicJoinDefinition(model, true));
                }
                builder.AppendLine(generator.GetTableTypeDefinition(model, includeDynamicJoins));
                builder.AppendLine(generator.GetPagedTableTypeDefinition());
            }

            // Root mutation type: default-schema tables + schema mutation fields
            builder.Append(GetFieldModeInputAndArgumentTypes(model, allTableGenerators, mutatingSpGenerators, config, grouped, nonDefaultSchemas));

            foreach (var generator in spGenerators)
            {
                builder.AppendLine(generator.GetResultTypeDefinition());
                var inputType = generator.GetInputTypeDefinition();
                if (!string.IsNullOrEmpty(inputType))
                    builder.AppendLine(inputType);
            }

            // Filter types for all columns in the database
            foreach (var gqlType in model.Tables.SelectMany(t => t.Columns).Select(c => typeMapper.GetGraphQlTypeName(c.EffectiveDataType).TrimEnd('!')).Distinct())
            {
                builder.AppendLine(SchemaGenerator.GetFilterType(gqlType));
            }

            builder.AppendLine(SchemaGenerator.GetMetadataSchemaTypes());

            return builder.ToString();
        }

        private static StringBuilder GetFieldModeInputAndArgumentTypes(
            IDbModel model,
            List<TableSchemaGenerator> allTableGenerators,
            List<StoredProcedureSchemaGenerator> mutatingSpGenerators,
            SchemaFieldConfig config,
            IReadOnlyDictionary<string, IReadOnlyList<IDbTable>> grouped,
            List<KeyValuePair<string, IReadOnlyList<IDbTable>>> nonDefaultSchemas)
        {
            var builder = new StringBuilder();
            var defaultTables = grouped.TryGetValue("", out var dt) ? dt : Array.Empty<IDbTable>();

            var typeMapper = model.TypeMapper;
            builder.AppendLine("type databaseInput {");
            foreach (var table in defaultTables)
            {
                var gen = new TableSchemaGenerator(table, typeMapper);
                builder.AppendLine(gen.GetInputFieldDefinition());
            }
            foreach (var kvp in nonDefaultSchemas)
            {
                var schemaFieldName = kvp.Key.ToGraphQl();
                var schemaMutTypeName = SchemaFieldConfig.GetSchemaMutationTypeName(kvp.Key);
                builder.AppendLine($"{schemaFieldName}: {schemaMutTypeName}");
            }
            foreach (var generator in mutatingSpGenerators)
            {
                builder.AppendLine(generator.GetFieldDefinition());
            }
            builder.AppendLine("}");

            // Per-schema mutation types
            foreach (var kvp in nonDefaultSchemas)
            {
                var schemaMutTypeName = SchemaFieldConfig.GetSchemaMutationTypeName(kvp.Key);
                builder.AppendLine($"type {schemaMutTypeName} {{");
                foreach (var table in kvp.Value)
                {
                    var gen = new TableSchemaGenerator(table, typeMapper);
                    builder.AppendLine(gen.GetInputFieldDefinition());
                }
                builder.AppendLine("}");
            }

            // All table mutation/filter/join types remain global
            foreach (var generator in allTableGenerators)
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

    }
}
