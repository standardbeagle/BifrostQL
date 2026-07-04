using System.Data.Common;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Workflows;
using GraphQL;
using GraphQL.Resolvers;
using Microsoft.Extensions.DependencyInjection;
using static BifrostQL.Core.Resolvers.DbParameterBinder;

namespace BifrostQL.Core.Resolvers
{
    public sealed class DbTableBatchResolver : IBifrostResolver, IFieldResolver
    {
        private const int DefaultMaxBatchSize = 100;
        private readonly IDbTable _table;

        public DbTableBatchResolver(IDbTable table)
        {
            _table = table;
        }

        public async ValueTask<object?> ResolveAsync(IBifrostFieldContext context)
        {
            var bifrost = new BifrostContextAdapter(context);
            var conFactory = bifrost.ConnFactory;
            var model = bifrost.Model;
            var dialect = conFactory.Dialect;
            var mutationTransformers = context.RequestServices!.GetRequiredService<IMutationTransformers>();

            var actions = context.GetArgument<List<Dictionary<string, object?>>>("actions");
            if (actions == null || actions.Count == 0)
                return 0;

            var maxBatchSize = GetMaxBatchSize(_table);
            if (actions.Count > maxBatchSize)
                throw new BifrostExecutionError($"Batch size {actions.Count} exceeds maximum allowed size of {maxBatchSize}.");

            var userContext = context.UserContext;
            var transformContext = new MutationTransformContext { Model = model, UserContext = userContext, Services = context.RequestServices };

            // Module mutation arguments (e.g. _hardDelete) are declared on the
            // batch field and apply to every delete action in the batch. Captured
            // once here and threaded into each delete's transform context, mirroring
            // the single-row resolver, so a batch delete with _hardDelete:true
            // performs a real DELETE on a soft-delete table.
            var moduleArguments = ModuleApiRegistry.CaptureMutationArguments(context, _table);

            await using var conn = conFactory.GetConnection();
            var outcomes = new List<BatchActionOutcome>();
            DbTransaction? transaction = null;
            try
            {
                await conn.OpenAsync();
                transaction = await conn.BeginTransactionAsync();
                foreach (var action in actions)
                {
                    var outcome = await ExecuteAction(action, _table, mutationTransformers, model, dialect, conn, transaction, userContext, transformContext, moduleArguments);
                    if (outcome is not null)
                        outcomes.Add(outcome);
                }
                await transaction.CommitAsync();
            }
            catch (BifrostExecutionError)
            {
                if (transaction != null)
                    await transaction.RollbackAsync();
                throw;
            }
            catch (Exception ex)
            {
                if (transaction != null)
                    await transaction.RollbackAsync();
                throw BifrostExecutionError.FromDatabaseException(ex);
            }
            finally
            {
                if (transaction != null)
                    await transaction.DisposeAsync();
            }

            // Observers fire only after commit so audit/state-transition
            // notifications never describe rolled-back work. Failures inside
            // observers are swallowed by MutationObservers/StateTransitionObservers.
            await NotifyObserversAsync(context.RequestServices, _table, outcomes, userContext);

            var totalAffected = 0;
            foreach (var outcome in outcomes) totalAffected += outcome.Affected;
            return totalAffected;
        }

        private sealed record BatchActionOutcome(
            int Affected,
            MutationType MutationType,
            IDictionary<string, object?> Data,
            StateTransitionInfo? Transition);

        private static async ValueTask NotifyObserversAsync(
            IServiceProvider? services,
            IDbTable table,
            IReadOnlyList<BatchActionOutcome> outcomes,
            IDictionary<string, object?> userContext)
        {
            if (services is null || outcomes.Count == 0) return;

            var mutationObservers = services.GetService<MutationObservers>();
            var transitionObservers = services.GetService<StateTransitionObservers>();
            var triggersSuppressed = IsWorkflowTriggerSuppressed(userContext);

            foreach (var outcome in outcomes)
            {
                if (mutationObservers is not null && !triggersSuppressed)
                {
                    await mutationObservers.NotifyAsync(new MutationObserverContext
                    {
                        Table = table,
                        MutationType = outcome.MutationType,
                        Data = outcome.Data,
                        Result = outcome.Affected,
                        UserContext = userContext,
                    });
                }
                if (outcome.Transition is not null && transitionObservers is not null)
                {
                    await transitionObservers.NotifyAsync(outcome.Transition, userContext);
                }
            }
        }

