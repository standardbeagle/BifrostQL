using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Schema
{
    /// <summary>
    /// Generates a unified GraphQL schema that exposes multiple databases as top-level
    /// fields. Each database gets its own query type containing its tables, enabling
    /// queries like:
    /// <code>
    /// query {
    ///   userDb { users { id name } }
    ///   orderDb { orders { id total } }
    /// }
    /// </code>
    ///
    /// Cross-database joins are not supported as native SQL joins because the data
    /// resides on separate servers. Instead, use <c>CrossDatabaseJoinResolver</c> to
    /// perform in-memory joins after fetching results from each database independently.
    ///
    /// Transaction limitations: transactions cannot span multiple databases. Mutations
    /// targeting different databases execute in separate transactions. Applications
    /// must handle eventual consistency across database boundaries.
    /// </summary>
    public static class MultiDbSchemaGenerator
    {
        /// <summary>
        /// Generates the top-level query type definition for a multi-database schema.
        /// Each database alias becomes a field on the root query type, resolving to
        /// a database-specific query type that contains that database's tables.
        /// </summary>
        /// <param name="databaseFields">
        /// Map of database alias to its IDbModel. The alias is used as the GraphQL
        /// field name and to generate the per-database query type name.
        /// </param>
        /// <returns>
        /// GraphQL schema text defining the root query type with database fields,
        /// each per-database query type with table fields, and all supporting types
        /// (filters, inputs, enums) for all databases.
        /// </returns>
        public static string GenerateSchema(IReadOnlyDictionary<string, IDbModel> databaseFields)
        {
            if (databaseFields.Count == 0)
                throw new ArgumentException("At least one database must be configured.");

            var builder = new StringBuilder();
            var queryTypeName = "multiDatabase";
            var mutationTypeName = "multiDatabaseInput";

            builder.AppendLine($"schema {{ query: {queryTypeName} mutation: {mutationTypeName} }}");

            // Root query type: one field per database
            builder.AppendLine($"type {queryTypeName} {{");
            foreach (var (alias, _) in databaseFields)
            {
                builder.AppendLine($"  {alias}: {GetDbQueryTypeName(alias)}");
            }
            builder.AppendLine("}");

            // Root mutation type: one field per database
            builder.AppendLine($"type {mutationTypeName} {{");
            foreach (var (alias, _) in databaseFields)
            {
                builder.AppendLine($"  {alias}: {GetDbMutationTypeName(alias)}");
            }
            builder.AppendLine("}");

            // Per-database query and mutation types
            foreach (var (alias, model) in databaseFields)
            {
                AppendDatabaseTypes(builder, alias, model);
            }

            // Shared scalar and utility types
            AppendSharedTypes(builder, databaseFields);

            return builder.ToString();
        }

        /// <summary>
        /// Returns the GraphQL type name for a database's query type.
        /// </summary>
        public static string GetDbQueryTypeName(string alias) => $"{alias}Query";

        /// <summary>
        /// Returns the GraphQL type name for a database's mutation type.
        /// </summary>
        public static string GetDbMutationTypeName(string alias) => $"{alias}Mutation";

        private static void AppendDatabaseTypes(StringBuilder builder, string alias, IDbModel model)
        {
            var tableGenerators = model.Tables
                .Select(t => new TableSchemaGenerator(t))
                .ToList();

            // Database query type with table fields
            builder.AppendLine($"type {GetDbQueryTypeName(alias)} {{");
            foreach (var generator in tableGenerators)
            {
                builder.AppendLine(generator.GetTableFieldDefinition());
            }
            builder.AppendLine("}");

            // Database mutation type with table mutation fields
            builder.AppendLine($"type {GetDbMutationTypeName(alias)} {{");
            foreach (var generator in tableGenerators)
            {
                builder.AppendLine(generator.GetInputFieldDefinition());
            }
            builder.AppendLine("}");

            // Table types, filter types, input types for this database
            foreach (var generator in tableGenerators)
            {
                builder.AppendLine(generator.GetTableTypeDefinition(model, true));
                builder.AppendLine(generator.GetPagedTableTypeDefinition());
                builder.AppendLine(generator.GetDynamicJoinDefinition(model, false));
                builder.AppendLine(generator.GetDynamicJoinDefinition(model, true));
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
        }

        private static void AppendSharedTypes(StringBuilder builder, IReadOnlyDictionary<string, IDbModel> databaseFields)
        {
            // Collect all distinct GraphQL filter types across all databases
            var allDataTypes = databaseFields.Values
                .SelectMany(m => m.Tables)
                .SelectMany(t => t.Columns)
                .Select(c => c.EffectiveDataType)
                .Distinct()
                .ToList();

            foreach (var dataType in allDataTypes)
            {
                var typeName = SchemaGenerator.GetFilterInputTypeName(dataType);
                builder.AppendLine($"input {typeName} {{");
                builder.AppendLine($"  _eq: {SchemaGenerator.GetGraphQlTypeName(dataType, true)}");
                builder.AppendLine($"  _neq: {SchemaGenerator.GetGraphQlTypeName(dataType, true)}");
                builder.AppendLine("}");
            }
        }

        /// <summary>
        /// Returns a summary of the multi-database schema structure for diagnostics.
        /// Lists each database alias and its table count.
        /// </summary>
        public static IReadOnlyDictionary<string, int> GetSchemaInfo(IReadOnlyDictionary<string, IDbModel> databaseFields)
        {
            return databaseFields.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Tables.Count);
        }
    }
}
