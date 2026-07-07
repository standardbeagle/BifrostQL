using System;
using System.Collections.Generic;
using System.Linq;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Model.AppSchema;
using BifrostQL.Core.Model.Relationships;
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

            if (model.Metadata != null)
            {
                ValidateMetadataKeyCasing(
                    model.Metadata, MetadataValidator.KnownDatabaseKeys, "database", ":root", errors);
            }

            foreach (var table in model.Tables)
            {
                ValidateComputedColumns(table, errors);
                ValidateColumnReference(table, MetadataKeys.Security.TenantFilter, errors);
                ValidateColumnReference(table, MetadataKeys.SoftDelete.Column, errors);
                ValidateColumnReference(table, MetadataKeys.SoftDelete.DeletedBy, errors);
                ValidateSoftDeleteNullability(table, errors);
                ValidateAutoFilter(table, errors);
                ValidateStateMachine(table, errors);

                var tableRef = $"{table.TableSchema}.{table.DbName}";
                ValidateMetadataKeyCasing(table.Metadata, MetadataValidator.KnownTableKeys, "table", tableRef, errors);

                foreach (var column in table.Columns)
                {
                    ValidateMetadataKeyCasing(
                        column.Metadata, MetadataValidator.KnownColumnKeys, "column", $"{tableRef}.{column.ColumnName}", errors);
                }
            }

            ValidateEavConfigs(model, errors);

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

        /// <summary>
        /// <c>soft-delete</c> / <c>soft-delete-by</c> columns are used exclusively
        /// via an <c>IS NULL</c> predicate (<c>SoftDeleteFilterTransformer</c>) and
        /// an equality-to-NULL-or-value rewrite on delete/restore. A NOT NULL
        /// column can never satisfy <c>IS NULL</c>, so every query would silently
        /// return zero rows forever — the whole table effectively vanishes with no
        /// error at startup or query time. Boolean-flag soft delete is a distinct,
        /// unsupported feature; fail fast instead of shipping a table that always
        /// reads empty.
        /// </summary>
        private static void ValidateSoftDeleteNullability(IDbTable table, List<string> errors)
        {
            ValidateNullableColumnReference(table, MetadataKeys.SoftDelete.Column, errors);
            ValidateNullableColumnReference(table, MetadataKeys.SoftDelete.DeletedBy, errors);
        }

        private static void ValidateNullableColumnReference(IDbTable table, string metadataKey, List<string> errors)
        {
            var columnName = table.GetMetadataValue(metadataKey);
            if (string.IsNullOrWhiteSpace(columnName))
                return;

            if (!table.ColumnLookup.TryGetValue(columnName, out var column))
                return; // Already reported by ValidateColumnReference.

            if (!column.IsNullable)
            {
                errors.Add(Problem(table, metadataKey, columnName,
                    $"column '{columnName}' is NOT NULL; soft-delete relies on an IS NULL / NOT NULL predicate " +
                    "and a NOT NULL column can never be soft-deleted or restored, hiding the whole table. " +
                    "Make the column nullable, or do not configure it as a soft-delete column."));
            }
        }

        /// <summary>
        /// Fail-fast validation for <c>eav-*</c> metadata. Partial configuration
        /// (some but not all of the four keys) and dangling/invalid column or
        /// parent-table references previously continued silently — the resulting
        /// EAV wiring either did nothing or threw a confusing runtime error deep in
        /// <c>EavMetaProvider</c> on first query. This mirrors the collector's own
        /// parsing (<see cref="EavConfigCollector"/>) so validation cannot drift
        /// from runtime behavior.
        /// </summary>
        private static void ValidateEavConfigs(IDbModel model, List<string> errors)
        {
            foreach (var table in model.Tables)
            {
                var parent = table.GetMetadataValue(MetadataKeys.Eav.Parent);
                var fk = table.GetMetadataValue(MetadataKeys.Eav.ForeignKey);
                var key = table.GetMetadataValue(MetadataKeys.Eav.Key);
                var value = table.GetMetadataValue(MetadataKeys.Eav.Value);

                var present = new[]
                {
                    (MetadataKeys.Eav.Parent, parent),
                    (MetadataKeys.Eav.ForeignKey, fk),
                    (MetadataKeys.Eav.Key, key),
                    (MetadataKeys.Eav.Value, value),
                }.Where(p => !string.IsNullOrWhiteSpace(p.Item2)).ToArray();

                if (present.Length == 0)
                    continue; // Not an EAV table.

                if (present.Length < 4)
                {
                    var missing = new[] { MetadataKeys.Eav.Parent, MetadataKeys.Eav.ForeignKey, MetadataKeys.Eav.Key, MetadataKeys.Eav.Value }
                        .Except(present.Select(p => p.Item1))
                        .ToArray();
                    errors.Add(Problem(table, present[0].Item1, present[0].Item2,
                        $"partial eav-* configuration; missing {string.Join(", ", missing)} (all four of eav-parent/eav-fk/eav-key/eav-value are required together)"));
                    continue;
                }

                var parentTable = model.Tables.FirstOrDefault(t =>
                    string.Equals(t.DbName, parent, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(t.TableSchema, table.TableSchema, StringComparison.OrdinalIgnoreCase))
                    ?? model.Tables.FirstOrDefault(t => string.Equals(t.DbName, parent, StringComparison.OrdinalIgnoreCase));

                if (parentTable == null)
                {
                    errors.Add(Problem(table, MetadataKeys.Eav.Parent, parent, "eav-parent does not name an existing table"));
                    continue;
                }

                if (!DbColumnExists(table, fk!))
                    errors.Add(Problem(table, MetadataKeys.Eav.ForeignKey, fk, "eav-fk column does not exist on the meta table"));

                if (!DbColumnExists(table, key!))
                    errors.Add(Problem(table, MetadataKeys.Eav.Key, key, "eav-key column does not exist on the meta table"));

                if (!DbColumnExists(table, value!))
                    errors.Add(Problem(table, MetadataKeys.Eav.Value, value, "eav-value column does not exist on the meta table"));
            }
        }

        /// <summary>
        /// Cross-checks a metadata dictionary's exact keys against a known-key
        /// allow-list that matches case-insensitively. The metadata dictionaries
        /// built off config sources use the framework-default (case-sensitive)
        /// comparer, so a recognized key typed with the wrong casing (e.g.
        /// <c>Soft-Delete</c> instead of <c>soft-delete</c>) silently fails every
        /// case-sensitive lookup performed by the transformers that read metadata
        /// directly — no "unknown key" warning is produced because this allow-list
        /// check is itself OrdinalIgnoreCase. Fail fast instead.
        /// </summary>
        private static void ValidateMetadataKeyCasing(
            IDictionary<string, object?> metadata,
            HashSet<string> knownKeys,
            string scope,
            string reference,
            List<string> errors)
        {
            foreach (var key in metadata.Keys)
            {
                if (!knownKeys.Contains(key))
                    continue; // Unknown entirely — reported by MetadataValidator's warnings, not an error here.

                var canonical = knownKeys.First(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                if (!string.Equals(canonical, key, StringComparison.Ordinal))
                {
                    errors.Add(
                        $"  {reference} [{key}]: recognized {scope} metadata key with incorrect casing " +
                        $"(expected '{canonical}'); the metadata dictionary is case-sensitive, so this key " +
                        "is silently ignored by every module that reads it.");
                }
            }
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
