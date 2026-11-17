using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Server.Grpc
{
    /// <summary>The wire-facing outcome of a mutation RPC: the affected row count plus an optional
    /// returned identity (insert only).</summary>
    internal readonly record struct GrpcMutationOutcome(long AffectedRows, string? ReturnedKey);

    /// <summary>
    /// Builds the programmatic write intent for a gRPC Insert/Update/Delete RPC and executes it THROUGH
    /// <see cref="IMutationIntentExecutor"/> — the same write seam every Bifrost adapter uses (RESP,
    /// MCP, S3). There is NO direct SQL, NO <c>SqlExecutionManager</c>, NO tenant/soft-delete predicate,
    /// and NO pipeline of the adapter's own: the adapter supplies ONLY the positional primary key (ALL
    /// columns, composite-safe — never a first-column guess) plus the caller's identity, and the full
    /// <see cref="Resolvers.MutationIntent"/> transformer chain (tenant scoping, audit actor,
    /// soft-delete, field-encryption-on-write, CDC/history hooks, validation, optimistic concurrency)
    /// applies unconditionally (protocol-adapter-security invariant 7).
    ///
    /// <para><b>Scope is structural, not remembered.</b> The pipeline narrows the write from the
    /// identity, so an out-of-tenant PK matches ZERO rows — "caller A cannot write caller B's row"
    /// holds because the pipeline ANDs the tenant/policy predicate, not because the adapter filtered.
    /// A scoped-away Update/Delete is detected via <see cref="MutationIntentResult.AffectedRows"/>,
    /// never <c>Value</c> (the KEY on a single-key update, inert as a count) — invariant 8b. A scoped-away write
    /// reports the SAME <c>affected_rows: 0</c> as a genuinely-absent row, so it is no existence
    /// oracle.</para>
    /// </summary>
    internal static class GrpcMutationDispatcher
    {
        /// <summary>Inserts a new row from the request's column values; returns the REAL affected count (0 when a trigger dropped the row) and the generated identity.</summary>
        public static async Task<GrpcMutationOutcome> InsertAsync(
            IMutationIntentExecutor executor,
            IDbTable table,
            IReadOnlyDictionary<string, object?> requestValues,
            IDictionary<string, object?> userContext,
            string? endpoint,
            CancellationToken cancellationToken)
        {
            var intent = new MutationIntent
            {
                Table = table.DbName,
                Action = MutationIntentAction.Insert,
                // Every supplied column becomes insert data (keyed by GraphQL/DB name, case-insensitive);
                // the pipeline enforces required columns and supplies defaults for the omitted ones. The
                // positional PrimaryKey is left null — an insert addresses no existing row.
                Data = new Dictionary<string, object?>(requestValues, StringComparer.OrdinalIgnoreCase),
                UserContext = new Dictionary<string, object?>(userContext),
                Endpoint = endpoint,
            };

            var result = await executor.ExecuteAsync(intent, cancellationToken);
            // AffectedRows, never a constant (invariant 8b): a policy write-deny throws, but a trigger
            // can drop the row and the INSERT still succeeds with 0 rows. Null is not success. Value
            // carries the generated identity.
            return new GrpcMutationOutcome(result.AffectedRows ?? 0, result.Value?.ToString());
        }

        /// <summary>Updates the addressed row's SET columns; returns the REAL affected count (0 when scoped away).</summary>
        public static async Task<GrpcMutationOutcome> UpdateAsync(
            IMutationIntentExecutor executor,
            IDbTable table,
            IReadOnlyDictionary<string, object?> requestValues,
            IDictionary<string, object?> userContext,
            string? endpoint,
            CancellationToken cancellationToken)
        {
            var (primaryKey, data) = SplitKeyAndData(table, requestValues);
            var intent = new MutationIntent
            {
                Table = table.DbName,
                Action = MutationIntentAction.Update,
                Data = data,
                PrimaryKey = primaryKey,
                UserContext = new Dictionary<string, object?>(userContext),
                Endpoint = endpoint,
            };

            var result = await executor.ExecuteAsync(intent, cancellationToken);
            // AffectedRows, NEVER Value: on a single-key table Value is THE KEY, inert read as a count and
            // misfiring on key value 0. A null count is treated as "did not write" (fail-closed).
            return new GrpcMutationOutcome(result.AffectedRows ?? 0, null);
        }

        /// <summary>Routes a Delete INTENT for the addressed row and lets the pipeline decide hard vs soft.</summary>
        public static async Task<GrpcMutationOutcome> DeleteAsync(
            IMutationIntentExecutor executor,
            IDbTable table,
            IReadOnlyDictionary<string, object?> requestValues,
            IDictionary<string, object?> userContext,
            string? endpoint,
            CancellationToken cancellationToken)
        {
            var intent = new MutationIntent
            {
                Table = table.DbName,
                Action = MutationIntentAction.Delete,
                // The adapter supplies no predicate: only the positional PK. A table with soft-delete
                // metadata is soft-deleted by its transformer — the adapter never special-cases it
                // (invariant 7c).
                Data = new Dictionary<string, object?>(),
                PrimaryKey = KeyValues(table, requestValues),
                UserContext = new Dictionary<string, object?>(userContext),
                Endpoint = endpoint,
            };

            var result = await executor.ExecuteAsync(intent, cancellationToken);
            // AffectedRows, never Value (invariant 8b): the intent executor reports a delete's count
            // there just as it does an update's. A null count is treated as "did not write".
            return new GrpcMutationOutcome(result.AffectedRows ?? 0, null);
        }

        /// <summary>
        /// Splits a decoded Update request into its positional primary key (all key columns, required)
        /// and the SET data (every non-key column present). Never index-zero-reduces a composite key.
        /// </summary>
        private static (object?[] PrimaryKey, Dictionary<string, object?> Data) SplitKeyAndData(
            IDbTable table, IReadOnlyDictionary<string, object?> requestValues)
        {
            var keyNames = new HashSet<string>(
                table.KeyColumns.Select(c => c.GraphQlName), StringComparer.OrdinalIgnoreCase);
            var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in requestValues)
                if (!keyNames.Contains(name))
                    data[name] = value;
            return (KeyValues(table, requestValues), data);
        }

        /// <summary>
        /// Reads the positional primary-key values in declared key-column order (composite-PK safe).
        /// A missing key field is a clean INVALID_ARGUMENT naming ONLY the request field — never an
        /// internal column/table/SQL identifier (invariant 3). Mirrors <see cref="GrpcReadDispatcher"/>'s
        /// Get key extraction so read and write address a row identically.
        /// </summary>
        private static object?[] KeyValues(IDbTable table, IReadOnlyDictionary<string, object?> requestValues)
        {
            var keyColumns = table.KeyColumns.ToList();
            var keyValues = new object?[keyColumns.Count];
            for (var i = 0; i < keyColumns.Count; i++)
            {
                if (!requestValues.TryGetValue(keyColumns[i].GraphQlName, out var value))
                    throw GrpcRequestException.InvalidField(
                        keyColumns[i].GraphQlName, "Required primary-key field is missing.");
                keyValues[i] = value;
            }
            return keyValues;
        }
    }
}
