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
                    // The violation names ONLY the request field (its proto/GraphQL name) — never the
                    // underlying column/table/SQL (invariant 3 / criterion 4).
                    throw GrpcRequestException.InvalidField(
                        keyColumns[i].GraphQlName, "Required primary-key field is missing.");
                keyValues[i] = value;
            }

            var query = BuildRowQuery(table);
            query.Filter = TableFilter.FromPrimaryKey(keyValues, keyColumns, table.DbName);
            query.Limit = 1;

            var result = await executor.ExecuteAsync(NewIntent(query, userContext, endpoint), cancellationToken);
            return result.Rows.Count > 0 ? result.Rows[0] : null;
        }

        /// <summary>
        /// Executes an ALREADY-COMPILED List/Stream query (built by
        /// <see cref="GrpcReadRequestCompiler"/> — the ONE compiler both RPCs share) through the read
        /// pipeline and returns its rows. The adapter never builds scope here: the compiled filter is
        /// AND-composed by the pipeline with tenant/soft-delete/policy, and the query's Limit bounds
        /// the row count so neither a unary List nor a server-streaming Stream is ever unbounded
        /// (invariant 6). The RPC's <see cref="CancellationToken"/> (deadline/cancel) threads straight
        /// into the executor.
        /// </summary>
        public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> RunAsync(
            IQueryIntentExecutor executor,
            GqlObjectQuery query,
            IDictionary<string, object?> userContext,
            string? endpoint,
            CancellationToken cancellationToken)
        {
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
