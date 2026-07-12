using System;
using System.Collections.Generic;
using System.Linq;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Model.AppSchema;
using BifrostQL.Core.Model.Relationships;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Cdc;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.Modules.History;
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
                ValidateUnknownMetadataKeys(
                    model.Metadata, MetadataValidator.KnownDatabaseKeys, "database", ":root", errors);
            }

            foreach (var table in model.Tables)
            {
                ValidateComputedColumns(table, errors);
                ValidateColumnReference(table, MetadataKeys.Security.TenantFilter, errors);
                ValidateColumnReference(table, MetadataKeys.SoftDelete.Column, errors);
                ValidateColumnReference(table, MetadataKeys.SoftDelete.DeletedBy, errors);
                ValidateColumnReference(table, MetadataKeys.Concurrency.Token, errors);
                ValidateSoftDeleteNullability(table, errors);
                ValidateAutoFilter(table, errors);
                ValidateStateMachine(table, errors);
                ValidatePolicy(table, errors);
                ValidateCdcTokens(table, errors);
                ValidateHistoryTokens(table, errors);

                var tableRef = $"{table.TableSchema}.{table.DbName}";
                ValidateMetadataKeyCasing(table.Metadata, MetadataValidator.KnownTableKeys, "table", tableRef, errors);
                ValidateUnknownMetadataKeys(table.Metadata, MetadataValidator.KnownTableKeys, "table", tableRef, errors);

                foreach (var column in table.Columns)
                {
                    var columnRef = $"{tableRef}.{column.ColumnName}";
                    ValidateMetadataKeyCasing(
                        column.Metadata, MetadataValidator.KnownColumnKeys, "column", columnRef, errors);
                    ValidateUnknownMetadataKeys(
                        column.Metadata, MetadataValidator.KnownColumnKeys, "column", columnRef, errors);
                    ValidateAutoPopulate(table, column, errors);
                    ValidateCrypto(table, column, errors);
                }
            }

            ValidateEavConfigs(model, errors);
            ValidateCdcOutbox(model, errors);
            ValidateHistoryTargets(model, errors);

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

        /// <summary>
        /// Central unknown-key gate: every metadata key that is neither in the built-in
        /// allow-list (<paramref name="knownKeys"/>, matched case-insensitively) nor a
        /// consumer extension key (<see cref="MetadataValidator.ConsumerExtensionPrefix"/>)
        /// is a hard error. Previously an unrecognized key produced only an advisory warning
        /// and then silently no-op'd — the exact failure the module-security audit flagged:
        /// a typo'd security key (e.g. <c>soft-delte</c> for <c>soft-delete</c>) disables the
        /// feature with no error. A deliberate custom key opts out of the gate by carrying the
        /// <c>x-</c> prefix; anything else fails fast at model load. Miscased-but-recognized
        /// keys are contained by the case-insensitive allow-list here and reported separately
        /// by <see cref="ValidateMetadataKeyCasing"/>, so they are not double-reported.
        /// </summary>
        private static void ValidateUnknownMetadataKeys(
            IDictionary<string, object?> metadata,
            HashSet<string> knownKeys,
            string scope,
            string reference,
            List<string> errors)
        {
            foreach (var key in metadata.Keys)
            {
                if (knownKeys.Contains(key))
                    continue; // Recognized (any casing); casing mismatch handled elsewhere.

                if (MetadataValidator.IsConsumerExtensionKey(key))
                    continue; // Intentional consumer extension key — never interpreted.

                errors.Add(
                    $"  {reference} [{key}]: unrecognized {scope} metadata key. Every built-in key is a " +
                    $"fixed name, so this is treated as a typo that would silently do nothing. If it is a " +
                    $"deliberate custom key, prefix it with '{MetadataValidator.ConsumerExtensionPrefix}' " +
                    $"(e.g. '{MetadataValidator.ConsumerExtensionPrefix}{key}').");
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

        /// <summary>
        /// Fail-fast validation for the authorization policy (<c>policy-*</c>). Every
        /// column named in <c>policy-read-deny</c> / <c>policy-write-deny</c> /
        /// <c>policy-row-scope</c> must exist on the table. The evaluator matches deny
        /// lists by string, so a typo'd deny column silently protects NOTHING — an
        /// absent column is never in the deny set, which reads as ALLOW (fail open).
        /// Unrecognized <c>policy-actions</c> tokens are surfaced too (the collector
        /// itself throws on them, for the same fail-open reason).
        /// </summary>
        private static void ValidatePolicy(IDbTable table, List<string> errors)
        {
            Auth.TablePolicy policy;
            try
            {
                policy = Auth.PolicyConfigCollector.FromTable(table);
            }
            catch (Exception ex)
            {
                errors.Add(Problem(table, MetadataKeys.Policy.Actions,
                    table.GetMetadataValue(MetadataKeys.Policy.Actions), ex.Message));
                return;
            }

            if (!policy.HasPolicy)
                return;

            foreach (var column in policy.ReadDenyColumns)
            {
                if (!DbColumnExists(table, column))
                    errors.Add(Problem(table, MetadataKeys.Policy.ReadDeny, column,
                        "policy-read-deny names a column that does not exist; a non-existent deny column protects nothing (the evaluator matches by name, so absent = ALLOW = fail open)"));
            }

            foreach (var column in policy.WriteDenyColumns)
            {
                if (!DbColumnExists(table, column))
                    errors.Add(Problem(table, MetadataKeys.Policy.WriteDeny, column,
                        "policy-write-deny names a column that does not exist; a non-existent deny column protects nothing (absent = ALLOW = fail open)"));
            }

            var rowScopeColumn = TryExtractRowScopeColumn(policy.RowScopeExpression);
            if (rowScopeColumn != null && !DbColumnExists(table, rowScopeColumn))
                errors.Add(Problem(table, MetadataKeys.Policy.RowScope, rowScopeColumn,
                    "policy-row-scope references a column that does not exist on the table"));
        }

        // Extracts the LHS column of a "column = {context-key}" row-scope expression
        // (the only grammar RowScopeCompiler supports). Returns null for a malformed
        // expression — the compiler itself fails closed on those at runtime, so the
        // validator does not double-report the shape here, only the column existence.
        private static string? TryExtractRowScopeColumn(string? expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
                return null;
            var idx = expression.IndexOf('=');
            if (idx <= 0)
                return null;
            var column = expression[..idx].Trim();
            return column.Length == 0 ? null : column;
        }

        /// <summary>
        /// Fail-fast validation for the audit <c>populate</c> column marker. A value
        /// outside the recognized populator set (e.g. <c>created_on</c> with an
        /// underscore instead of <c>created-on</c>) is silently ignored by
        /// <c>AuditMutationTransformer</c> — the column then never gets stamped and
        /// the audit trail has a permanent hole with no error. Reject it here.
        /// </summary>
        private static void ValidateAutoPopulate(IDbTable table, ColumnDto column, List<string> errors)
        {
            var populate = column.GetMetadataValue(MetadataKeys.AutoPopulate.Marker);
            if (string.IsNullOrWhiteSpace(populate))
                return;

            if (!MetadataKeys.AutoPopulate.KnownPopulators.Contains(populate))
                errors.Add(
                    $"  {table.TableSchema}.{table.DbName}.{column.ColumnName} [{MetadataKeys.AutoPopulate.Marker}]: " +
                    $"'{populate}' is not a recognized audit populator (expected one of " +
                    $"{string.Join(", ", MetadataKeys.AutoPopulate.KnownPopulators.OrderBy(p => p))}); " +
                    "the column would silently never be stamped.");
        }

        /// <summary>
        /// Fail-fast validation for a table's Change Data Capture opt-in
        /// (<c>emit-events</c> / <c>event-sink</c> / <c>event-payload</c>). An
        /// unrecognized operation, sink, or payload token is silently dropped by the
        /// parser's callers otherwise — the table would then either never emit or
        /// capture the wrong payload with no error. Reuses <see cref="CdcEventConfig.FromTable"/>
        /// so validation cannot drift from the runtime parse.
        /// </summary>
        private static void ValidateCdcTokens(IDbTable table, List<string> errors)
        {
            try
            {
                CdcEventConfig.FromTable(table);
            }
            catch (Exception ex)
            {
                errors.Add(Problem(table, MetadataKeys.Cdc.EmitEvents,
                    table.GetMetadataValue(MetadataKeys.Cdc.EmitEvents), ex.Message));
            }
        }

        /// <summary>
        /// Fail-fast validation for the transactional outbox contract. Once any table
        /// opts into <c>emit-events</c>, the model must name an <c>outbox-table</c>,
        /// that table must exist, and it must expose the full
        /// <see cref="MetadataKeys.Cdc.OutboxColumns"/> contract. A missing outbox or a
        /// column hole would surface only when the before-commit writer runs on the
        /// first mutation — aborting a real write in production — so catch it at model
        /// load instead.
        /// </summary>
        private static void ValidateCdcOutbox(IDbModel model, List<string> errors)
        {
            var emittingTables = model.Tables
                .Where(t =>
                {
                    try { return CdcEventConfig.FromTable(t).EmitsEvents; }
                    catch { return false; } // Token error already reported by ValidateCdcTokens.
                })
                .ToArray();

            if (emittingTables.Length == 0)
                return; // CDC not in use — outbox is optional.

            var outboxName = model.GetMetadataValue(MetadataKeys.Cdc.OutboxTable);
            if (string.IsNullOrWhiteSpace(outboxName))
            {
                errors.Add(
                    $"  :root [{MetadataKeys.Cdc.OutboxTable}]: {emittingTables.Length} table(s) set " +
                    $"'{MetadataKeys.Cdc.EmitEvents}' but no outbox table is configured; events have nowhere " +
                    "to be written. Set a model-level 'outbox-table' naming the transactional outbox.");
                return;
            }

            var outboxTable = FindTableByQualifiedName(model, outboxName);
            if (outboxTable == null)
            {
                errors.Add(
                    $"  :root [{MetadataKeys.Cdc.OutboxTable}]: '{outboxName}' does not name an existing table; " +
                    "the transactional outbox must exist in the database before events can be written.");
                return;
            }

            var missing = MetadataKeys.Cdc.OutboxColumns
                .Where(c => !DbColumnExists(outboxTable, c))
                .ToArray();

            if (missing.Length > 0)
            {
                errors.Add(
                    $"  :root [{MetadataKeys.Cdc.OutboxTable}]: outbox table '{outboxName}' is missing required " +
                    $"column(s): {string.Join(", ", missing)}. The outbox contract is: " +
                    $"{string.Join(", ", MetadataKeys.Cdc.OutboxColumns)}.");
            }
        }

        /// <summary>
        /// Fail-fast validation for a table's temporal-history opt-in (<c>history</c> /
        /// <c>history-table</c> / <c>history-columns</c>). An unrecognized operation token
        /// leaves the table with no trail; a <c>history-columns</c> entry naming a missing
        /// column silently drops that column from the diff. Both holes are invisible until
        /// someone needs the history that was never written. Reuses
        /// <see cref="HistoryConfig.FromTable"/> so validation cannot drift from the
        /// runtime parse.
        /// </summary>
        private static void ValidateHistoryTokens(IDbTable table, List<string> errors)
        {
            HistoryConfig config;
            try
            {
                config = HistoryConfig.FromTable(table);
            }
            catch (Exception ex)
            {
                errors.Add(Problem(table, MetadataKeys.History.Enabled,
                    table.GetMetadataValue(MetadataKeys.History.Enabled), ex.Message));
                return;
            }

            if (!config.RecordsHistory)
            {
                // history-table / history-columns without 'history' record nothing: the
                // author believes the table is tracked and it is not.
                foreach (var key in new[] { MetadataKeys.History.Table, MetadataKeys.History.Columns })
                {
                    var value = table.GetMetadataValue(key);
                    if (!string.IsNullOrWhiteSpace(value))
                        errors.Add(Problem(table, key, value,
                            $"set without '{MetadataKeys.History.Enabled}'; the table records no history, " +
                            "so this key has no effect."));
                }
                return;
            }

            // The writer names every trail row by the table's full primary key and reads
            // rows back by it: with no key column every insert fails at read-back and
            // every update/delete is vetoed — the config can never record anything.
            if (!table.KeyColumns.Any())
                errors.Add(Problem(table, MetadataKeys.History.Enabled,
                    table.GetMetadataValue(MetadataKeys.History.Enabled),
                    "records history but the table has no primary-key column; a history row " +
                    "could not name the row it describes."));

            foreach (var column in config.TrackedColumns)
            {
                if (!DbColumnExists(table, column))
                    errors.Add(Problem(table, MetadataKeys.History.Columns, column,
                        "history-columns names a column that does not exist; its changes would never be recorded"));
            }
        }

        /// <summary>
        /// Fail-fast validation of the history-table contract. Every history-enabled table
        /// must resolve to a history table — its own <c>history-table</c> override, else the
        /// model-level default — that exists and carries the full
        /// <see cref="MetadataKeys.History.HistoryColumns"/> contract. Per-table and shared
        /// history tables share one shape, so one check covers both. Also rejects a table
        /// pointed at itself and a history table that itself records history: either would
        /// make the writer recurse into the trail it is writing.
        /// </summary>
        private static void ValidateHistoryTargets(IDbModel model, List<string> errors)
        {
            var configs = new List<(IDbTable Table, HistoryConfig Config)>();
            foreach (var table in model.Tables)
            {
                HistoryConfig config;
                try { config = HistoryConfig.FromTable(table); }
                catch { continue; } // Token error already reported by ValidateHistoryTokens.

                if (config.RecordsHistory)
                    configs.Add((table, config));
            }

            if (configs.Count == 0)
                return; // History not in use — a model-level history-table is simply unused.

            var sharedDefault = model.GetMetadataValue(MetadataKeys.History.Table);

            foreach (var (table, config) in configs)
            {
                var targetName = config.HistoryTableOverride ?? sharedDefault;
                if (string.IsNullOrWhiteSpace(targetName))
                {
                    errors.Add(Problem(table, MetadataKeys.History.Enabled,
                        table.GetMetadataValue(MetadataKeys.History.Enabled),
                        $"records history but no '{MetadataKeys.History.Table}' is configured on the table " +
                        "or on the model; history rows have nowhere to be written."));
                    continue;
                }

                var target = FindTableByQualifiedName(model, targetName);
                if (target == null)
                {
                    errors.Add(Problem(table, MetadataKeys.History.Table, targetName,
                        "does not name an existing table; the history table must exist in the database " +
                        "before changes can be recorded."));
                    continue;
                }

                if (ReferenceEquals(target, table))
                {
                    errors.Add(Problem(table, MetadataKeys.History.Table, targetName,
                        "names the tracked table itself; each recorded change would write a row into the " +
                        "table being tracked."));
                    continue;
                }

                if (HistoryRecords(target))
                {
                    errors.Add(Problem(table, MetadataKeys.History.Table, targetName,
                        $"names a table that itself sets '{MetadataKeys.History.Enabled}'; a history table " +
                        "cannot be tracked, or writing a history row would record a change of its own."));
                    continue;
                }

                var missing = MetadataKeys.History.HistoryColumns
                    .Where(c => !DbColumnExists(target, c))
                    .ToArray();

                if (missing.Length > 0)
                {
                    errors.Add(Problem(table, MetadataKeys.History.Table, targetName,
                        $"history table is missing required column(s): {string.Join(", ", missing)}. " +
                        $"The history contract is: {string.Join(", ", MetadataKeys.History.HistoryColumns)}."));
                }
            }
        }

        private static bool HistoryRecords(IDbTable table)
        {
            try { return HistoryConfig.FromTable(table).RecordsHistory; }
            catch { return false; } // Token error already reported by ValidateHistoryTokens.
        }

        // Resolution of an outbox-table / history-table reference is shared with the runtime
        // writers (see ModelTableReference) so a reference that validates at model load
        // resolves to the same table when the writer runs.
        private static IDbTable? FindTableByQualifiedName(IDbModel model, string qualified)
            => ModelTableReference.Find(model, qualified);

        /// <summary>
        /// Fail-fast validation for field-level encryption (<c>encrypt</c> / <c>key-ref</c>
        /// / <c>mask</c> / <c>unmask-role</c> / <c>blind-index</c>). These are security
        /// keys: a typo'd algorithm, an unparseable key-ref, or a blind-index naming a
        /// missing column would either fail to encrypt (leaking plaintext at rest) or
        /// throw deep in the mutation pipeline on the first write. Reject at model load.
        /// </summary>
        private static void ValidateCrypto(IDbTable table, ColumnDto column, List<string> errors)
        {
            var algorithm = column.GetMetadataValue(MetadataKeys.Crypto.Encrypt);
            var keyRef = column.GetMetadataValue(MetadataKeys.Crypto.KeyRef);
            var mask = column.GetMetadataValue(MetadataKeys.Crypto.Mask);
            var blindIndex = column.GetMetadataValue(MetadataKeys.Crypto.BlindIndex);

            var columnRef = $"{table.TableSchema}.{table.DbName}.{column.ColumnName}";
            var encryptOptedIn = !string.IsNullOrWhiteSpace(algorithm);

            // key-ref / mask / blind-index only make sense on an encrypted column; a
            // stray one without encrypt is a misconfiguration that silently does nothing.
            if (!encryptOptedIn)
            {
                foreach (var (key, value) in new[]
                {
                    (MetadataKeys.Crypto.KeyRef, keyRef),
                    (MetadataKeys.Crypto.Mask, mask),
                    (MetadataKeys.Crypto.BlindIndex, blindIndex),
                    (MetadataKeys.Crypto.UnmaskRole, column.GetMetadataValue(MetadataKeys.Crypto.UnmaskRole)),
                })
                {
                    if (!string.IsNullOrWhiteSpace(value))
                        errors.Add(
                            $"  {columnRef} [{key}]: set without '{MetadataKeys.Crypto.Encrypt}'; " +
                            "encryption metadata has no effect on a column that is not encrypted.");
                }
                return;
            }

            if (!MetadataKeys.Crypto.Algorithms.Contains(algorithm!))
                errors.Add(Problem(table, MetadataKeys.Crypto.Encrypt, algorithm,
                    $"unsupported encryption algorithm (expected one of {string.Join(", ", MetadataKeys.Crypto.Algorithms)})"));

            // key-ref is required, and must parse to a recognized provider:id.
            if (string.IsNullOrWhiteSpace(keyRef))
                errors.Add(
                    $"  {columnRef} [{MetadataKeys.Crypto.KeyRef}]: an encrypted column requires a key-ref " +
                    $"(e.g. 'kms:pii' or 'config:pii').");
            else if (!BifrostQL.Core.Crypto.KeyRef.TryParse(keyRef, out _))
                errors.Add(Problem(table, MetadataKeys.Crypto.KeyRef, keyRef,
                    $"malformed key-ref (expected 'provider:id' with provider one of {string.Join(", ", MetadataKeys.Crypto.KeyRefProviders)})"));

            if (!string.IsNullOrWhiteSpace(mask) && !MetadataKeys.Crypto.MaskModes.Contains(mask))
                errors.Add(Problem(table, MetadataKeys.Crypto.Mask, mask,
                    $"unrecognized mask mode (expected one of {string.Join(", ", MetadataKeys.Crypto.MaskModes)})"));

            if (!string.IsNullOrWhiteSpace(blindIndex) && !DbColumnExists(table, blindIndex))
                errors.Add(Problem(table, MetadataKeys.Crypto.BlindIndex, blindIndex,
                    "blind-index names a column that does not exist on the table"));
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
