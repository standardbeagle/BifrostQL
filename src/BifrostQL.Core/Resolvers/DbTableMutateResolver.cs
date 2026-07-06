using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Model;
using GraphQL;
using GraphQL.Resolvers;
using Microsoft.Extensions.DependencyInjection;
using static BifrostQL.Core.Resolvers.DbParameterBinder;

namespace BifrostQL.Core.Resolvers
{
    public sealed class DbTableMutateResolver : IBifrostResolver, IFieldResolver
    {
        private readonly IDbTable _table;

        public DbTableMutateResolver(IDbTable table)
        {
            _table = table;
        }

        public async ValueTask<object?> ResolveAsync(IBifrostFieldContext context)
        {
            var bifrost = new BifrostContextAdapter(context);
            var conFactory = bifrost.ConnFactory;
            var model = bifrost.Model;
            var dialect = conFactory.Dialect;
            var table = _table;
            var mutationTransformers = context.RequestServices!.GetRequiredService<IMutationTransformers>();

            if (context.HasArgument("sync"))
            {
                return await SyncObject(context, table, mutationTransformers, model, conFactory, dialect);
            }
            if (context.HasArgument("insert"))
            {
                return await InsertObject(context, table, mutationTransformers, model, conFactory, dialect);
            }
            if (context.HasArgument("update"))
            {
                return await UpdateObject(context, table, mutationTransformers, model, conFactory, dialect);
            }
            if (context.HasArgument("delete"))
            {
                return await DeleteObject(context, mutationTransformers, table, model, conFactory, dialect);
            }

            if (context.HasArgument("upsert"))
            {
                var propertyInfo = GetPropertyInfo(context, _table, "upsert");
                if (!propertyInfo.data.Any())
                    return 0;

                var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
                // Build in DB-column-name space: keyData is already DB-named, and
                // standardData (GraphQL field names) is mapped so sanitized columns
                // resolve correctly. The bound upsertData is rekeyed to match below.
                var keyColumns = propertyInfo.keyData.Keys.ToList();
                var updateColumns = propertyInfo.standardData.Keys.Select(k => DbParameterBinder.ToDbColumnName(table, k)).ToList();
                var allColumns = keyColumns.Concat(updateColumns).ToList();
                var upsertSql = dialect.UpsertSql(tableRef, keyColumns, allColumns, updateColumns);

                if (upsertSql != null)
                {
                    // An upsert that resolves to a single statement is gated as an
                    // update: it targets an existing or new row keyed by primary key.
                    // Run the transformer pipeline so rewriting transformers (e.g.
                    // enum-name → DB-value mapping) actually reach the SQL; non-empty
                    // Errors abort it. The key/non-key split (and therefore the
                    // already-built upsertSql) is unaffected because transformers
                    // rewrite values, not the primary-key membership. When no
                    // transformer applies, Transform returns the same data reference,
                    // so adopting transformResult.Data is an exact no-op.
                    var upsertTransformContext = new MutationTransformContext { Model = model, UserContext = context.UserContext, Services = context.RequestServices };
                    var transformResult = await mutationTransformers.TransformAsync(table, MutationType.Update, propertyInfo.data, upsertTransformContext);
                    if (transformResult.Errors.Length > 0)
                        throw new BifrostExecutionError(string.Join("; ", transformResult.Errors));

                    var upsertData = DbParameterBinder.ToDbColumnKeys(table, transformResult.Data);
                    var returning = dialect.ReturningIdentityClauseFor(table.KeyColumns.Select(k => k.ColumnName).ToList());
                    var identitySql = returning != null
                        ? upsertSql.TrimEnd(';') + returning + ";"
                        : upsertSql + $"SELECT {dialect.LastInsertedIdentity} ID;";
                    // Before-commit hooks and the upsert (with its identity SELECT)
                    // commit atomically or roll back together.
                    object? upsertResult = null;
                    await MutationCommandExecutor.RunInTransactionAsync(conFactory, async (conn, transaction) =>
                    {
                        await MutationNotifier.RunBeforeCommitHooksAsync(context.RequestServices, table, MutationType.Update, upsertData, context.UserContext);
                        upsertResult = await MutationCommandExecutor.ExecuteScalar(conn, transaction, identitySql, upsertData, context.CancellationToken);
                    }, context.CancellationToken);
                    return HandleDecimals(upsertResult);
                }

                if (propertyInfo.keyData.Any())
                    return await UpdateObject(context, table, mutationTransformers, model, conFactory, dialect, "upsert");

                return await InsertObject(context, table, mutationTransformers, model, conFactory, dialect, "upsert");
            }
            return null;
        }

