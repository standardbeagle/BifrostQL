using System.Data.Common;
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
                return await SyncObject(context, table, model, conFactory, dialect);
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
                var keyColumns = propertyInfo.keyData.Keys.ToList();
                var updateColumns = propertyInfo.standardData.Keys.ToList();
                var upsertSql = dialect.UpsertSql(tableRef, keyColumns, propertyInfo.data.Keys.ToList(), updateColumns);

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
                    var transformResult = mutationTransformers.Transform(table, MutationType.Update, propertyInfo.data, upsertTransformContext);
                    if (transformResult.Errors.Length > 0)
                        throw new BifrostExecutionError(string.Join("; ", transformResult.Errors));

                    var upsertData = transformResult.Data;
                    var returning = dialect.ReturningIdentityClauseFor(table.KeyColumns.Select(k => k.ColumnName).ToList());
                    var identitySql = returning != null
                        ? upsertSql.TrimEnd(';') + returning + ";"
                        : upsertSql + $"SELECT {dialect.LastInsertedIdentity} ID;";
                    return HandleDecimals(await ExecuteScalar(conFactory, identitySql, upsertData));
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

        private (Dictionary<string, object?> data, Dictionary<string, object?> keyData, Dictionary<string, object?> standardData) GetPropertyInfo(IBifrostFieldContext context, IDbTable table, string parameterName)
        {
            var baseData = context.GetArgument<Dictionary<string, object?>>(parameterName) ?? new();

            var data = new Dictionary<string, object?>(baseData!, StringComparer.OrdinalIgnoreCase);

            var pkKeyData = ResolvePrimaryKeyArgument(context, table);
            var keyData = pkKeyData
                ?? data.Where(d => table.ColumnLookup[d.Key].IsPrimaryKey)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);

            var standardData = data
                .Where(d => !keyData.ContainsKey(d.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            var allData = new Dictionary<string, object?>(standardData, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in keyData)
                allData[kv.Key] = kv.Value;

            return (allData, keyData, standardData);
        }

        private static Dictionary<string, object?>? ResolvePrimaryKeyArgument(IBifrostFieldContext context, IDbTable table)
        {
            if (!context.HasArgument("_primaryKey"))
                return null;

            var pkValues = context.GetArgument<List<object?>>("_primaryKey");
            if (pkValues == null || pkValues.Count == 0)
                return null;

            var keyColumns = table.KeyColumns.ToList();

            if (keyColumns.Count == 0)
                throw new BifrostExecutionError($"Table '{table.DbName}' has no primary key columns.");

            if (pkValues.Count != keyColumns.Count)
                throw new BifrostExecutionError(
                    $"_primaryKey for '{table.DbName}' expects {keyColumns.Count} value(s) " +
                    $"({string.Join(", ", keyColumns.Select(c => c.GraphQlName))}) but received {pkValues.Count}.");

            return keyColumns.Zip(pkValues, (col, val) => new { col.ColumnName, Value = val })
                .ToDictionary(x => x.ColumnName, x => x.Value);
        }

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
            var transformResult = mutationTransformers.Transform(table, MutationType.Delete, data, transformContext);

            if (transformResult.Errors.Length > 0)
                throw new BifrostExecutionError(string.Join("; ", transformResult.Errors));

            // The transformer's AdditionalFilter (e.g. policy row-scope, soft-delete
            // IS NULL) is ANDed onto the WHERE clause so it narrows — never
            // replaces — the primary-key predicate.
            var additionalFilter = RenderAdditionalFilter(transformResult.AdditionalFilter, dialect);

            if (transformResult.MutationType == MutationType.Update)
            {
                // Soft-delete: transformed to UPDATE
                var tableRef = dialect.TableReference(table.TableSchema, table.DbName);

                var keyData = transformResult.Data.Where(d => table.ColumnLookup.ContainsKey(d.Key) && table.ColumnLookup[d.Key].IsPrimaryKey)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                var setData = transformResult.Data.Where(d => !table.ColumnLookup.ContainsKey(d.Key) || !table.ColumnLookup[d.Key].IsPrimaryKey)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);

                var setClause = string.Join(",", setData.Select(kv => SetAssignment(dialect, table, kv.Key)));
                var whereClause = string.Join(" AND ", keyData.Select(kv => $"{dialect.EscapeIdentifier(kv.Key)}=@{kv.Key}"));
                var sql = $"UPDATE {tableRef} SET {setClause} WHERE {whereClause}{additionalFilter.WhereSuffix};";
                var result = await ExecuteNonQuery(conFactory, sql, transformResult.Data, additionalFilter.Parameters);
                await NotifyMutationAsync(context.RequestServices, table, MutationType.Update, transformResult.Data, result, userContext);
                return result;
            }

            // Standard DELETE — adopt the (possibly rewritten) data so transformer
            // output (e.g. enum-name → DB-value mapping on a predicate column)
            // reaches the WHERE clause and parameters, mirroring the soft-delete
            // branch above.
            var deleteData = transformResult.Data;
            var deleteTableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var deleteWhereClause = string.Join(" AND ", deleteData.Select(kv => $"{dialect.EscapeIdentifier(kv.Key)}=@{kv.Key}"));
            var deleteSql = $"DELETE FROM {deleteTableRef} WHERE {deleteWhereClause}{additionalFilter.WhereSuffix};";
            var deleteResult = await ExecuteNonQuery(conFactory, deleteSql, deleteData, additionalFilter.Parameters);
            await NotifyMutationAsync(context.RequestServices, table, MutationType.Delete, deleteData, deleteResult, userContext);
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

            // Mutation transformers (e.g. the authorization policy engine) gate
            // the update before any SQL is built; non-empty Errors abort it.
            var currentRow = await LoadCurrentStateMachineRow(
                conFactory,
                dialect,
                table,
                propertyInfo.keyData);
            var transformContext = new MutationTransformContext
            {
                Model = model,
                UserContext = context.UserContext,
                CurrentRow = currentRow,
                Services = context.RequestServices,
            };
            var transformResult = mutationTransformers.Transform(table, MutationType.Update, propertyInfo.data, transformContext);
            if (transformResult.Errors.Length > 0)
                throw new BifrostExecutionError(string.Join("; ", transformResult.Errors));

            // The transformer's AdditionalFilter (e.g. policy row-scope, soft-delete
            // IS NULL) is ANDed onto the WHERE clause so it narrows — never
            // replaces — the primary-key predicate.
            var additionalFilter = RenderAdditionalFilter(transformResult.AdditionalFilter, dialect);

            // Adopt the (possibly rewritten) data so transformer output — e.g.
            // enum-name → DB-value mapping — reaches the SQL. Keys are split anew
            // against the (unchanged) primary-key set; enum columns are non-key.
            var updatedData = transformResult.Data;
            var standardData = updatedData
                .Where(d => !propertyInfo.keyData.ContainsKey(d.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var setClause = string.Join(",", standardData.Select(kv => SetAssignment(dialect, table, kv.Key)));
            var whereClause = string.Join(" AND ", propertyInfo.keyData.Select(kv => $"{dialect.EscapeIdentifier(kv.Key)}=@{kv.Key}"));
            var sql = $"UPDATE {tableRef} SET {setClause} WHERE {whereClause}{additionalFilter.WhereSuffix};";
            var result = await ExecuteNonQuery(conFactory, sql, updatedData, additionalFilter.Parameters);
            await NotifyMutationAsync(context.RequestServices, table, MutationType.Update, updatedData, result, context.UserContext);
            await NotifyStateTransitionAsync(context.RequestServices, transformResult.StateTransition, context.UserContext);
            return propertyInfo.keyData.Values.First();
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
        // NOTE (v1 scope): the nested path bypasses the mutation transformer /
        // module pipeline — no policy gating, no audit-populate. Columns relying on
        // populate metadata must have a database default. Tracked as follow-up.
        private static async Task<object?> SyncObject(IBifrostFieldContext context, IDbTable table,
            IDbModel model, IDbConnFactory conFactory, ISqlDialect dialect)
        {
            var tree = context.GetArgument<Dictionary<string, object?>>("sync") ?? new();
            if (tree.Count == 0)
                return null;

            var loader = new TreeSyncStateLoader(dialect);
            var existing = await loader.LoadAsync(table, tree, conFactory);

            var engine = new TreeSyncEngine(model);
            var operations = engine.ComputeOperations(table, tree, existing);

            var executor = new TreeSyncExecutor(dialect);
            var rootId = await executor.ExecuteAsync(operations, conFactory);
            // On a pure insert the executor returns the generated PK; on an update
            // the root already has one, so fall back to the submitted key value.
            rootId ??= RootKeyValue(table, tree);
            await NotifyMutationAsync(context.RequestServices, table, MutationType.Insert, tree, rootId, context.UserContext);
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
            var transformResult = mutationTransformers.Transform(table, MutationType.Insert, data, transformContext);
            if (transformResult.Errors.Length > 0)
                throw new BifrostExecutionError(string.Join("; ", transformResult.Errors));

            // Adopt the (possibly rewritten) data so transformer output — e.g.
            // enum-name → DB-value mapping — actually reaches the SQL. Mirrors the
            // delete path; equals the original data when no transformer applies.
            data = transformResult.Data;

            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var columns = string.Join(",", data.Keys.Select(k => dialect.EscapeIdentifier(k)));
            var values = string.Join(",", data.Keys.Select(k => ValuePlaceholder(dialect, table, k)));
            var returning = dialect.ReturningIdentityClauseFor(table.KeyColumns.Select(k => k.ColumnName).ToList());
            var sql = returning != null
                ? $"INSERT INTO {tableRef}({columns}) VALUES({values}){returning};"
                : $"INSERT INTO {tableRef}({columns}) VALUES({values});SELECT {dialect.LastInsertedIdentity} ID;";
            var result = HandleDecimals(await ExecuteScalar(conFactory, sql, data));
            await NotifyMutationAsync(context.RequestServices, table, MutationType.Insert, data, result, context.UserContext);
            return result;
        }

        private static async ValueTask<object?> ExecuteScalar(IDbConnFactory connFactory, string sql, Dictionary<string, object?> data)
        {
            await using var conn = connFactory.GetConnection();
            try
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                AddParameters(cmd, data);
                return await cmd.ExecuteScalarAsync();
            }
            catch (Exception ex)
            {
                throw new BifrostExecutionError(ex.Message, ex);
            }
        }

        private static async Task<IReadOnlyDictionary<string, object?>?> LoadCurrentStateMachineRow(
            IDbConnFactory connFactory,
            ISqlDialect dialect,
            IDbTable table,
            Dictionary<string, object?> keyData)
        {
            var definition = StateMachineConfigCollector.FromTable(table);
            if (definition is null || keyData.Count == 0)
                return null;

            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var stateColumn = dialect.EscapeIdentifier(definition.StateColumn);
            var whereClause = string.Join(" AND ", keyData.Select(kv => $"{dialect.EscapeIdentifier(kv.Key)}=@{kv.Key}"));
            var sql = $"SELECT {stateColumn} FROM {tableRef} WHERE {whereClause};";

            await using var conn = connFactory.GetConnection();
            try
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                AddParameters(cmd, keyData);
                var currentState = await cmd.ExecuteScalarAsync();
                return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    [definition.StateColumn] = currentState == DBNull.Value ? null : currentState,
                };
            }
            catch (Exception ex)
            {
                throw new BifrostExecutionError(ex.Message, ex);
            }
        }
        private static async ValueTask<int> ExecuteNonQuery(IDbConnFactory connFactory, string sql,
            Dictionary<string, object?> data, IReadOnlyList<SqlParameterInfo>? extraParameters = null)
        {
            await using var conn = connFactory.GetConnection();
            try
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                AddParameters(cmd, data);
                AddExtraParameters(cmd, extraParameters);
                return await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                throw new BifrostExecutionError(ex.Message, ex);
            }
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

        private static async ValueTask NotifyStateTransitionAsync(
            IServiceProvider? services,
            StateTransitionInfo? transition,
            IDictionary<string, object?> userContext)
        {
            if (transition is null || services is null)
                return;

            var observers = services.GetService<StateTransitionObservers>();
            if (observers is not null)
                await observers.NotifyAsync(transition, userContext);
        }

        private static async ValueTask NotifyMutationAsync(
            IServiceProvider? services,
            IDbTable table,
            MutationType mutationType,
            IDictionary<string, object?> data,
            object? result,
            IDictionary<string, object?> userContext)
        {
            if (services is null || IsWorkflowTriggerSuppressed(userContext))
                return;

            var observers = services.GetService<MutationObservers>();
            if (observers is not null)
            {
                await observers.NotifyAsync(new MutationObserverContext
                {
                    Table = table,
                    MutationType = mutationType,
                    Data = data,
                    Result = result,
                    UserContext = userContext,
                });
            }
        }

        private static bool IsWorkflowTriggerSuppressed(IDictionary<string, object?> userContext)
            => userContext.TryGetValue(BifrostQL.Core.Workflows.WorkflowTriggerHost.SuppressTriggersKey, out var value)
               && value is bool suppressed
               && suppressed;

    }
}
