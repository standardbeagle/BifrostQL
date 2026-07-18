using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Server.Grpc
{
    /// <summary>
    /// Builds the programmatic read intent for a gRPC RPC and executes it THROUGH
    /// <see cref="IQueryIntentExecutor"/> — the same read seam every Bifrost adapter uses. There is
    /// no direct SQL, no <c>SqlExecutionManager</c>, and no GraphQL text here: the adapter hands a
    /// <see cref="GqlObjectQuery"/> tree to the executor and the transformer pipeline
    /// (tenant/soft-delete/policy row scope, column read guards) applies unconditionally.
    ///
    /// <para>The adapter builds NO predicate of its own beyond the positional primary key on a Get:
    /// List/Stream carry no WHERE at all, so tenant scope is ANDed on by the pipeline and an
    /// out-of-scope caller simply sees fewer/zero rows. A Get keys by ALL primary-key columns via
    /// <see cref="TableFilter.FromPrimaryKey"/> — never a first-column guess (composite-PK
    /// compliance).</para>
    /// </summary>
    internal static class GrpcReadDispatcher
    {
        /// <summary>Resolves a single row by its full primary key, or null when it is missing OR out of the caller's scope.</summary>
        public static async Task<IReadOnlyDictionary<string, object?>?> GetByKeyAsync(
            IQueryIntentExecutor executor,
            IDbTable table,
            IReadOnlyDictionary<string, object?> requestValues,
            IDictionary<string, object?> userContext,
            string? endpoint,
            CancellationToken cancellationToken)
        {
            var keyColumns = table.KeyColumns.ToList();
            var keyValues = new object?[keyColumns.Count];
            for (var i = 0; i < keyColumns.Count; i++)
            {
                if (!requestValues.TryGetValue(keyColumns[i].GraphQlName, out var value))
                    throw GrpcRequestException.InvalidArgument(
                        $"Get request is missing primary-key field '{keyColumns[i].GraphQlName}'.");
                keyValues[i] = value;
            }

            var query = BuildRowQuery(table);
            query.Filter = TableFilter.FromPrimaryKey(keyValues, keyColumns, table.DbName);
            query.Limit = 1;

            var result = await executor.ExecuteAsync(NewIntent(query, userContext, endpoint), cancellationToken);
            return result.Rows.Count > 0 ? result.Rows[0] : null;
        }

        /// <summary>
        /// Resolves up to <paramref name="limit"/> rows of a table with NO adapter predicate. The
        /// limit is the caller-independent bound the pipeline applies after its tenant/soft-delete
        /// scope — a streaming List is therefore never unbounded (invariant 6).
        /// </summary>
        public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ListAsync(
            IQueryIntentExecutor executor,
            IDbTable table,
            int limit,
            IDictionary<string, object?> userContext,
            string? endpoint,
            CancellationToken cancellationToken)
        {
            var query = BuildRowQuery(table);
            query.Sort = table.KeyColumns.Select(k => $"{k.GraphQlName}_asc").ToList();
            query.Limit = limit;

            var result = await executor.ExecuteAsync(NewIntent(query, userContext, endpoint), cancellationToken);
            return result.Rows;
        }

        private static QueryIntent NewIntent(
            GqlObjectQuery query, IDictionary<string, object?> userContext, string? endpoint) => new()
            {
                Query = query,
                UserContext = new Dictionary<string, object?>(userContext),
                Endpoint = endpoint,
            };

        private static GqlObjectQuery BuildRowQuery(IDbTable table)
        {
            var query = new GqlObjectQuery
            {
                DbTable = table,
                SchemaName = table.TableSchema,
                TableName = table.DbName,
                GraphQlName = table.GraphQlName,
                Path = table.GraphQlName,
            };
            foreach (var column in table.Columns.OrderBy(c => c.OrdinalPosition))
                query.ScalarColumns.Add(new GqlObjectColumn(column.DbName));
            return query;
        }
    }
}