        ValueTask<object?> IFieldResolver.ResolveAsync(IResolveFieldContext context)
        {
            return ResolveAsync(new BifrostFieldContextAdapter(context));
        }

        // Reads the mutation argument dictionary and optional positional _primaryKey
        // off the GraphQL context, then delegates the (pure) key/standard split to
        // MutationArgumentBinder — which is directly unit-tested.
        private (Dictionary<string, object?> data, Dictionary<string, object?> keyData, Dictionary<string, object?> standardData) GetPropertyInfo(IBifrostFieldContext context, IDbTable table, string parameterName)
        {
            var baseData = context.GetArgument<Dictionary<string, object?>>(parameterName) ?? new();
            return MutationArgumentBinder.SplitProperties(table, baseData, ReadPrimaryKeyValues(context));
        }

        private static Dictionary<string, object?>? ResolvePrimaryKeyArgument(IBifrostFieldContext context, IDbTable table)
            => MutationArgumentBinder.ResolvePrimaryKey(table, ReadPrimaryKeyValues(context));

        private static IReadOnlyList<object?>? ReadPrimaryKeyValues(IBifrostFieldContext context)
            => context.HasArgument("_primaryKey")
                ? context.GetArgument<List<object?>>("_primaryKey")
                : null;

        private async Task<object?> DeleteObject(IBifrostFieldContext context,
            IMutationTransformers mutationTransformers, IDbTable table, IDbModel model,
            IDbConnFactory conFactory, ISqlDialect dialect)
        {
            var data = context.GetArgument<Dictionary<string, object?>>("delete") ?? new();
            if (!data.Any())
                return 0;

            var pkKeyData = ResolvePrimaryKeyArgument(context, table);
            if (pkKeyData != null)
            {
                foreach (var kv in pkKeyData)
                    data[kv.Key] = kv.Value;
            }

            var userContext = context.UserContext;
            var transformContext = new MutationTransformContext
            {
                Model = model,
                UserContext = userContext,
                Services = context.RequestServices,
                ModuleArguments = ModuleApiRegistry.CaptureMutationArguments(context, table),
            };
            var transformResult = await mutationTransformers.TransformAsync(table, MutationType.Delete, data, transformContext);

            if (transformResult.Errors.Length > 0)
                throw new BifrostExecutionError(string.Join("; ", transformResult.Errors));

            // The transformer's AdditionalFilter (e.g. policy row-scope, soft-delete
            // IS NULL) is ANDed onto the WHERE clause so it narrows — never
            // replaces — the primary-key predicate.
            var additionalFilter = MutationCommandExecutor.RenderAdditionalFilter(transformResult.AdditionalFilter, dialect);

            // Rekey to DB column names once so the PK split (via ColumnLookup) and
            // the emitted WHERE/SET use one consistent name space even when a
            // GraphQL field name differs from its column.
            var dbData = DbParameterBinder.ToDbColumnKeys(table, transformResult.Data);

            if (transformResult.MutationType == MutationType.Update)
            {
                // Soft-delete: transformed to UPDATE
                var tableRef = dialect.TableReference(table.TableSchema, table.DbName);

                var keyData = dbData.Where(d => table.ColumnLookup.ContainsKey(d.Key) && table.ColumnLookup[d.Key].IsPrimaryKey)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                var setData = dbData.Where(d => !table.ColumnLookup.ContainsKey(d.Key) || !table.ColumnLookup[d.Key].IsPrimaryKey)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);

                var setClause = string.Join(",", setData.Select(kv => SetAssignment(dialect, table, kv.Key)));
                var whereClause = string.Join(" AND ", keyData.Select(kv => $"{dialect.EscapeIdentifier(kv.Key)}=@{SqlParameterNames.Sanitize(kv.Key)}"));
                var sql = $"UPDATE {tableRef} SET {setClause} WHERE {whereClause}{additionalFilter.WhereSuffix};";
                // Before-commit hooks and the soft-delete write commit atomically.
                var result = 0;
                await MutationCommandExecutor.RunInTransactionAsync(conFactory, async (conn, transaction) =>
                {
                    await MutationNotifier.RunBeforeCommitHooksAsync(context.RequestServices, table, MutationType.Update, dbData, userContext);
                    result = await MutationCommandExecutor.ExecuteNonQuery(conn, transaction, sql, dbData, additionalFilter.Parameters, context.CancellationToken);
                }, context.CancellationToken);
                await MutationNotifier.NotifyMutationAsync(context.RequestServices, table, MutationType.Update, dbData, result, userContext);
                return result;
            }

