using System;
using System.Collections.Generic;
using System.Linq;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Model.AppSchema;
using BifrostQL.Core.Model.Relationships;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Cdc;
using BifrostQL.Core.Modules.Chat;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.Modules.Deferred;
using BifrostQL.Core.Modules.History;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Storage;
using BifrostQL.Core.Utils;

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
                ValidateFtsColumns(table, errors);
                ValidateRetention(table, errors);
                ValidateDeferred(table, errors);
                ValidateApproval(table, errors);
                ValidateFeed(table, errors);

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
            ValidateCdcSubscription(model, errors);
            ValidateHistoryTargets(model, errors);
            ValidateDeferredStores(model, errors);
            ValidateChat(model, errors);
            ValidateChatConnectors(model, errors);
            ValidatePrometheusMetrics(model, errors);
            ValidateLdap(model, errors);

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
                // CS0618: validating the deprecated-but-functional raw-SQL kind still requires
                // naming it here.
#pragma warning disable CS0618
                var key = definition.Kind == ComputedColumnKind.Sql
                    ? MetadataKeys.Computed.Sql
                    : definition.Options != null
                        ? MetadataKeys.FileStorage.Folder
                        : MetadataKeys.Computed.Provider;
#pragma warning restore CS0618

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

        /// <summary>
        /// Fail-fast validation for a table's full-text search opt-in (<c>search</c> /
        /// <c>search-language</c>). A <c>search</c> list naming a missing or non-string
        /// column would surface the <c>_search</c> operator on a table whose match target
        /// is meaningless (or empty), with no error until the per-dialect lowering ran on
        /// the first query. Reuses <see cref="Modules.Fts.FtsConfig.FromTable"/> so
        /// validation cannot drift from the runtime parse.
        /// </summary>
        private static void ValidateFtsColumns(IDbTable table, List<string> errors)
        {
            try
            {
                Modules.Fts.FtsConfig.FromTable(table);
            }
            catch (Exception ex)
            {
                errors.Add(Problem(table, MetadataKeys.Fts.Search,
                    table.GetMetadataValue(MetadataKeys.Fts.Search), ex.Message));
            }
        }

        /// <summary>
        /// Fail-fast validation for a table's data-retention opt-in (<c>retain</c> / <c>ttl</c>).
        /// A malformed duration, a <c>retain</c> without a <c>soft-delete</c> anchor, a <c>ttl</c>
        /// without its explicit <c>after &lt;column&gt;</c> anchor, or an anchor column that does not
        /// exist or is not a timestamp would otherwise leave a table that reads as retention-managed
        /// in config either purging nothing (a silent compliance gap) or built to purge the wrong
        /// rows — invisible until the scheduler slice runs. Reuses
        /// <see cref="Modules.Retention.RetentionConfig.FromTable"/> so validation cannot drift from
        /// the runtime parse.
        /// </summary>
        private static void ValidateRetention(IDbTable table, List<string> errors)
        {
            try
            {
                Modules.Retention.RetentionConfig.FromTable(table);
            }
            catch (Exception ex)
            {
                // Attribute to whichever key is present (retain if both) — the exception
                // message itself names the offending key precisely.
                var key = !string.IsNullOrWhiteSpace(table.GetMetadataValue(MetadataKeys.Retention.Retain))
                    ? MetadataKeys.Retention.Retain
                    : MetadataKeys.Retention.Ttl;
                errors.Add(Problem(table, key, table.GetMetadataValue(key), ex.Message));
            }
        }

        /// <summary>
        /// A deferred reversal relies on an optimistic token to avoid undoing over a newer
        /// write, and on temporal history to preserve the original audit trail. Neither may
        /// be inferred or defaulted: a marked table missing either prerequisite is invalid.
        /// </summary>
        private static void ValidateDeferred(IDbTable table, List<string> errors)
        {
            DeferredConfig config;
            try
            {
                config = DeferredConfig.FromTable(table);
            }
            catch (Exception ex)
            {
                errors.Add(Problem(table, MetadataKeys.Deferred.Deferrable,
                    table.GetMetadataValue(MetadataKeys.Deferred.Deferrable), ex.Message));
                return;
            }

            if (!config.IsDeferrable)
                return;

            if (string.IsNullOrWhiteSpace(table.GetMetadataValue(MetadataKeys.Concurrency.Token)))
            {
                errors.Add(Problem(table, MetadataKeys.Deferred.Deferrable,
                    table.GetMetadataValue(MetadataKeys.Deferred.Deferrable),
                    $"requires '{MetadataKeys.Concurrency.Token}' so a reversal cannot overwrite a newer change"));
            }

            if (string.IsNullOrWhiteSpace(table.GetMetadataValue(MetadataKeys.History.Enabled)))
            {
                errors.Add(Problem(table, MetadataKeys.Deferred.Deferrable,
                    table.GetMetadataValue(MetadataKeys.Deferred.Deferrable),
                    $"requires '{MetadataKeys.History.Enabled}' so the applied change remains auditable"));
                return;
            }

            try
            {
                var history = HistoryConfig.FromTable(table);
                if (!history.TracksAllColumns)
                {
                    errors.Add(Problem(table, MetadataKeys.Deferred.Deferrable,
                        table.GetMetadataValue(MetadataKeys.Deferred.Deferrable),
                        $"cannot combine with '{MetadataKeys.History.Columns}'; deferred reversal reuses " +
                        "HistoryMutationHook's full before-image to reconstruct the original row."));
                }
            }
            catch (InvalidOperationException)
            {
                // ValidateHistoryTokens reports malformed history metadata.
            }
        }

        private static void ValidateDeferredStores(IDbModel model, List<string> errors)
        {
            var hasDeferrableTable = false;
            foreach (var table in model.Tables)
            {
                try
                {
                    hasDeferrableTable |= DeferredConfig.FromTable(table).IsDeferrable;
                }
                catch (InvalidOperationException)
                {
                    // ValidateDeferred already reports malformed deferred metadata.
                }
            }

            if (!hasDeferrableTable)
                return;

            ValidateDeferredStore(model, MetadataKeys.Deferred.ChangeSet.Table,
                MetadataKeys.Deferred.ChangeSet.Columns, errors);
            ValidateDeferredStore(model, MetadataKeys.Deferred.ChangeSetDelta.Table,
                MetadataKeys.Deferred.ChangeSetDelta.Columns, errors);
        }

        private static void ValidateDeferredStore(
            IDbModel model, string tableName, IReadOnlyList<string> requiredColumns, List<string> errors)
        {
            var table = ModelTableReference.Find(model, tableName);
            if (table is null)
            {
                errors.Add($"Deferred effects require store table '{tableName}', but it was not found in the model.");
                return;
            }

            foreach (var column in requiredColumns.Where(column => !DbColumnExists(table, column)))
                errors.Add($"Deferred store table '{table.TableSchema}.{table.DbName}' is missing required column '{column}'.");
        }

        /// <summary>
        /// Fail-fast validation for a table's approval opt-in (<c>approval</c> /
        /// <c>approver-role</c> / <c>self-approve</c>). An <c>approval</c> declaration with no
        /// <c>approver-role</c> is fail-open (a gate nobody can approve), and a bad
        /// <c>approval</c>/<c>self-approve</c> value would silently mis-gate — each would leave a
        /// table that reads as approval-managed in config either impossible to approve or
        /// ungated, invisible until the write-interception slice runs. Reuses
        /// <see cref="Modules.Approval.ApprovalConfig.FromTable"/> so validation cannot drift
        /// from the runtime parse.
        /// </summary>
        private static void ValidateApproval(IDbTable table, List<string> errors)
        {
            try
            {
                Modules.Approval.ApprovalConfig.FromTable(table);
            }
            catch (Exception ex)
            {
                errors.Add(Problem(table, MetadataKeys.Approval.Marker,
                    table.GetMetadataValue(MetadataKeys.Approval.Marker), ex.Message));
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

            // Resolved through ModelTableReference — the same lookup the runtime outbox
            // writer uses — so a reference that validates here resolves to the same table
            // when the writer runs.
            var outboxTable = ModelTableReference.Find(model, outboxName);
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
        /// Fail-fast validation for the model-level CDC subscription
        /// (<c>subscription-tables</c> / <c>subscription-tenant</c> /
        /// <c>subscription-redact</c>). The subscription routes fail-closed, so a malformed
        /// or dangling allow-list must be caught at model load rather than silently
        /// scoping delivery to nothing: a present-but-empty list value (<c>","</c>) and an
        /// allow-list entry that names no existing table are both rejected. Reuses
        /// <see cref="CdcSubscription.FromModel"/> for the present-but-empty guard so
        /// validation cannot drift from the runtime parse.
        /// </summary>
        private static void ValidateCdcSubscription(IDbModel model, List<string> errors)
        {
            try
            {
                CdcSubscription.FromModel(model);
            }
            catch (Exception ex)
            {
                // Present-but-empty list value; the message names the offending key.
                errors.Add($"  :root: {ex.Message}");
                return; // The table-existence loop below would re-throw the same parse error.
            }

            var tablesRaw = model.GetMetadataValue(MetadataKeys.Cdc.SubscriptionTables);
            if (string.IsNullOrWhiteSpace(tablesRaw))
                return; // No allow-list (subscription may be tenant/redact-only) — nothing to resolve.

            foreach (var entry in tablesRaw.Split(
                ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // Resolved through ModelTableReference — the same lookup the drain uses for
                // the outbox and aggregate tables — so an entry that validates here names
                // the same table the router gates on.
                if (ModelTableReference.Find(model, entry) is null)
                    errors.Add(
                        $"  :root [{MetadataKeys.Cdc.SubscriptionTables}]: '{entry}' does not name an existing " +
                        "table; a subscription allow-list entry must name a source table in the model.");
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

            foreach (var (table, config) in configs)
            {
                // Every history-enabled table generates a `<table>History` trail read
                // field on the root query type. A real table whose generated query
                // field carries that exact name would produce a duplicate GraphQL
                // field and crash (or shadow) at schema build — reject at model load,
                // naming both tables, while the config is still being written.
                var readField = Schema.HistorySurface.HistoryFieldName(table);
                var colliding = model.Tables.FirstOrDefault(t =>
                    !ReferenceEquals(t, table)
                    && string.Equals(t.GraphQlName, readField, StringComparison.Ordinal));
                if (colliding != null)
                {
                    errors.Add(Problem(table, MetadataKeys.History.Enabled,
                        table.GetMetadataValue(MetadataKeys.History.Enabled),
                        $"generates the trail read field '{readField}', which collides with the generated query " +
                        $"field of table '{colliding.TableSchema}.{colliding.DbName}' (GraphQL name " +
                        $"'{colliding.GraphQlName}'). Rename one of the tables, or disable history on " +
                        $"'{table.TableSchema}.{table.DbName}'."));
                }

                // A policy row scope is an arbitrary expression evaluated against the
                // TRACKED table's rows; the history table has no materialized column
                // to re-apply it to, so the generated trail read field cannot enforce
                // it — trail rows of rows the caller is scoped OUT of would be
                // readable. There is no per-table grant to opt the trail out of the
                // row scope, so the combination is rejected outright rather than
                // silently exposing scoped-out history.
                if (PolicyConfigCollector.FromTable(table).RowScopeExpression is not null)
                {
                    errors.Add(Problem(table, MetadataKeys.Policy.RowScope,
                        table.GetMetadataValue(MetadataKeys.Policy.RowScope),
                        $"cannot be combined with '{MetadataKeys.History.Enabled}': the policy row scope is an " +
                        "arbitrary expression with no materialized column on the history table, so the generated " +
                        $"'{readField}' trail read field cannot enforce it and would expose trail rows the caller's " +
                        "row scope hides. Remove the row scope, or disable history on this table."));
                }

                var targetName = config.ResolveTargetName(model);
                if (targetName is null)
                {
                    errors.Add(Problem(table, MetadataKeys.History.Enabled,
                        table.GetMetadataValue(MetadataKeys.History.Enabled),
                        $"records history but no '{MetadataKeys.History.Table}' is configured on the table " +
                        "or on the model; history rows have nowhere to be written."));
                    continue;
                }

                // Resolved through ModelTableReference — the same lookup the change-history
                // writer uses — so a reference that validates here resolves to the same
                // table when the writer runs.
                var target = ModelTableReference.Find(model, targetName);
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

                // A tenant-filtered tracked table materializes its tenant scope value into
                // every trail row (history reads are authorized by plain column predicates),
                // so the target must physically carry a column of the tenant column's name.
                // A shared target serving tables with DIFFERENT tenant column names must
                // carry every one of them — each table checks its own name here — or split
                // via per-table overrides.
                var scopeColumn = HistoryConfig.ResolveTenantScopeColumn(table);

                // A tenant column that shares a name with a fixed contract column would
                // pass the existence check below trivially — every target has 'op', 'id',
                // ... — and the writer's scope copy would then silently overwrite the
                // contract value in every trail row (or break on the identity column).
                // The contract names are the same on every target, so no history-table
                // override can resolve this; only renaming the column or opting the
                // table out of history can.
                if (scopeColumn is not null
                    && MetadataKeys.History.HistoryColumns.Contains(scopeColumn, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add(Problem(table, MetadataKeys.Security.TenantFilter, scopeColumn,
                        $"the tenant column's name collides with the history column contract " +
                        $"({string.Join(", ", MetadataKeys.History.HistoryColumns)}); materializing the tenant " +
                        $"scope would overwrite the trail's own '{scopeColumn}' value in every history row. " +
                        "Rename the tenant column or disable history on this table — the contract names are " +
                        $"fixed, so a '{MetadataKeys.History.Table}' override cannot avoid the collision."));
                    continue;
                }

                if (scopeColumn is not null && !DbColumnExists(target, scopeColumn))
                {
                    errors.Add(Problem(table, MetadataKeys.Security.TenantFilter, scopeColumn,
                        $"tracked table is tenant-filtered, but its history table '{targetName}' has no " +
                        $"'{scopeColumn}' column. Every trail row materializes the tracked row's tenant scope " +
                        "so history reads can be authorized by plain column predicates; add a nullable " +
                        $"'{scopeColumn}' column to '{targetName}', or point this table at its own " +
                        $"'{MetadataKeys.History.Table}' override."));
                }
            }
        }

        private static bool HistoryRecords(IDbTable table)
        {
            try { return HistoryConfig.FromTable(table).RecordsHistory; }
            catch { return false; } // Token error already reported by ValidateHistoryTokens.
        }

        /// <summary>
        /// Fail-fast validation for the chat metadata contract (<c>chat-*</c>). The chat
        /// tables are user-supplied, so every structural assumption the chat surface will
        /// make must hold at model load: exactly one conversations/messages pair, mapped
        /// columns that exist with the right types, a message FK that actually reaches
        /// the conversations table's single-column PK, published tables, and no overlap
        /// with history TARGETS (chat tables MAY be history-ENABLED — that composes).
        /// Reuses <see cref="ChatConfig.FromTable"/> / <see cref="ChatConfig.FromModel"/>
        /// so validation cannot drift from the runtime parse.
        /// </summary>
        private static void ValidateChat(IDbModel model, List<string> errors)
        {
            // Per-table parse errors first (unknown tokens, present-but-empty values,
            // mapping keys on the wrong table, incomplete messages mapping), attributed
            // to the offending table + key.
            var parseFailed = false;
            foreach (var table in model.Tables)
            {
                try
                {
                    ChatConfig.FromTable(table);
                }
                catch (Exception ex)
                {
                    var key = FirstPresentChatKey(table);
                    errors.Add(Problem(table, key, table.GetMetadataValue(key), ex.Message));
                    parseFailed = true;
                }
            }

            if (parseFailed)
                return; // Pair resolution would re-throw the same parse errors.

            ChatModelConfig? chat;
            try
            {
                chat = ChatConfig.FromModel(model);
            }
            catch (Exception ex)
            {
                // Pair-level problem (multiples, half a pair); the message names the
                // offending tables and keys.
                errors.Add($"  {ex.Message}");
                return;
            }

            if (chat == null)
                return; // Chat not in use.

            ValidateChatTablePublication(model, chat.ConversationsTable, MetadataKeys.Chat.Conversations, errors);
            ValidateChatTablePublication(model, chat.MessagesTable, MetadataKeys.Chat.Messages, errors);
            ValidateChatConversations(chat, errors);
            ValidateChatMessages(chat, errors);
        }

        // Both chat tables must be ordinary published tables: the chat surface is built
        // on the generated schema, and history targets are system tables whose reads the
        // intent executors reject outright.
        private static void ValidateChatTablePublication(
            IDbModel model, IDbTable table, string optInKey, List<string> errors)
        {
            if (table.CompareMetadata(MetadataKeys.Ui.Visibility, MetadataKeys.Ui.Hidden))
                errors.Add(Problem(table, optInKey, table.GetMetadataValue(optInKey),
                    $"a chat table must be published; '{MetadataKeys.Ui.Visibility}: {MetadataKeys.Ui.Hidden}' " +
                    "removes it from the schema the chat surface is built on. Unhide the table or remove the chat opt-in."));

            if (Schema.HistorySurface.IsHistoryTarget(model, table))
                errors.Add(Problem(table, optInKey, table.GetMetadataValue(optInKey),
                    $"a chat table cannot be a history target (a table named by some '{MetadataKeys.History.Table}'): " +
                    "history targets are unpublished system tables whose reads are rejected. Point that " +
                    $"'{MetadataKeys.History.Table}' elsewhere. A chat table MAY itself set " +
                    $"'{MetadataKeys.History.Enabled}' — recording chat history composes."));
        }

        private static void ValidateChatConversations(ChatModelConfig chat, List<string> errors)
        {
            var table = chat.ConversationsTable;
            var pks = table.KeyColumns.ToArray();

            // The conversation FK is a single column, so the conversations PK must be a
            // single column too — a composite PK cannot be referenced by it.
            if (pks.Length == 0)
                errors.Add(Problem(table, MetadataKeys.Chat.Conversations,
                    table.GetMetadataValue(MetadataKeys.Chat.Conversations),
                    "the conversations table has no primary-key column; messages reference conversations by primary key."));
            else if (pks.Length > 1)
                errors.Add(Problem(table, MetadataKeys.Chat.Conversations,
                    table.GetMetadataValue(MetadataKeys.Chat.Conversations),
                    $"the conversations table has a composite primary key ({string.Join(", ", pks.Select(c => c.ColumnName))}); " +
                    $"'{MetadataKeys.Chat.ConversationFk}' is a single column, so a single-column primary key is required."));
            else
                ValidateChatOrderingKey(table, MetadataKeys.Chat.Conversations, pks[0],
                    "the chat surface lists conversations newest first by ordering on this key descending", errors);

            var title = chat.ConversationsConfig.TitleColumn;
            if (title != null && !DbColumnExists(table, title))
                errors.Add(Problem(table, MetadataKeys.Chat.Title, title,
                    "column does not exist on the conversations table"));
        }

        private static void ValidateChatMessages(ChatModelConfig chat, List<string> errors)
        {
            var table = chat.MessagesTable;
            var config = chat.MessagesConfig;

            var pks = table.KeyColumns.ToArray();
            if (pks.Length == 0)
                errors.Add(Problem(table, MetadataKeys.Chat.Messages,
                    table.GetMetadataValue(MetadataKeys.Chat.Messages),
                    "the messages table has no primary-key column."));
            else if (pks.Length > 1)
                errors.Add(Problem(table, MetadataKeys.Chat.Messages,
                    table.GetMetadataValue(MetadataKeys.Chat.Messages),
                    $"the messages table has a composite primary key ({string.Join(", ", pks.Select(c => c.ColumnName))}); " +
                    "message paging breaks created-at ties by ordering on a single key column, so a single-column " +
                    "primary key is required."));
            else
                ValidateChatOrderingKey(table, MetadataKeys.Chat.Messages, pks[0],
                    "message paging breaks created-at ties by ordering on this key ascending", errors);

            ValidateChatColumn(table, MetadataKeys.Chat.Role, config.RoleColumn!,
                ChatConfig.StringColumnTypes, "string-typed", errors);
            ValidateChatColumn(table, MetadataKeys.Chat.Content, config.ContentColumn!,
                ChatConfig.StringColumnTypes, "string-typed", errors);
            ValidateChatColumn(table, MetadataKeys.Chat.CreatedAt, config.CreatedAtColumn!,
                ChatConfig.DateTimeColumnTypes, "date/time-typed", errors);
            ValidateChatConversationFk(chat, errors);
        }

        // The chat surface's ordering contract — conversations newest first (key desc),
        // message paging tiebreak (key asc) — is only true for a monotonic integer key;
        // a GUID (or other non-integer) key orders randomly and would break it silently,
        // so the contract is made impossible to misconfigure at model load.
        private static void ValidateChatOrderingKey(
            IDbTable table, string optInKey, ColumnDto keyColumn, string orderingContract, List<string> errors)
        {
            if (!ChatConfig.IntegerKeyColumnTypes.Contains(Utils.StringNormalizer.NormalizeType(keyColumn.DataType)))
                errors.Add(Problem(table, optInKey, table.GetMetadataValue(optInKey),
                    $"primary-key column '{keyColumn.ColumnName}' has type '{keyColumn.DataType}', but {orderingContract}; " +
                    "that ordering is only creation order for a monotonic integer-typed key " +
                    $"({string.Join("/", ChatConfig.IntegerKeyColumnTypes.OrderBy(t => t, StringComparer.Ordinal))}). " +
                    "Use an integer identity key for this chat table."));
        }

        private static void ValidateChatColumn(
            IDbTable table, string key, string columnName,
            IReadOnlySet<string> allowedTypes, string typeRequirement, List<string> errors)
        {
            if (!table.ColumnLookup.TryGetValue(columnName, out var column))
            {
                errors.Add(Problem(table, key, columnName, "column does not exist on the messages table"));
                return;
            }

            if (!allowedTypes.Contains(Utils.StringNormalizer.NormalizeType(column.DataType)))
                errors.Add(Problem(table, key, columnName,
                    $"column '{column.ColumnName}' has type '{column.DataType}'; the {key} column must be {typeRequirement}."));
        }

        // The chat surface joins messages to conversations through this column, so the
        // reference must be a real relationship — a declared FK or an explicit 'join'
        // metadata rule — that reaches the conversations table's PK, not merely a
        // column that happens to hold ids.
        private static void ValidateChatConversationFk(ChatModelConfig chat, List<string> errors)
        {
            var messages = chat.MessagesTable;
            var conversations = chat.ConversationsTable;
            var fk = chat.MessagesConfig.ConversationFkColumn!;

            if (!DbColumnExists(messages, fk))
            {
                errors.Add(Problem(messages, MetadataKeys.Chat.ConversationFk, fk,
                    "column does not exist on the messages table"));
                return;
            }

            var pks = conversations.KeyColumns.ToArray();
            if (pks.Length != 1)
                return; // Missing/composite PK already reported by ValidateChatConversations.

            var linksToConversations = messages.SingleLinks.Values
                .Where(link =>
                    string.Equals(link.ParentTable.TableSchema, conversations.TableSchema, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(link.ParentTable.DbName, conversations.DbName, StringComparison.OrdinalIgnoreCase)
                    && link.ChildIds.Any(c => string.Equals(c.ColumnName, fk, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            var referencesPk = linksToConversations.Any(link =>
                !link.IsComposite
                && string.Equals(link.ChildId.ColumnName, fk, StringComparison.OrdinalIgnoreCase)
                && string.Equals(link.ParentId.ColumnName, pks[0].ColumnName, StringComparison.OrdinalIgnoreCase));
            if (referencesPk)
                return;

            // A composite FK exposes the chat column only as one component of a
            // multi-column key. The chat surface joins messages to conversations on the
            // single chat-conversation-fk column; joining on part of a composite key
            // would silently mix conversations across the remaining key columns'
            // values — reject it by name rather than fall through to the generic error.
            var composite = linksToConversations.FirstOrDefault(link => link.IsComposite);
            if (composite != null)
            {
                errors.Add(Problem(messages, MetadataKeys.Chat.ConversationFk, fk,
                    $"column '{fk}' reaches the conversations table '{conversations.TableSchema}.{conversations.DbName}' " +
                    $"only through the composite foreign key '{composite.Name}' " +
                    $"({string.Join(", ", composite.ChildIds.Select(c => c.ColumnName))}). The chat surface joins on this " +
                    "single column, and joining on part of a composite key would mix conversations across the remaining " +
                    $"key columns; '{MetadataKeys.Chat.ConversationFk}' requires a single-column foreign key to the " +
                    "conversations table's primary key."));
                return;
            }

            errors.Add(Problem(messages, MetadataKeys.Chat.ConversationFk, fk,
                $"column '{fk}' does not reference the conversations table " +
                $"'{conversations.TableSchema}.{conversations.DbName}' primary key '{pks[0].ColumnName}'. " +
                "Declare a foreign key (or an explicit 'join' metadata rule) from the messages table " +
                "to the conversations table's primary key."));
        }

        // Picks the chat metadata key actually present on the table so a parse failure
        // is attributed to the real source rather than always to chat-conversations.
        private static string FirstPresentChatKey(IDbTable table)
        {
            var keys = new[]
            {
                MetadataKeys.Chat.Conversations,
                MetadataKeys.Chat.Messages,
                MetadataKeys.Chat.Title,
                MetadataKeys.Chat.Role,
                MetadataKeys.Chat.Content,
                MetadataKeys.Chat.ConversationFk,
                MetadataKeys.Chat.CreatedAt,
            };
            return keys.FirstOrDefault(table.Metadata.ContainsKey) ?? MetadataKeys.Chat.Conversations;
        }

        /// <summary>
        /// Fail-fast validation for the chat-connector metadata contract
        /// (<c>chat-connector</c> + media/plan/tool-description keys). A connector
        /// exposes its table to the chat LLM as a Claude tool, so every structural
        /// assumption the tool generator will make must hold at model load: a published,
        /// non-system table; a media column that exists and is binary- or string-typed
        /// (the serving mode is derived from that type); a string-typed caption column;
        /// and a primary key behind gated plan writes. Reuses
        /// <see cref="ChatConnectorConfig.FromTable"/> so validation cannot drift from
        /// the runtime parse.
        /// </summary>
        private static void ValidateChatConnectors(IDbModel model, List<string> errors)
        {
            foreach (var table in model.Tables)
            {
                ChatConnectorConfig config;
                try
                {
                    config = ChatConnectorConfig.FromTable(table);
                }
                catch (Exception ex)
                {
                    // Parse errors (unknown type/operation tokens, empty values, mapping
                    // keys without their type token), attributed to the offending key.
                    var key = OffendingChatConnectorKey(table, ex.Message);
                    errors.Add(Problem(table, key, table.GetMetadataValue(key), ex.Message));
                    continue;
                }

                if (!config.IsConnector)
                    continue;

                // The chat pair tables (chat-conversations / chat-messages) MAY also be
                // connectors — deliberately. "Explore my own conversation history" is a
                // legitimate tool, the pair tables are ordinary published tables, and the
                // row-scope transformers guard connector reads exactly as they guard the
                // chat store's. Rejecting the combination here would be an arbitrary
                // restriction; pinned by test.

                // A connector tool is built on the generated schema, so its table must
                // be published; history targets are unpublished system tables whose
                // reads the intent executors reject outright.
                if (table.CompareMetadata(MetadataKeys.Ui.Visibility, MetadataKeys.Ui.Hidden))
                    errors.Add(Problem(table, MetadataKeys.ChatConnector.Marker,
                        table.GetMetadataValue(MetadataKeys.ChatConnector.Marker),
                        $"a chat-connector table must be published; '{MetadataKeys.Ui.Visibility}: " +
                        $"{MetadataKeys.Ui.Hidden}' removes it from the schema the connector tools are built on. " +
                        "Unhide the table or remove the connector opt-in."));

                if (Schema.HistorySurface.IsHistoryTarget(model, table))
                    errors.Add(Problem(table, MetadataKeys.ChatConnector.Marker,
                        table.GetMetadataValue(MetadataKeys.ChatConnector.Marker),
                        $"a chat-connector table cannot be a history target (a table named by some " +
                        $"'{MetadataKeys.History.Table}'): history targets are unpublished system tables whose " +
                        $"reads are rejected. Point that '{MetadataKeys.History.Table}' elsewhere. A connector " +
                        $"table MAY itself set '{MetadataKeys.History.Enabled}' — recording its changes composes."));

                if (config.Media)
                    ValidateChatConnectorMedia(table, config, errors);

                // Plan writes target rows by primary key; a keyless table cannot name
                // the row a plan step would update or delete. Explore needs no extra
                // structure — any published table qualifies.
                if (config.Plan && !table.KeyColumns.Any())
                    errors.Add(Problem(table, MetadataKeys.ChatConnector.Marker,
                        table.GetMetadataValue(MetadataKeys.ChatConnector.Marker),
                        $"a '{MetadataKeys.ChatConnector.TypePlan}' connector requires a primary-key column; " +
                        "gated writes could not name the row they target."));
            }
        }

        private static void ValidateChatConnectorMedia(
            IDbTable table, ChatConnectorConfig config, List<string> errors)
        {
            // Media references address rows by a SINGLE id — the binary fetch route
            // (bifrost-media://<table>/<id>) and the vision view_image_id input both
            // carry one key value. Keyless and composite-key tables alike have no
            // such id, so the tool the connector would generate could never work;
            // fail at model load, not on the first chat request's tool-set build.
            if (table.KeyColumns.Count() != 1)
                errors.Add(Problem(table, MetadataKeys.ChatConnector.Marker,
                    table.GetMetadataValue(MetadataKeys.ChatConnector.Marker),
                    $"a '{MetadataKeys.ChatConnector.TypeMedia}' connector requires exactly one primary-key " +
                    "column; media references address rows by a single id."));

            var mediaColumn = config.MediaColumn!;

            // The media pipeline serves the RAW column value: the fetch endpoint and
            // vision loads stream the bytes straight off the row, and URL mode hands
            // the string out verbatim — none of them decrypt, so an encrypted media
            // column would serve ciphertext (and break every vision call at runtime).
            if (table.ColumnLookup.TryGetValue(mediaColumn, out var resolvedMediaColumn)
                && !string.IsNullOrWhiteSpace(resolvedMediaColumn.GetMetadataValue(MetadataKeys.Crypto.Encrypt)))
                errors.Add(Problem(table, MetadataKeys.ChatConnector.MediaColumn, mediaColumn,
                    $"column '{resolvedMediaColumn.ColumnName}' is encrypted ('{MetadataKeys.Crypto.Encrypt}'); " +
                    "media serving streams the raw column value and cannot decrypt it. Use an unencrypted column."));

            if (!table.ColumnLookup.ContainsKey(mediaColumn))
            {
                errors.Add(Problem(table, MetadataKeys.ChatConnector.MediaColumn, mediaColumn,
                    "column does not exist on the connector table"));
            }
            else if (config.MediaMode is null)
            {
                // The column resolved but has neither servable type, so no mode can be
                // derived: it can hold neither bytes (binary mode) nor a URL (URL mode).
                var column = table.ColumnLookup[mediaColumn];
                errors.Add(Problem(table, MetadataKeys.ChatConnector.MediaColumn, mediaColumn,
                    $"column '{column.ColumnName}' has type '{column.DataType}'; the media column must be " +
                    $"binary-typed (served as bytes: {TypeList(ChatConnectorConfig.BinaryColumnTypes)}) or " +
                    $"string-typed (served as a URL: {TypeList(ChatConfig.StringColumnTypes)})."));
            }
            else if (config.VisionEnabled && config.MediaMode == ChatMediaMode.Url)
            {
                // Vision input sends server-held bytes to the model; a URL-mode media
                // column holds no bytes, so the flag would either be silently dead or
                // require the server to fetch arbitrary URLs — both rejected.
                errors.Add(Problem(table, MetadataKeys.ChatConnector.MediaVision,
                    table.GetMetadataValue(MetadataKeys.ChatConnector.MediaVision),
                    $"'{MetadataKeys.ChatConnector.MediaVision}' requires a binary-typed media column: vision " +
                    $"input attaches server-held bytes, and string-typed column '{mediaColumn}' serves URLs. " +
                    "Remove the key or store the media bytes in a binary column."));
            }

            var caption = config.MediaCaptionColumn;
            if (caption == null)
                return;

            if (!table.ColumnLookup.TryGetValue(caption, out var captionColumn))
                errors.Add(Problem(table, MetadataKeys.ChatConnector.MediaCaption, caption,
                    "column does not exist on the connector table"));
            else if (!ChatConfig.StringColumnTypes.Contains(Utils.StringNormalizer.NormalizeType(captionColumn.DataType)))
                errors.Add(Problem(table, MetadataKeys.ChatConnector.MediaCaption, caption,
                    $"column '{captionColumn.ColumnName}' has type '{captionColumn.DataType}'; the " +
                    $"{MetadataKeys.ChatConnector.MediaCaption} column must be string-typed."));
        }

        private static string TypeList(IReadOnlySet<string> types) =>
            string.Join("/", types.OrderBy(t => t, StringComparer.Ordinal));

        // Attributes a chat-connector parse failure to the actual offending key: the
        // parse errors always name their key, so prefer the present key the message
        // names (specific mapping keys before the marker, which the stray-key messages
        // also mention), falling back to the first key present on the table.
        private static string OffendingChatConnectorKey(IDbTable table, string errorMessage)
        {
            var keys = new[]
            {
                MetadataKeys.ChatConnector.MediaColumn,
                MetadataKeys.ChatConnector.MediaVision,
                MetadataKeys.ChatConnector.MediaCaption,
                MetadataKeys.ChatConnector.PlanOperations,
                MetadataKeys.ChatConnector.ToolDescription,
                MetadataKeys.ChatConnector.Marker,
            };
            return keys.FirstOrDefault(k => table.Metadata.ContainsKey(k) && errorMessage.Contains($"'{k}'"))
                ?? keys.FirstOrDefault(table.Metadata.ContainsKey)
                ?? MetadataKeys.ChatConnector.Marker;
        }

        /// <summary>
        /// Fail-fast validation for field-level encryption (<c>encrypt</c> / <c>key-ref</c>
        /// / <c>mask</c> / <c>unmask-role</c> / <c>blind-index</c>). These are security
        /// keys: a typo'd algorithm, an unparseable key-ref, or a blind-index naming a
        /// missing column would either fail to encrypt (leaking plaintext at rest) or
        /// throw deep in the mutation pipeline on the first write. Reject at model load.
        /// </summary>
        private static void ValidateFeed(IDbTable table, List<string> errors)
        {
            var config = FeedConfig.FromTable(table);
            var feedKeys = new[]
            {
                MetadataKeys.Feed.Timestamp,
                MetadataKeys.Feed.Title,
                MetadataKeys.Feed.Body,
                MetadataKeys.Feed.Link,
            };
            var configuredKeys = feedKeys.Where(key => table.Metadata.ContainsKey(key)).ToArray();

            if (configuredKeys.Length == 0)
                return;

            if (!config.IsEnabled)
            {
                errors.Add(Problem(table, MetadataKeys.Feed.Timestamp,
                    table.GetMetadataValue(MetadataKeys.Feed.Timestamp),
                    "feed-* metadata requires a non-empty feed-timestamp column to opt the table into publication"));
                return;
            }

            if (!table.ColumnLookup.TryGetValue(config.TimestampColumn!, out var timestampColumn))
            {
                errors.Add(Problem(table, MetadataKeys.Feed.Timestamp, config.TimestampColumn,
                    "column does not exist"));
            }
            else if (!IsDateTimeType(timestampColumn.EffectiveDataType))
            {
                errors.Add(Problem(table, MetadataKeys.Feed.Timestamp, config.TimestampColumn,
                    $"column '{timestampColumn.ColumnName}' has type '{timestampColumn.EffectiveDataType}'; feed timestamps must be date/time-typed"));
            }

            if (string.IsNullOrWhiteSpace(config.TitleTemplate))
            {
                errors.Add(Problem(table, MetadataKeys.Feed.Title, config.TitleTemplate,
                    "a feed requires a non-empty title column or {column} template"));
            }
            else
            {
                ValidateFeedTemplate(table, MetadataKeys.Feed.Title, config.TitleTemplate, true, errors);
            }

            if (string.IsNullOrWhiteSpace(config.BodyColumn))
            {
                errors.Add(Problem(table, MetadataKeys.Feed.Body, config.BodyColumn,
                    "a feed requires a non-empty body column"));
            }
            else
            {
                ValidateUnencryptedFeedColumn(table, MetadataKeys.Feed.Body, config.BodyColumn, errors);
            }

            if (!string.IsNullOrWhiteSpace(config.LinkTemplate))
                ValidateFeedTemplate(table, MetadataKeys.Feed.Link, config.LinkTemplate, false, errors);

            var primaryKeys = table.KeyColumns.ToArray();
            if (primaryKeys.Length == 0)
            {
                errors.Add(Problem(table, MetadataKeys.Feed.Timestamp, config.TimestampColumn,
                    "a feed table requires at least one primary-key column so item GUIDs and stable ordering can identify rows"));
            }
        }

        private static void ValidateFeedTemplate(
            IDbTable table, string metadataKey, string template, bool titleTemplate, List<string> errors)
        {
            if (FeedConfig.HasMalformedPlaceholders(template))
            {
                errors.Add(Problem(table, metadataKey, template,
                    "template contains malformed placeholders; only schema-derived {column} placeholders are allowed"));
                return;
            }

            var placeholders = FeedConfig.GetPlaceholders(template);
            if (placeholders.Count == 0 && titleTemplate)
            {
                // A bare title value is a column shorthand. Other literal text remains a
                // valid template, but a title must still come from a row.
                ValidateUnencryptedFeedColumn(table, metadataKey, template, errors);
                return;
            }

            foreach (var placeholder in placeholders)
            {
                if (!TryGetSchemaColumn(table, placeholder, out var column))
                {
                    errors.Add(Problem(table, metadataKey, placeholder,
                        "template placeholder does not name a schema column"));
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(column.GetMetadataValue(MetadataKeys.Crypto.Encrypt)))
                {
                    errors.Add(Problem(table, metadataKey, placeholder,
                        $"column '{column.ColumnName}' is field-encrypted ('{MetadataKeys.Crypto.Encrypt}'); feed templates must use unencrypted columns"));
                }
            }
        }

        private static void ValidateUnencryptedFeedColumn(IDbTable table, string metadataKey, string columnName, List<string> errors)
        {
            if (!table.ColumnLookup.TryGetValue(columnName, out var column))
            {
                errors.Add(Problem(table, metadataKey, columnName, "column does not exist"));
                return;
            }

            if (!string.IsNullOrWhiteSpace(column.GetMetadataValue(MetadataKeys.Crypto.Encrypt)))
            {
                errors.Add(Problem(table, metadataKey, columnName,
                    $"column '{column.ColumnName}' is field-encrypted ('{MetadataKeys.Crypto.Encrypt}'); feed content must use an unencrypted column"));
            }
        }

        private static bool TryGetSchemaColumn(IDbTable table, string name, out ColumnDto column)
            => table.ColumnLookup.TryGetValue(name, out column!) || table.GraphQlLookup.TryGetValue(name, out column!);

        private static bool IsDateTimeType(string dataType)
            => StringNormalizer.NormalizeType(dataType) is
                "date" or "datetime" or "datetime2" or "smalldatetime" or
                "datetimeoffset" or "time" or "timestamp";

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

        /// <summary>
        /// Fail-fast validation for the Prometheus business-metric contract
        /// (<c>metric-*</c>). A metric is exported over an operational wire, so every
        /// structural assumption the later collection query and exposition endpoint will
        /// make must hold at model load: a valid metric name, count/sum sources that name
        /// existing (and, for sum, numeric) columns, label columns that exist and are NOT
        /// field-encrypted (labels are cleartext exposition — an encrypted label would
        /// leak the very value encryption protects), a tenant-scoped table that has
        /// explicitly chosen a scrape-security mode rather than exporting an ambient
        /// cross-tenant aggregate, and no two metrics colliding on the same exported
        /// series (name + label set). Reuses <see cref="PrometheusMetricConfig.FromTable"/>
        /// so validation cannot drift from the runtime parse.
        /// </summary>
        private static void ValidatePrometheusMetrics(IDbModel model, List<string> errors)
        {
            var configs = new List<(IDbTable Table, PrometheusMetricConfig Config)>();

            foreach (var table in model.Tables)
            {
                PrometheusMetricConfig config;
                try
                {
                    config = PrometheusMetricConfig.FromTable(table);
                }
                catch (Exception ex)
                {
                    // Structural parse error (empty/invalid name, empty source, bad token,
                    // label normalization collision), attributed to the metric-name key.
                    errors.Add(Problem(table, MetadataKeys.Metrics.Name,
                        table.GetMetadataValue(MetadataKeys.Metrics.Name), ex.Message));
                    continue;
                }

                if (!config.DeclaresMetric)
                {
                    // metric-help/count/sum/labels/... without metric-name declare nothing:
                    // the author believes a metric is exported and none is.
                    foreach (var key in new[]
                    {
                        MetadataKeys.Metrics.Help, MetadataKeys.Metrics.Count, MetadataKeys.Metrics.Sum,
                        MetadataKeys.Metrics.Labels, MetadataKeys.Metrics.MaxCardinality,
                        MetadataKeys.Metrics.SecurityMode,
                    })
                    {
                        var value = table.GetMetadataValue(key);
                        if (!string.IsNullOrWhiteSpace(value))
                            errors.Add(Problem(table, key, value,
                                $"set without '{MetadataKeys.Metrics.Name}'; the table declares no metric, " +
                                "so this key has no effect."));
                    }
                    continue;
                }

                configs.Add((table, config));

                // metric-count naming a column (not COUNT(*)) must exist.
                if (config.CountColumn != null && !DbColumnExists(table, config.CountColumn))
                    errors.Add(Problem(table, MetadataKeys.Metrics.Count, config.CountColumn,
                        "metric-count names a column that does not exist; there is nothing to count"));

                // metric-sum must name an existing, numeric column.
                if (config.SumColumn != null)
                {
                    if (!table.ColumnLookup.TryGetValue(config.SumColumn, out var sumColumn))
                        errors.Add(Problem(table, MetadataKeys.Metrics.Sum, config.SumColumn,
                            "metric-sum names a column that does not exist; there is nothing to sum"));
                    else if (!MetadataKeys.Metrics.NumericColumnTypes.Contains(
                                 Utils.StringNormalizer.NormalizeType(sumColumn.DataType)))
                        errors.Add(Problem(table, MetadataKeys.Metrics.Sum, config.SumColumn,
                            $"column '{sumColumn.ColumnName}' has type '{sumColumn.DataType}'; a " +
                            $"'{MetadataKeys.Metrics.Sum}' source must be numeric."));
                }

                // Each label column must exist and must NOT be field-encrypted — a metric
                // label is cleartext exposition, so labeling by an encrypted column would
                // publish the plaintext the encryption exists to protect.
                foreach (var label in config.Labels)
                {
                    if (!table.ColumnLookup.TryGetValue(label, out var labelColumn))
                    {
                        errors.Add(Problem(table, MetadataKeys.Metrics.Labels, label,
                            "metric-labels names a column that does not exist"));
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(labelColumn.GetMetadataValue(MetadataKeys.Crypto.Encrypt)))
                        errors.Add(Problem(table, MetadataKeys.Metrics.Labels, label,
                            $"column '{labelColumn.ColumnName}' is field-encrypted " +
                            $"('{MetadataKeys.Crypto.Encrypt}'); a metric label is cleartext exposition and " +
                            "must not expose an encrypted column. Remove the label or use an unencrypted column."));
                }

                // A row-scoped table must EXPLICITLY choose a scrape-security mode: a metric
                // that aggregates across partitions without a chosen mode would export an
                // ambient cross-tenant total (slice 3 enforces the mode at scrape time).
                //
                // Row scoping has two spellings and BOTH narrow rows from the caller's
                // identity, which a scrape does not have. Checking only the tenant spelling
                // let a policy-row-scoped metric load with no declared mode.
                var tenantColumn = table.GetMetadataValue(MetadataKeys.Security.TenantFilter);
                var scoping = !string.IsNullOrWhiteSpace(tenantColumn)
                    ? $"tenant-filtered ('{MetadataKeys.Security.TenantFilter}')"
                    : HasPolicyRowScoping(table)
                        ? $"policy row-scoped ('{MetadataKeys.Policy.RowScope}')"
                        : null;
                if (scoping is not null && config.SecurityMode is null)
                    errors.Add(Problem(table, MetadataKeys.Metrics.Name, config.MetricName,
                        $"table is {scoping} but declares a metric " +
                        $"without an explicit '{MetadataKeys.Metrics.SecurityMode}' " +
                        $"({string.Join(", ", MetadataKeys.Metrics.SecurityModes)}); a row-scoped metric with " +
                        "no chosen mode would export an ambient cross-tenant aggregate."));
            }

            // Two metrics that produce the same exported series (name + label set) would
            // clobber each other on the exposition wire — a duplicate-series collision.
            foreach (var group in configs.GroupBy(c => c.Config.SeriesKey, StringComparer.Ordinal))
            {
                var members = group.ToArray();
                if (members.Length < 2)
                    continue;

                var names = string.Join(", ", members.Select(m => $"{m.Table.TableSchema}.{m.Table.DbName}"));
                errors.Add(Problem(members[0].Table, MetadataKeys.Metrics.Name, members[0].Config.MetricName,
                    $"produces the same metric series (name + label set) as {names}; duplicate series would " +
                    "collide on the exposition wire. Rename one metric or give it a distinct label set."));
            }
        }

        // A single DN component, e.g. "dc=example" or "ou=people": attribute=value with no
        // placeholder. The base DN and the static DN-template path are both chains of these.
        private static readonly System.Text.RegularExpressions.Regex DnComponentGrammar =
            new(@"^[A-Za-z][A-Za-z0-9-]*=[^,=]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Fail-fast validation for the LDAP directory mapping contract (<c>ldap-*</c>). A
        /// misconfiguration must be caught at model load, before a listener is ever opened: an
        /// invalid or non-unique DN template, a naming/attribute/credential column that does not
        /// exist, an attribute mapped onto a column whose type is incompatible with the
        /// attribute's LDAP syntax, a group-membership relationship that names no relationship,
        /// is composite (explicitly unsupported in this slice), or points at a non-mapped table,
        /// and — the security crux, enforced in the parser — a credential (password-hash) column
        /// exposed as a returned attribute. Reuses <see cref="LdapMappingConfig.FromTable"/> so
        /// validation cannot drift from the runtime parse.
        /// </summary>
        private static void ValidateLdap(IDbModel model, List<string> errors)
        {
            var mapped = new List<(IDbTable Table, LdapMappingConfig Config)>();

            foreach (var table in model.Tables)
            {
                LdapMappingConfig config;
                try
                {
                    config = LdapMappingConfig.FromTable(table);
                }
                catch (Exception ex)
                {
                    var key = FirstPresentLdapKey(table);
                    errors.Add(Problem(table, key, table.GetMetadataValue(key), ex.Message));
                    continue;
                }

                if (config.IsMapped)
                {
                    mapped.Add((table, config));
                    continue;
                }

                // ldap-dn-template / ldap-attributes / ldap-credential / ldap-member without
                // ldap-object-class map nothing: the author believes the table is a directory
                // entry and it is not.
                foreach (var key in new[]
                {
                    MetadataKeys.Ldap.DnTemplate, MetadataKeys.Ldap.Attributes,
                    MetadataKeys.Ldap.Credential, MetadataKeys.Ldap.Member,
                })
                {
                    var value = table.GetMetadataValue(key);
                    if (!string.IsNullOrWhiteSpace(value))
                        errors.Add(Problem(table, key, value,
                            $"set without '{MetadataKeys.Ldap.ObjectClass}'; the table publishes no directory " +
                            "entry, so this key has no effect."));
                }
            }

            if (mapped.Count == 0)
                return; // LDAP not in use — a model-level base DN is simply unused.

            // A mapped directory must be rooted somewhere.
            var baseDn = model.GetMetadataValue(MetadataKeys.Ldap.BaseDn);
            if (string.IsNullOrWhiteSpace(baseDn))
                errors.Add(
                    $"  :root [{MetadataKeys.Ldap.BaseDn}]: {mapped.Count} table(s) map to LDAP but no base DN " +
                    "is configured; the directory has nowhere to root its entries.");
            else if (!IsValidDn(baseDn))
                errors.Add(
                    $"  :root [{MetadataKeys.Ldap.BaseDn}]: '{baseDn}' is not a valid DN " +
                    "(expected a comma-separated chain of 'attribute=value', e.g. 'dc=example,dc=com').");

            // Two tables whose entries would share a DN namespace (same normalized template)
            // would produce overlapping/ambiguous DNs; reject the collision at model load.
            foreach (var group in mapped.GroupBy(m => m.Config.NormalizedDnTemplate, StringComparer.Ordinal))
            {
                var members = group.ToArray();
                if (members.Length < 2)
                    continue;

                var names = string.Join(", ", members.Select(m => $"{m.Table.TableSchema}.{m.Table.DbName}"));
                errors.Add(Problem(members[0].Table, MetadataKeys.Ldap.DnTemplate, members[0].Config.DnTemplate,
                    $"produces the same DN namespace as {names}; two tables cannot publish entries under the same " +
                    "DN template. Give each a distinct DN template."));
            }

            foreach (var (table, config) in mapped)
            {
                if (!DbColumnExists(table, config.NamingColumn!))
                    errors.Add(Problem(table, MetadataKeys.Ldap.DnTemplate, config.NamingColumn,
                        "the DN template's naming column does not exist on the table"));

                foreach (var attribute in config.Attributes)
                    ValidateLdapAttribute(table, attribute, errors);

                if (config.CredentialColumn != null && !DbColumnExists(table, config.CredentialColumn))
                    errors.Add(Problem(table, MetadataKeys.Ldap.Credential, config.CredentialColumn,
                        "the credential column does not exist on the table"));

                if (config.MemberRelationship != null)
                    ValidateLdapMembership(table, config.MemberRelationship, errors);
            }
        }

        // Each returned attribute must name an existing column whose type is compatible with the
        // attribute's LDAP syntax. A well-known attribute (uid/cn/mail/uidNumber/...) carries a
        // required syntax class; mapping it onto a column of a different class (e.g. a text
        // attribute onto an integer column) would emit values the syntax cannot represent, so it
        // is rejected. A custom (unregistered) attribute has no syntax constraint.
        private static void ValidateLdapAttribute(IDbTable table, LdapAttributeMapping attribute, List<string> errors)
        {
            if (!table.ColumnLookup.TryGetValue(attribute.Column, out var column))
            {
                errors.Add(Problem(table, MetadataKeys.Ldap.Attributes, attribute.Column,
                    $"attribute '{attribute.Attribute}' maps to a column that does not exist on the table"));
                return;
            }

            if (!LdapMappingConfig.WellKnownAttributeSyntax.TryGetValue(attribute.Attribute, out var required))
                return; // Custom attribute — no syntax requirement.

            var actual = LdapMappingConfig.ColumnSyntax(column.DataType);
            if (actual != required)
                errors.Add(Problem(table, MetadataKeys.Ldap.Attributes, attribute.Column,
                    $"attribute '{attribute.Attribute}' requires LDAP syntax {required}, but column " +
                    $"'{column.ColumnName}' has type '{column.DataType}' (syntax {actual}); the column type is " +
                    "incompatible with the attribute's syntax."));
        }

        // The group-membership relationship must name a real to-many relationship (a multi-link
        // or a many-to-many) whose target table is itself LDAP-mapped, so a member DN can be
        // constructed. A composite-key relationship is explicitly unsupported in this slice —
        // rejected by name rather than silently taking the first key column.
        private static void ValidateLdapMembership(IDbTable table, string relationship, List<string> errors)
        {
            if (table.MultiLinks.TryGetValue(relationship, out var multiLink))
            {
                if (multiLink.IsComposite)
                {
                    errors.Add(Problem(table, MetadataKeys.Ldap.Member, relationship,
                        "names a composite-key relationship; composite group-membership relationships are not " +
                        "supported in this slice. Use a single-column relationship."));
                    return;
                }
                RequireMappedMemberTarget(table, relationship, multiLink.ChildTable, errors);
                return;
            }

            if (table.ManyToManyLinks.TryGetValue(relationship, out var m2mLink))
            {
                // Same rule on the bridge: ManyToManyLink records one column per leg, so a
                // composite leg is a FIRST-COLUMN join. Membership joined on a partial key
                // matches rows agreeing on that column alone — across the tenant discriminator
                // the remaining columns carry — and the group would publish member DNs naming
                // FOREIGN entries. Rejected by name, never silently narrowed.
                if (m2mLink.IsComposite)
                {
                    errors.Add(Problem(table, MetadataKeys.Ldap.Member, relationship,
                        "names a composite-key many-to-many relationship; composite group-membership " +
                        "relationships are not supported in this slice. Use a single-column relationship."));
                    return;
                }
                RequireMappedMemberTarget(table, relationship, m2mLink.TargetTable, errors);
                return;
            }

            if (table.SingleLinks.ContainsKey(relationship))
            {
                errors.Add(Problem(table, MetadataKeys.Ldap.Member, relationship,
                    "names a to-one relationship; group membership requires a to-many relationship " +
                    "(a multi-link or a many-to-many)."));
                return;
            }

            errors.Add(Problem(table, MetadataKeys.Ldap.Member, relationship,
                "does not name a known relationship on the table"));
        }

        private static void RequireMappedMemberTarget(
            IDbTable table, string relationship, IDbTable target, List<string> errors)
        {
            if (!LdapMappingConfig.FromTable(target).IsMapped)
                errors.Add(Problem(table, MetadataKeys.Ldap.Member, relationship,
                    $"the membership target table '{target.TableSchema}.{target.DbName}' is not LDAP-mapped " +
                    $"(no '{MetadataKeys.Ldap.ObjectClass}'); a member DN cannot be constructed for an unmapped table."));
        }

        private static bool IsValidDn(string dn) =>
            dn.Split(',', StringSplitOptions.TrimEntries)
              .All(component => component.Length > 0 && DnComponentGrammar.IsMatch(component));

        private static string FirstPresentLdapKey(IDbTable table)
        {
            var keys = new[]
            {
                MetadataKeys.Ldap.ObjectClass,
                MetadataKeys.Ldap.DnTemplate,
                MetadataKeys.Ldap.Attributes,
                MetadataKeys.Ldap.Credential,
                MetadataKeys.Ldap.Member,
            };
            return keys.FirstOrDefault(table.Metadata.ContainsKey) ?? MetadataKeys.Ldap.ObjectClass;
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

        /// <summary>
        /// Whether the table's ROWS are narrowed by a policy derived from the caller's
        /// identity. Deliberately narrower than <c>TablePolicy.HasPolicy</c>, which is also
        /// true for a purely column-level restriction such as <c>policy-read-deny</c>: a
        /// column policy hides a field but never partitions rows, so an aggregate over it
        /// carries no cross-tenant exposure and must not be forced to declare a mode.
        /// Rejecting those here would refuse the whole model at load — taking down every
        /// other metric with it — where the scrape-time resolver only drops one series.
        /// An unparseable policy counts as scoped: it cannot be shown to be safe.
        /// </summary>
        private static bool HasPolicyRowScoping(IDbTable table)
        {
            try
            {
                return PolicyConfigCollector.FromTable(table).RowScopeExpression is not null;
            }
            catch
            {
                return true;
            }
        }

        private static string Problem(IDbTable table, string metadataKey, string? value, string reason)
            => $"  {table.TableSchema}.{table.DbName} [{metadataKey}]: '{value}' - {reason}";
    }
}
