using System;
using System.Collections.Generic;
using System.Linq;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Storage;

namespace BifrostQL.Core.Model
{
    /// <summary>
    /// Fail-fast validation for stringly-typed metadata configs. Runs once, after
    /// the model is fully built (so <c>ColumnLookup</c>/<c>GraphQlLookup</c> and all
    /// applied metadata are available), and aggregates every structural problem into
    /// a single descriptive <see cref="InvalidOperationException"/> rather than letting
    /// a typo surface only as a runtime <see cref="BifrostExecutionError"/> on the
    /// first query.
    ///
    /// All parsing reuses the existing collectors/transformers
    /// (<see cref="ComputedColumnConfigCollector"/>, <see cref="StateMachineConfigCollector"/>,
    /// <see cref="AutoFilterTransformer.ParseMappings"/>) so validation never re-derives
    /// the string formats and cannot drift from runtime behavior.
    ///
    /// OUT OF SCOPE: provider-registered-in-DI checks. There is no DI container at
    /// model build time, so a <c>computed-plugin</c>/<c>validation-plugin</c> provider
    /// NAME cannot be verified to be registered here — only structural/column validity
    /// is checked.
    /// </summary>
    internal static class ModelConfigValidator
    {
        public static void Validate(IDbModel model)
        {
            var errors = new List<string>();

            foreach (var table in model.Tables)
            {
                ValidateComputedColumns(table, errors);
                ValidateColumnReference(table, MetadataKeys.Security.TenantFilter, errors);
                ValidateColumnReference(table, MetadataKeys.SoftDelete.Column, errors);
                ValidateColumnReference(table, MetadataKeys.SoftDelete.DeletedBy, errors);
                ValidateAutoFilter(table, errors);
                ValidateStateMachine(table, errors);
            }

            if (errors.Count > 0)
            {
                throw new InvalidOperationException(
                    "Invalid BifrostQL metadata configuration:" + Environment.NewLine +
                    string.Join(Environment.NewLine, errors));
            }
        }

        private static void ValidateComputedColumns(IDbTable table, List<string> errors)
        {
            IReadOnlyList<ComputedColumnDefinition> definitions;
            try
            {
                definitions = ComputedColumnConfigCollector.FromTable(table);
            }
            catch (Exception ex)
            {
                // FromTable parses computed-sql, computed-plugin, and file-folder; we
                // cannot tell which one threw, so attribute the failure to whichever
                // computed key is actually present (rather than always blaming sql).
                var (key, value) = FirstPresentComputedKey(table);
                errors.Add(Problem(table, key, value, ex.Message));
                return;
            }

            foreach (var definition in definitions)
            {
                // Attribute the problem to the metadata key that produced the definition.
                // computed-sql carries placeholders in Dependencies; computed-plugin carries
                // depends= columns; file-folder (Provider + Options) carries depends= columns too.
                var key = definition.Kind == ComputedColumnKind.Sql
                    ? MetadataKeys.Computed.Sql
                    : definition.Options != null
                        ? MetadataKeys.FileStorage.Folder
                        : MetadataKeys.Computed.Provider;

                if (definition.Kind == ComputedColumnKind.Provider
                    && string.IsNullOrWhiteSpace(definition.ExpressionOrProvider))
                {
                    errors.Add(Problem(table, key, definition.Name, "provider name is empty"));
                }

                foreach (var dependency in definition.Dependencies)
                {
                    if (!ColumnExists(table, dependency))
                    {
                        errors.Add(Problem(table, key, dependency,
                            $"computed column '{definition.Name}' references a column that does not exist"));
                    }
                }
            }
        }

        private static void ValidateColumnReference(IDbTable table, string metadataKey, List<string> errors)
        {
            var column = table.GetMetadataValue(metadataKey);
            if (string.IsNullOrWhiteSpace(column))
                return;

            if (!DbColumnExists(table, column))
                errors.Add(Problem(table, metadataKey, column, "column does not exist"));
        }