            // Standard DELETE — adopt the (possibly rewritten) data so transformer
            // output (e.g. enum-name → DB-value mapping on a predicate column)
            // reaches the WHERE clause and parameters, mirroring the soft-delete
            // branch above.
            var deleteData = dbData;
            var deleteTableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var deleteWhereClause = string.Join(" AND ", deleteData.Select(kv => $"{dialect.EscapeIdentifier(kv.Key)}=@{SqlParameterNames.Sanitize(kv.Key)}"));
            var deleteSql = $"DELETE FROM {deleteTableRef} WHERE {deleteWhereClause}{additionalFilter.WhereSuffix};";
            // Before-commit hooks and the delete write commit atomically.
            var deleteResult = 0;
            await MutationCommandExecutor.RunInTransactionAsync(conFactory, async (conn, transaction) =>
            {
                await MutationNotifier.RunBeforeCommitHooksAsync(context.RequestServices, table, MutationType.Delete, deleteData, userContext);
                deleteResult = await MutationCommandExecutor.ExecuteNonQuery(conn, transaction, deleteSql, deleteData, additionalFilter.Parameters, context.CancellationToken);
            }, context.CancellationToken);
            await MutationNotifier.NotifyMutationAsync(context.RequestServices, table, MutationType.Delete, deleteData, deleteResult, userContext);
            return deleteResult;
        }