        private static bool IsWorkflowTriggerSuppressed(IDictionary<string, object?> userContext)
            => userContext.TryGetValue(WorkflowTriggerHost.SuppressTriggersKey, out var value)
               && value is bool suppressed
               && suppressed;

        ValueTask<object?> IFieldResolver.ResolveAsync(IResolveFieldContext context)
        {
            return ResolveAsync(new BifrostFieldContextAdapter(context));
        }

        private static int GetMaxBatchSize(IDbTable table)
        {
            var metaValue = table.GetMetadataValue(MetadataKeys.Batch.MaxSize);
            if (metaValue != null && int.TryParse(metaValue, out var size) && size > 0)
                return size;
            return DefaultMaxBatchSize;
        }

        private static async Task<BatchActionOutcome?> ExecuteAction(
            Dictionary<string, object?> action,
            IDbTable table,
            IMutationTransformers mutationTransformers,
            IDbModel model,
            ISqlDialect dialect,
            DbConnection conn,
            DbTransaction transaction,
            IDictionary<string, object?> userContext,
            MutationTransformContext transformContext,
            IReadOnlyDictionary<string, object?> moduleArguments)
        {
            if (action.TryGetValue("insert", out var insertObj) && insertObj is Dictionary<string, object?> insertData)
            {
                return await ExecuteInsert(insertData, table, mutationTransformers, model, dialect, conn, transaction, userContext, transformContext);
            }
            if (action.TryGetValue("update", out var updateObj) && updateObj is Dictionary<string, object?> updateData)
            {
                return await ExecuteUpdate(updateData, table, mutationTransformers, model, dialect, conn, transaction, userContext, transformContext);
            }
            if (action.TryGetValue("delete", out var deleteObj) && deleteObj is Dictionary<string, object?> deleteData)
            {
                return await ExecuteDelete(deleteData, table, mutationTransformers, model, dialect, conn, transaction, userContext, transformContext, moduleArguments);
            }
            if (action.TryGetValue("upsert", out var upsertObj) && upsertObj is Dictionary<string, object?> upsertData)
            {
                return await ExecuteUpsert(upsertData, table, mutationTransformers, model, dialect, conn, transaction, userContext, transformContext);
            }
            return null;
        }

        private static async Task<BatchActionOutcome?> ExecuteInsert(
            Dictionary<string, object?> data,
            IDbTable table,
            IMutationTransformers mutationTransformers,
            IDbModel model,
            ISqlDialect dialect,
            DbConnection conn,
            DbTransaction transaction,
            IDictionary<string, object?> userContext,
            MutationTransformContext transformContext)
        {
            if (data.Count == 0) return null;

            // Mutation transformers (e.g. the authorization policy engine) gate
            // the insert before any SQL is built; non-empty Errors abort it.
            var transformResult = await mutationTransformers.TransformAsync(table, MutationType.Insert, data, transformContext);
            if (transformResult.Errors.Length > 0)
                throw new BifrostExecutionError(string.Join("; ", transformResult.Errors));

            // Adopt the (possibly rewritten) data so transformer output — e.g.
            // enum-name → DB-value mapping — reaches the SQL, rekeyed from GraphQL
            // field names to real DB column names. When no transformer applies and
            // names already match, this is effectively a no-op.
            data = ToDbColumnKeys(table, transformResult.Data);

            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var columns = string.Join(",", data.Keys.Select(k => dialect.EscapeIdentifier(k)));
            var values = string.Join(",", data.Keys.Select(k => ValuePlaceholder(dialect, table, k)));
            var sql = $"INSERT INTO {tableRef}({columns}) VALUES({values});";
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = transaction;
            AddParameters(cmd, data);
            var affected = await cmd.ExecuteNonQueryAsync();
            return new BatchActionOutcome(affected, MutationType.Insert, data, transformResult.StateTransition);
        }