        private static void ValidateAutoFilter(IDbTable table, List<string> errors)
        {
            var raw = table.GetMetadataValue(MetadataKeys.Security.AutoFilter);
            if (string.IsNullOrWhiteSpace(raw))
                return;

            List<AutoFilterMapping> mappings;
            try
            {
                mappings = AutoFilterTransformer.ParseMappings(raw, $"{table.TableSchema}.{table.DbName}");
            }
            catch (Exception ex)
            {
                errors.Add(Problem(table, MetadataKeys.Security.AutoFilter, raw, ex.Message));
                return;
            }

            foreach (var mapping in mappings)
            {
                if (!DbColumnExists(table, mapping.Column))
                    errors.Add(Problem(table, MetadataKeys.Security.AutoFilter, mapping.Column, "auto-filter column does not exist"));
            }
        }

        private static void ValidateStateMachine(IDbTable table, List<string> errors)
        {
            StateMachineDefinition? definition;
            try
            {
                definition = StateMachineConfigCollector.FromTable(table);
            }
            catch (Exception ex)
            {
                errors.Add(Problem(table, MetadataKeys.StateMachine.StateColumn,
                    table.GetMetadataValue(MetadataKeys.StateMachine.StateColumn), ex.Message));
                return;
            }

            if (definition == null)
                return;

            if (!DbColumnExists(table, definition.StateColumn))
                errors.Add(Problem(table, MetadataKeys.StateMachine.StateColumn, definition.StateColumn, "state column does not exist"));
        }

        /// <summary>
        /// For computed-column dependencies only. Mirrors runtime dependency resolution
        /// (<see cref="ComputedColumnDefinition.ResolveDependencyColumn"/>): a reference is
        /// valid if it matches either a GraphQL name or a DB column name.
        /// </summary>
        private static bool ColumnExists(IDbTable table, string name)
            => !string.IsNullOrWhiteSpace(name)
               && (table.GraphQlLookup.ContainsKey(name) || table.ColumnLookup.ContainsKey(name));

        /// <summary>
        /// For tenant-filter / soft-delete / auto-filter / state-machine column
        /// references. These resolve at runtime through <c>ColumnLookup</c> ONLY (the DB
        /// name) — e.g. <c>SingleColumnFilterTransformerBase</c>, <c>MutationTransformerBase</c>,
        /// <c>AutoFilterTransformer</c>, and the state-column SQL paths. A GraphQL-only
        /// name would pass a dual lookup yet fail at runtime, so it must be rejected here.
        /// <c>ColumnLookup</c> is OrdinalIgnoreCase, so DB names match case-insensitively.
        /// </summary>
        private static bool DbColumnExists(IDbTable table, string name)
            => !string.IsNullOrWhiteSpace(name) && table.ColumnLookup.ContainsKey(name);

        /// <summary>
        /// Picks the computed-* metadata key actually present on the table so a parse
        /// failure is attributed to the real source rather than always to computed-sql.
        /// </summary>
        private static (string Key, string? Value) FirstPresentComputedKey(IDbTable table)
        {
            var sql = table.GetMetadataValue(MetadataKeys.Computed.Sql);
            if (!string.IsNullOrWhiteSpace(sql))
                return (MetadataKeys.Computed.Sql, sql);

            var plugin = table.GetMetadataValue(MetadataKeys.Computed.Provider);
            if (!string.IsNullOrWhiteSpace(plugin))
                return (MetadataKeys.Computed.Provider, plugin);

            return (MetadataKeys.FileStorage.Folder, table.GetMetadataValue(MetadataKeys.FileStorage.Folder));
        }

        private static string Problem(IDbTable table, string metadataKey, string? value, string reason)
            => $"  {table.TableSchema}.{table.DbName} [{metadataKey}]: '{value}' - {reason}";
    }
}
