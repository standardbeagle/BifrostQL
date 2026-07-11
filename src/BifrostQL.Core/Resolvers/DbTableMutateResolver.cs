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
    /// <summary>
    /// GraphQL entry point for single-row mutations. Argument extraction (verb
    /// selection, <c>_primaryKey</c>, module arguments) happens here; execution —
    /// the mutation transformer chain, hooks, parameterized SQL — is delegated to
    /// <see cref="TableMutationPipeline"/>, the seam shared with the protocol-adapter
    /// mutation-intent path.
    /// </summary>
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

            switch (MutationActionSelector.FromContext(context))
            {
                case MutationAction.Sync:
                    return await SyncObject(context, table, mutationTransformers, model, conFactory, dialect);
                case MutationAction.Insert:
                    return await InsertObject(context, table, mutationTransformers, model, conFactory);
                case MutationAction.Update:
                    return await UpdateObject(context, table, mutationTransformers, model, conFactory);
                case MutationAction.Delete:
                    return await DeleteObject(context, mutationTransformers, table, model, conFactory);
                case MutationAction.Upsert:
                    return await UpsertObject(context, table, mutationTransformers, model, conFactory, dialect);
                default:
                    return null;
            }
        }

        private async Task<object?> UpsertObject(IBifrostFieldContext context, IDbTable table,
            IMutationTransformers mutationTransformers, IDbModel model,
            IDbConnFactory conFactory, ISqlDialect dialect)
        {
            var propertyInfo = GetPropertyInfo(context, _table, "upsert");
            if (!propertyInfo.data.Any())
                return 0;

            // A true upsert is routed through the real Insert-or-Update decision
            // rather than a native single-statement UpsertSql. A single statement
            // (ON CONFLICT / MERGE) cannot express a transformer's AdditionalFilter
            // — tenant/policy row-scope, soft-delete IS NULL — as a guard on its
            // INSERT branch, so a caller could take over a row in another tenant or
            // resurrect a soft-deleted one. It also runs the pipeline as Update with
            // no CurrentRow, which skips created-* stamps, state-machine
            // current-state validation and insert-required checks. Probing existence
            // by primary key and dispatching to InsertObject / UpdateObject makes
            // every one of those enforcements apply exactly as for a plain
            // insert/update, and rebuilds the column list from post-transform data.
            //
            // The probe is a read outside the write transaction; the database's
            // primary-key / unique constraint is the real arbiter (a lost insert
            // race fails the INSERT, a lost update race affects 0 rows), so a
            // concurrent writer cannot cause silent data corruption here.
            if (propertyInfo.keyData.Any()
                && await RowExistsAsync(conFactory, dialect, table, propertyInfo.keyData, context.CancellationToken))
                return await UpdateObject(context, table, mutationTransformers, model, conFactory, "upsert");

            return await InsertObject(context, table, mutationTransformers, model, conFactory, "upsert");
        }

        ValueTask<object?> IFieldResolver.ResolveAsync(IResolveFieldContext context)
        {
            return ResolveAsync(new BifrostFieldContextAdapter(context));
        }

        /// <summary>
        /// Builds the shared pipeline context from the GraphQL field context: the
        /// request's user context, the request-scoped service provider (hooks,
        /// observers), and optional module arguments (delete's <c>_hardDelete</c>).
        /// </summary>
        private static MutationPipelineContext BuildPipelineContext(
            IBifrostFieldContext context, IDbModel model, IDbConnFactory conFactory,
            IMutationTransformers mutationTransformers,
            IReadOnlyDictionary<string, object?>? moduleArguments = null)
            => new()
            {
                Model = model,
                ConnFactory = conFactory,
                Transformers = mutationTransformers,
                UserContext = context.UserContext,
                Services = context.RequestServices,
                ModuleArguments = moduleArguments ?? ModuleApiRegistry.EmptyArguments,
                CancellationToken = context.CancellationToken,
            };

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

        // Probes whether a row keyed by the given primary-key values already exists,
        // so the upsert path can dispatch to the safe Insert or Update executor. The
        // probe uses its own connection; the database's PK/unique constraint remains
        // the authority under a concurrent writer (see the upsert call site).
        private static async Task<bool> RowExistsAsync(
            IDbConnFactory connFactory, ISqlDialect dialect, IDbTable table,
            Dictionary<string, object?> keyData, CancellationToken cancellationToken)
        {
            if (keyData.Count == 0)
                return false;

            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var whereClause = MutationCommandExecutor.BuildKeyPredicate(dialect, keyData.Keys);
            var sql = $"SELECT 1 FROM {tableRef} WHERE {whereClause};";

            await using var conn = connFactory.GetConnection();
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            AddParameters(cmd, keyData);
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result != null && result != DBNull.Value;
        }

        private async Task<object?> DeleteObject(IBifrostFieldContext context,
            IMutationTransformers mutationTransformers, IDbTable table, IDbModel model,
            IDbConnFactory conFactory)
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

            var ctx = BuildPipelineContext(context, model, conFactory, mutationTransformers,
                ModuleApiRegistry.CaptureMutationArguments(context, table));
            return await TableMutationPipeline.DeleteAsync(table, data, ctx);
        }

        private async Task<object?> UpdateObject(IBifrostFieldContext context, IDbTable table,
            IMutationTransformers mutationTransformers, IDbModel model,
            IDbConnFactory conFactory, string parameterName = "update")
        {
            var propertyInfo = GetPropertyInfo(context, table, parameterName);
            var ctx = BuildPipelineContext(context, model, conFactory, mutationTransformers);
            return await TableMutationPipeline.UpdateAsync(table, propertyInfo, ctx);
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
            IDbConnFactory conFactory, string parameterName = "insert")
        {
            var data = context.GetArgument<Dictionary<string, object?>>(parameterName) ?? new();
            var ctx = BuildPipelineContext(context, model, conFactory, mutationTransformers);
            return await TableMutationPipeline.InsertAsync(table, data, ctx);
        }
    }
}