        private static async Task<BatchActionOutcome?> ExecuteUpdate(
            Dictionary<string, object?> data,
            IDbTable table,
            IMutationTransformers mutationTransformers,
            IDbModel model,
            ISqlDialect dialect,
            DbConnection conn,
            DbTransaction transaction,
            IDictionary<string, object?> userContext,
            MutationTransformContext transformContext)
        {
            if (data.Count == 0) return null;

            var caseData = new Dictionary<string, object?>(data, StringComparer.OrdinalIgnoreCase);
            // keyData is DB-name space (drives WHERE + current-row load); tolerant of
            // GraphQL field names. standardData keeps GraphQL names for transformers
            // and is normalized to DB names before SQL generation.
            var keyData = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in caseData.Where(d => IsPrimaryKeyColumn(table, d.Key)))
                keyData[ToDbColumnName(table, d.Key)] = d.Value;
            var standardData = caseData.Where(d => !IsPrimaryKeyColumn(table, d.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            if (!keyData.Any() || !standardData.Any()) return null;

            var currentRow = await LoadCurrentStateMachineRow(dialect, table, keyData, conn, transaction);
            var updateTransformContext = currentRow is null
                ? transformContext
                : new MutationTransformContext
                {
                    Model = transformContext.Model,
                    UserContext = transformContext.UserContext,
                    CurrentRow = currentRow,
                    Services = transformContext.Services,
                };

            // Mutation transformers (e.g. the authorization policy engine) gate
            // the update before any SQL is built; non-empty Errors abort it.
            var transformResult = await mutationTransformers.TransformAsync(table, MutationType.Update, caseData, updateTransformContext);
            if (transformResult.Errors.Length > 0)
                throw new BifrostExecutionError(string.Join("; ", transformResult.Errors));

            // The transformer's AdditionalFilter (e.g. policy row-scope, soft-delete
            // IS NULL) is ANDed onto the WHERE clause so it narrows — never
            // replaces — the primary-key predicate.
            var additionalFilter = RenderAdditionalFilter(transformResult.AdditionalFilter, dialect);

            // Adopt the (possibly rewritten) data so transformer output — e.g.
            // enum-name → DB-value mapping — reaches the SQL. The non-key SET split
            // is recomputed against the (unchanged) primary-key set; enum columns are
            // non-key. When no transformer applies, Transform returns the same data
            // reference, so standardData is re-derived identically (no-op).
            var updatedData = ToDbColumnKeys(table, transformResult.Data);
            standardData = updatedData
                .Where(d => !keyData.ContainsKey(d.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var setClause = string.Join(",", standardData.Select(kv => SetAssignment(dialect, table, kv.Key)));
            var whereClause = string.Join(" AND ", keyData.Select(kv => $"{dialect.EscapeIdentifier(kv.Key)}=@{kv.Key}"));
            var sql = $"UPDATE {tableRef} SET {setClause} WHERE {whereClause}{additionalFilter.WhereSuffix};";
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = transaction;
            AddParameters(cmd, updatedData);
            AddExtraParameters(cmd, additionalFilter.Parameters);
            var affected = await cmd.ExecuteNonQueryAsync();
            return new BatchActionOutcome(affected, MutationType.Update, updatedData, transformResult.StateTransition);
        }

        private static async Task<IReadOnlyDictionary<string, object?>?> LoadCurrentStateMachineRow(
            ISqlDialect dialect,
            IDbTable table,
            Dictionary<string, object?> keyData,
            DbConnection conn,
            DbTransaction transaction)
        {
            var definition = BifrostQL.Core.Auth.StateMachineConfigCollector.FromTable(table);
            if (definition is null || keyData.Count == 0)
                return null;

            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var stateColumn = dialect.EscapeIdentifier(definition.StateColumn);
            var whereClause = string.Join(" AND ", keyData.Select(kv => $"{dialect.EscapeIdentifier(kv.Key)}=@{kv.Key}"));
            var sql = $"SELECT {stateColumn} FROM {tableRef} WHERE {whereClause};";

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = transaction;
            AddParameters(cmd, keyData);
            var currentState = await cmd.ExecuteScalarAsync();
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [definition.StateColumn] = currentState == DBNull.Value ? null : currentState,
            };
        }

        private static async Task<BatchActionOutcome?> ExecuteDelete(
            Dictionary<string, object?> data,
            IDbTable table,
            IMutationTransformers mutationTransformers,
            IDbModel model,
            ISqlDialect dialect,
            DbConnection conn,
            DbTransaction transaction,
            IDictionary<string, object?> userContext,
            MutationTransformContext transformContext,
            IReadOnlyDictionary<string, object?> moduleArguments)
        {
            if (data.Count == 0) return null;

            // Thread the captured module arguments (e.g. _hardDelete) so the
            // soft-delete transformer can read HardDeleteKey and skip the
            // DELETE→UPDATE rewrite, mirroring the single-row resolver.
            var deleteTransformContext = moduleArguments.Count == 0
                ? transformContext
                : new MutationTransformContext
                {
                    Model = transformContext.Model,
                    UserContext = transformContext.UserContext,
                    Services = transformContext.Services,
                    ModuleArguments = moduleArguments,
                };

            var transformResult = await mutationTransformers.TransformAsync(table, MutationType.Delete, data, deleteTransformContext);
            if (transformResult.Errors.Length > 0)
                throw new BifrostExecutionError(string.Join("; ", transformResult.Errors));

            // The transformer's AdditionalFilter (e.g. policy row-scope, soft-delete
            // IS NULL) is ANDed onto the WHERE clause so it narrows — never
            // replaces — the primary-key predicate.
            var additionalFilter = RenderAdditionalFilter(transformResult.AdditionalFilter, dialect);

            // Rekey to DB column names so the PK split (via ColumnLookup) and the
            // emitted WHERE/SET share one name space even for sanitized columns.
            var dbData = ToDbColumnKeys(table, transformResult.Data);

            if (transformResult.MutationType == MutationType.Update)
            {
                var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
                var keyData = dbData.Where(d => table.ColumnLookup.ContainsKey(d.Key) && table.ColumnLookup[d.Key].IsPrimaryKey)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                var setData = dbData.Where(d => !table.ColumnLookup.ContainsKey(d.Key) || !table.ColumnLookup[d.Key].IsPrimaryKey)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                var setClause = string.Join(",", setData.Select(kv => SetAssignment(dialect, table, kv.Key)));
                var whereClause = string.Join(" AND ", keyData.Select(kv => $"{dialect.EscapeIdentifier(kv.Key)}=@{kv.Key}"));
                var sql = $"UPDATE {tableRef} SET {setClause} WHERE {whereClause}{additionalFilter.WhereSuffix};";
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.Transaction = transaction;
                AddParameters(cmd, dbData);
                AddExtraParameters(cmd, additionalFilter.Parameters);
                var softAffected = await cmd.ExecuteNonQueryAsync();
                return new BatchActionOutcome(softAffected, MutationType.Update, transformResult.Data, transformResult.StateTransition);
            }

            // Adopt the (possibly rewritten) data so transformer output (e.g.
            // enum-name → DB-value mapping on a predicate column) reaches the
            // WHERE clause and parameters, mirroring the soft-delete branch above.
            var deleteData = dbData;
            var deleteTableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var deleteWhereClause = string.Join(" AND ", deleteData.Select(kv => $"{dialect.EscapeIdentifier(kv.Key)}=@{kv.Key}"));
            var deleteSql = $"DELETE FROM {deleteTableRef} WHERE {deleteWhereClause}{additionalFilter.WhereSuffix};";
            await using var deleteCmd = conn.CreateCommand();
            deleteCmd.CommandText = deleteSql;
            deleteCmd.Transaction = transaction;
            AddParameters(deleteCmd, deleteData);
            AddExtraParameters(deleteCmd, additionalFilter.Parameters);
            var deleteAffected = await deleteCmd.ExecuteNonQueryAsync();
            return new BatchActionOutcome(deleteAffected, MutationType.Delete, deleteData, transformResult.StateTransition);
        }

        private static async Task<BatchActionOutcome?> ExecuteUpsert(
            Dictionary<string, object?> data,
            IDbTable table,
            IMutationTransformers mutationTransformers,
            IDbModel model,
            ISqlDialect dialect,
            DbConnection conn,
            DbTransaction transaction,
            IDictionary<string, object?> userContext,
            MutationTransformContext transformContext)
        {
            if (data.Count == 0) return null;

            var caseData = new Dictionary<string, object?>(data, StringComparer.OrdinalIgnoreCase);
            // Build the statement in DB-column-name space so sanitized GraphQL
            // field names resolve to real columns; transform still runs on the
            // GraphQL-keyed caseData below, and the bound data is rekeyed to match.
            var dbColumns = caseData.Keys.Select(k => ToDbColumnName(table, k)).ToList();
            var keyColumns = dbColumns.Where(k => table.ColumnLookup[k].IsPrimaryKey).ToList();
            var updateColumns = dbColumns.Where(k => !table.ColumnLookup[k].IsPrimaryKey).ToList();
            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var upsertSql = dialect.UpsertSql(tableRef, keyColumns, dbColumns, updateColumns);

            if (upsertSql != null)
            {
                // An upsert that resolves to a single statement is gated as an
                // update: it targets an existing or new row keyed by primary key.
                var transformResult = await mutationTransformers.TransformAsync(table, MutationType.Update, caseData, transformContext);
                if (transformResult.Errors.Length > 0)
                    throw new BifrostExecutionError(string.Join("; ", transformResult.Errors));

                // Adopt the (possibly rewritten) data so transformer output — e.g.
                // enum-name → DB-value mapping — reaches the SQL. The key/non-key
                // split (and therefore upsertSql) is unaffected because transformers
                // rewrite values, not primary-key membership. When no transformer
                // applies, Transform returns the same data reference (no-op).
                var upsertData = ToDbColumnKeys(table, transformResult.Data);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = upsertSql;
                cmd.Transaction = transaction;
                AddParameters(cmd, upsertData);
                var affected = await cmd.ExecuteNonQueryAsync();
                return new BatchActionOutcome(affected, MutationType.Update, upsertData, transformResult.StateTransition);
            }

            if (keyColumns.Count > 0)
                return await ExecuteUpdate(data, table, mutationTransformers, model, dialect, conn, transaction, userContext, transformContext);

            return await ExecuteInsert(data, table, mutationTransformers, model, dialect, conn, transaction, userContext, transformContext);
        }

        // Renders MutationTransformResult.AdditionalFilter into an AND-prefixed
        // WHERE suffix and its bound parameters. Returns an empty suffix when no
        // transformer contributed a filter.
        private static (string WhereSuffix, IReadOnlyList<SqlParameterInfo> Parameters) RenderAdditionalFilter(
            TableFilter? filter, ISqlDialect dialect)
        {
            if (filter == null)
                return ("", Array.Empty<SqlParameterInfo>());

            var parameters = new SqlParameterCollection();
            var rendered = filter.RenderForMutation(dialect, parameters);
            return ($" AND ({rendered.Sql})", parameters.Parameters);
        }

    }
}