        private async Task<object?> UpdateObject(IBifrostFieldContext context, IDbTable table,
            IMutationTransformers mutationTransformers, IDbModel model,
            IDbConnFactory conFactory, ISqlDialect dialect, string parameterName = "update")
        {
            var propertyInfo = GetPropertyInfo(context, table, parameterName);
            if (!propertyInfo.data.Any())
                return 0;

            if (!propertyInfo.keyData.Any())
                return 0;

            if (!propertyInfo.standardData.Any())
                return 0;

            // The state-machine load, mutation transformers, before-commit hooks and
            // the update run inside one transaction so the read the transformer gates
            // on and the write it produces commit atomically or roll back together.
            Dictionary<string, object?> updatedData = null!;
            int result = 0;
            StateTransitionInfo? stateTransition = null;
            await MutationCommandExecutor.RunInTransactionAsync(conFactory, async (conn, transaction) =>
            {
                // Mutation transformers (e.g. the authorization policy engine) gate
                // the update before any SQL is built; non-empty Errors abort it.
                var currentRow = await MutationCommandExecutor.LoadCurrentStateMachineRow(
                    conn,
                    transaction,
                    dialect,
                    table,
                    propertyInfo.keyData,
                    context.CancellationToken);
                var transformContext = new MutationTransformContext
                {
                    Model = model,
                    UserContext = context.UserContext,
                    CurrentRow = currentRow,
                    Services = context.RequestServices,
                };
                var transformResult = await mutationTransformers.TransformAsync(table, MutationType.Update, propertyInfo.data, transformContext);
                if (transformResult.Errors.Length > 0)
                    throw new BifrostExecutionError(string.Join("; ", transformResult.Errors));

                // The transformer's AdditionalFilter (e.g. policy row-scope, soft-delete
                // IS NULL) is ANDed onto the WHERE clause so it narrows — never
                // replaces — the primary-key predicate.
                var additionalFilter = MutationCommandExecutor.RenderAdditionalFilter(transformResult.AdditionalFilter, dialect);

                // Adopt the (possibly rewritten) data so transformer output — e.g.
                // enum-name → DB-value mapping — reaches the SQL, rekeyed to real DB
                // column names. keyData is already DB-named (see GetPropertyInfo), so
                // the non-key split and WHERE share one name space; enum columns are
                // non-key.
                updatedData = DbParameterBinder.ToDbColumnKeys(table, transformResult.Data);
                var standardData = updatedData
                    .Where(d => !propertyInfo.keyData.ContainsKey(d.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

                var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
                var setClause = string.Join(",", standardData.Select(kv => SetAssignment(dialect, table, kv.Key)));
                var whereClause = string.Join(" AND ", propertyInfo.keyData.Select(kv => $"{dialect.EscapeIdentifier(kv.Key)}=@{SqlParameterNames.Sanitize(kv.Key)}"));
                var sql = $"UPDATE {tableRef} SET {setClause} WHERE {whereClause}{additionalFilter.WhereSuffix};";
                await MutationNotifier.RunBeforeCommitHooksAsync(context.RequestServices, table, MutationType.Update, updatedData, context.UserContext);
                result = await MutationCommandExecutor.ExecuteNonQuery(conn, transaction, sql, updatedData, additionalFilter.Parameters, context.CancellationToken);
                stateTransition = transformResult.StateTransition;
            }, context.CancellationToken);
            await MutationNotifier.NotifyMutationAsync(context.RequestServices, table, MutationType.Update, updatedData, result, context.UserContext);
            await MutationNotifier.NotifyStateTransitionAsync(context.RequestServices, stateTransition, context.UserContext);
            // The update mutation field is typed `Int`, so its scalar return cannot
            // carry a composite key. For a single-key table we keep returning the key
            // value (identifies the affected row, back-compat). For a composite key
            // `keyData.Values.First()` would silently surface only the FIRST key
            // component — misleading — so instead return the affected row count
            // (0 or 1), consistent with the delete mutation.
            return propertyInfo.keyData.Count == 1
                ? propertyInfo.keyData.Values.First()
                : result;
        }

        // Nested ("tree") sync: accepts a parent object with nested child
        // collections and reconciles it against current database state — inserting
        // new rows, updating changed rows, and deleting orphaned rows — in one
        // transaction. When the root carries a primary key, its existing subtree is
        // loaded and diffed; otherwise the whole tree is a fresh insert.
        //
        // Child links are auto-wired: a conventional FK receives the parent's PK,
        // and a polymorphic link additionally gets its discriminator stamped, so a
        // note synced under a company resolves back through that company's notes.
        // Orphan loading/deletion is scoped per parent (and per discriminator for
        // polymorphic links), so reconciling one parent never touches another's.
        //
        // Each inferred operation is routed through the mutation-transformer
        // pipeline (TreeSyncExecutor) before its SQL is built, so soft-delete,
        // authorization policy, and audit-populate apply to nested and orphan
        // operations exactly as they do to a single-row mutation. In particular an
        // orphaned child on a soft-delete table is rewritten Delete → UPDATE
        // (deleted_at stamped) instead of being physically removed.
        //
        // NOTE (remaining sub-item): _hardDelete is not yet capturable on the sync
        // field — opting a soft-delete orphan into a real DELETE would require
        // emitting + capturing the module arg on the sync mutation field. The
        // default (soft-delete orphans become UPDATE) is correct without it.
        private static async Task<object?> SyncObject(IBifrostFieldContext context, IDbTable table,
            IMutationTransformers mutationTransformers, IDbModel model, IDbConnFactory conFactory, ISqlDialect dialect)
        {
            var tree = context.GetArgument<Dictionary<string, object?>>("sync") ?? new();
            if (tree.Count == 0)
                return null;

            var loader = new TreeSyncStateLoader(dialect);
            var existing = await loader.LoadAsync(table, tree, conFactory);

            var engine = new TreeSyncEngine(model);
            var operations = engine.ComputeOperations(table, tree, existing);

            var executor = new TreeSyncExecutor(dialect);
            var rootId = await executor.ExecuteAsync(
                operations, conFactory, mutationTransformers, model, context.UserContext, context.RequestServices);
            // On a pure insert the executor returns the generated PK; on an update
            // the root already has one, so fall back to the submitted key value.
            rootId ??= RootKeyValue(table, tree);
            await MutationNotifier.NotifyMutationAsync(context.RequestServices, table, MutationType.Insert, tree, rootId, context.UserContext);
            return rootId;
        }

        private static object? RootKeyValue(IDbTable table, Dictionary<string, object?> tree)
        {
            var keys = table.KeyColumns.ToList();
            if (keys.Count != 1)
                return null;
            var data = new Dictionary<string, object?>(tree, StringComparer.OrdinalIgnoreCase);
            return data.TryGetValue(keys[0].ColumnName, out var v) ? v : null;
        }

        private async Task<object?> InsertObject(IBifrostFieldContext context, IDbTable table,
            IMutationTransformers mutationTransformers, IDbModel model,
            IDbConnFactory conFactory, ISqlDialect dialect, string parameterName = "insert")
        {
            var data = context.GetArgument<Dictionary<string, object?>>(parameterName) ?? new();

            // Mutation transformers (e.g. the authorization policy engine) gate
            // the insert before any SQL is built; non-empty Errors abort it.
            var transformContext = new MutationTransformContext { Model = model, UserContext = context.UserContext, Services = context.RequestServices };
            var transformResult = await mutationTransformers.TransformAsync(table, MutationType.Insert, data, transformContext);
            if (transformResult.Errors.Length > 0)
                throw new BifrostExecutionError(string.Join("; ", transformResult.Errors));

            // Adopt the (possibly rewritten) data so transformer output — e.g.
            // enum-name → DB-value mapping — actually reaches the SQL, and rekey
            // GraphQL field names to real DB column names so sanitized/prefixed
            // columns land in the right column. Mirrors the delete path.
            data = DbParameterBinder.ToDbColumnKeys(table, transformResult.Data);

            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var columns = string.Join(",", data.Keys.Select(k => dialect.EscapeIdentifier(k)));
            var values = string.Join(",", data.Keys.Select(k => ValuePlaceholder(dialect, table, k)));
            var returning = dialect.ReturningIdentityClauseFor(table.KeyColumns.Select(k => k.ColumnName).ToList());
            var sql = returning != null
                ? $"INSERT INTO {tableRef}({columns}) VALUES({values}){returning};"
                : $"INSERT INTO {tableRef}({columns}) VALUES({values});SELECT {dialect.LastInsertedIdentity} ID;";

            // Before-commit hooks and the insert (with its identity SELECT) run in
            // one transaction so a hook veto or a failed write rolls back as a unit.
            object? result = null;
            await MutationCommandExecutor.RunInTransactionAsync(conFactory, async (conn, transaction) =>
            {
                await MutationNotifier.RunBeforeCommitHooksAsync(context.RequestServices, table, MutationType.Insert, data, context.UserContext);
                result = HandleDecimals(await MutationCommandExecutor.ExecuteScalar(conn, transaction, sql, data, context.CancellationToken));
            }, context.CancellationToken);
            await MutationNotifier.NotifyMutationAsync(context.RequestServices, table, MutationType.Insert, data, result, context.UserContext);
            return result;
        }


    }
}
