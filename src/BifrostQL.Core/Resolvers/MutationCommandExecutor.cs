using System.Data.Common;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Model;
using static BifrostQL.Core.Resolvers.DbParameterBinder;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// Database command + transaction primitives split out of
    /// <see cref="DbTableMutateResolver"/>. Runs a single-row mutation's reads
    /// (state-machine load, identity SELECT) and its one write inside a single
    /// connection + transaction so the whole path — before-commit hooks included —
    /// commits atomically or rolls back as a unit. Observer notifications fire from
    /// the caller AFTER the transaction returns so they never describe rolled-back
    /// work (see <see cref="MutationNotifier"/>).
    /// </summary>
    internal static class MutationCommandExecutor
    {
        public static async Task RunInTransactionAsync(
            IDbConnFactory connFactory,
            Func<DbConnection, DbTransaction, Task> work,
            CancellationToken cancellationToken = default)
        {
            // Honour a client abort before we take a connection or open a
            // transaction; once the write is in flight the individual ADO calls
            // observe the token themselves (rollback deliberately does not — it must
            // run to completion even when the request is being torn down).
            cancellationToken.ThrowIfCancellationRequested();
            await using var conn = connFactory.GetConnection();
            DbTransaction? transaction = null;
            try
            {
                await conn.OpenAsync(cancellationToken);
                transaction = await conn.BeginTransactionAsync(cancellationToken);
                await work(conn, transaction);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (BifrostExecutionError)
            {
                if (transaction != null)
                    await transaction.RollbackAsync();
                throw;
            }
            catch (OperationCanceledException)
            {
                // Propagate request aborts as-is (after rolling back) so the pipeline
                // can short-circuit; wrapping them would mask the cancellation.
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
        }

        /// <summary>
        /// Renders the AND-joined <c>"col"=@col</c> equality predicate shared by every
        /// UPDATE/DELETE WHERE clause (and the state-machine load). Each column name is
        /// escaped as an identifier and sanitized for its parameter placeholder, matching
        /// the placeholders <see cref="AddParameters"/> binds.
        /// </summary>
        public static string BuildKeyPredicate(ISqlDialect dialect, IEnumerable<string> columns)
            => string.Join(" AND ", columns.Select(c => $"{dialect.EscapeIdentifier(c)}=@{SqlParameterNames.Sanitize(c)}"));

        /// <summary>
        /// Builds the <c>INSERT INTO tableRef(cols) VALUES(placeholders)</c> prefix
        /// shared by the single-row and batch inserts (callers append their own
        /// terminator: a RETURNING/identity clause, or a bare <c>;</c>). Columns and
        /// value placeholders are drawn from one snapshot of the column set so they
        /// stay positionally paired.
        /// </summary>
        public static string BuildInsertInto(ISqlDialect dialect, IDbTable table, string tableRef, IEnumerable<string> columns)
        {
            var cols = columns.ToList();
            var columnList = string.Join(",", cols.Select(dialect.EscapeIdentifier));
            var valueList = string.Join(",", cols.Select(c => ValuePlaceholder(dialect, table, c)));
            return $"INSERT INTO {tableRef}({columnList}) VALUES({valueList})";
        }

        /// <summary>
        /// Builds the full <c>UPDATE tableRef SET … WHERE …suffix;</c> statement shared
        /// by the single-row update, batch update, and the soft-delete DELETE→UPDATE
        /// rewrite. <paramref name="whereSuffix"/> carries a transformer's ANDed
        /// AdditionalFilter (empty when none).
        /// </summary>
        public static string BuildUpdateSql(ISqlDialect dialect, IDbTable table, string tableRef,
            IEnumerable<string> setColumns, IEnumerable<string> keyColumns, string whereSuffix)
        {
            var setClause = string.Join(",", setColumns.Select(c => SetAssignment(dialect, table, c)));
            var whereClause = BuildKeyPredicate(dialect, keyColumns);
            return $"UPDATE {tableRef} SET {setClause} WHERE {whereClause}{whereSuffix};";
        }

        /// <summary>
        /// Builds the filtered set-update statement: <c>UPDATE tableRef SET … WHERE whereSql;</c>.
        /// Unlike <see cref="BuildUpdateSql"/> there is NO key predicate — the caller-supplied
        /// filter ANDed with the transformers' row scope IS the entire WHERE, already rendered
        /// (and parameterized) by the filtered-update pipeline, which is the only legal caller.
        /// </summary>
        public static string BuildFilteredUpdateSql(ISqlDialect dialect, IDbTable table, string tableRef,
            IEnumerable<string> setColumns, string whereSql)
        {
            var setClause = string.Join(",", setColumns.Select(c => SetAssignment(dialect, table, c)));
            return $"UPDATE {tableRef} SET {setClause} WHERE {whereSql};";
        }

        /// <summary>
        /// Builds the full <c>DELETE FROM tableRef WHERE …suffix;</c> statement shared by
        /// the single-row and batch hard-delete paths. <paramref name="whereSuffix"/>
        /// carries a transformer's ANDed AdditionalFilter (empty when none).
        /// </summary>
        public static string BuildDeleteSql(ISqlDialect dialect, string tableRef,
            IEnumerable<string> whereColumns, string whereSuffix)
            => $"DELETE FROM {tableRef} WHERE {BuildKeyPredicate(dialect, whereColumns)}{whereSuffix};";

        public static string BuildExistsSql(ISqlDialect dialect, string tableRef, IEnumerable<string> keyColumns)
            => $"SELECT 1 FROM {tableRef} WHERE {BuildKeyPredicate(dialect, keyColumns)};";

        public static string BuildFilteredCountSql(string tableRef, string whereSql)
            => $"SELECT COUNT(*) FROM {tableRef} WHERE {whereSql};";

        public static async ValueTask<object?> ExecuteScalar(DbConnection conn, DbTransaction transaction, string sql, Dictionary<string, object?> data, CancellationToken cancellationToken = default)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = transaction;
            AddParameters(cmd, data);
            return await cmd.ExecuteScalarAsync(cancellationToken);
        }

        // transaction may be null when the caller manages the transaction at the SQL
        // level (dialect BEGIN/COMMIT keywords) rather than through the ADO.NET
        // DbTransaction API — the TreeSync path. A null cmd.Transaction then runs the
        // command on the connection's ambient (SQL-level) transaction.
        public static async ValueTask<int> ExecuteNonQuery(DbConnection conn, DbTransaction? transaction, string sql,
            Dictionary<string, object?> data, IReadOnlyList<SqlParameterInfo>? extraParameters = null, CancellationToken cancellationToken = default)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = transaction;
            AddParameters(cmd, data);
            AddExtraParameters(cmd, extraParameters);
            return await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        /// <summary>
        /// Reads the current state-machine column for the keyed row inside the update
        /// transaction, so a state-transition transformer can gate on it. Returns null
        /// when the table has no state machine or no key values were supplied.
        ///
        /// <para>The read is narrowed by the SAME row scope the update itself carries —
        /// the transformer chain's AdditionalFilter (tenant, policy row-scope,
        /// soft-delete). Keying it on the primary key ALONE let the state-machine
        /// transformer gate on a row the caller cannot write: a transition the victim's
        /// stored state permits returned success (a zero-row no-op), while one it
        /// forbids returned a denial, so the caller could walk another tenant's state
        /// space one guess at a time. Scoped, an unreachable row reads as no row at all,
        /// the transformer denies, and the answer is byte-identical to a row that does
        /// not exist (.claude/rules/protocol-adapter-security.md invariant 2). This is
        /// the read counterpart of invariant 8(c): the write already scoped itself, so
        /// the read that gates it must scope itself the same way.</para>
        /// </summary>
        public static async Task<IReadOnlyDictionary<string, object?>?> LoadCurrentStateMachineRow(
            DbConnection conn,
            DbTransaction transaction,
            ISqlDialect dialect,
            IDbTable table,
            Dictionary<string, object?> keyData,
            IMutationTransformers transformers,
            MutationTransformContext scopeContext,
            CancellationToken cancellationToken = default)
        {
            var definition = StateMachineConfigCollector.FromTable(table);
            if (definition is null || keyData.Count == 0)
                return null;

            var scope = await RenderRowScopeAsync(table, keyData, transformers, scopeContext, dialect);

            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var stateColumn = dialect.EscapeIdentifier(definition.StateColumn);
            var whereClause = BuildKeyPredicate(dialect, keyData.Keys);
            var sql = $"SELECT {stateColumn} FROM {tableRef} WHERE {whereClause}{scope.WhereSuffix};";

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = transaction;
            AddParameters(cmd, keyData);
            AddExtraParameters(cmd, scope.Parameters);
            var currentState = await cmd.ExecuteScalarAsync(cancellationToken);
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [definition.StateColumn] = currentState == DBNull.Value ? null : currentState,
            };
        }

        /// <summary>
        /// Asks the mutation transformer chain — the SAME chain the write runs, never a
        /// second hand-rolled construction of tenant/policy scope (invariant 4) — which
        /// rows this caller may update on this table, and renders it as a WHERE suffix.
        ///
        /// <para>The probe payload is the PRIMARY KEY ONLY, deliberately: row scope is a
        /// property of the caller and the table, so the key is all a scope-contributing
        /// transformer needs. Passing the full payload would drag in the optimistic
        /// concurrency token's predicate, and a stale token would then make the state
        /// load read as "no such row" — reporting a lost update as an illegal transition
        /// instead of the CONFLICT the write itself raises. Omitting the state column
        /// also keeps the state-machine transformer itself a no-op here, so the probe
        /// cannot deny on the very question it exists to answer.</para>
        ///
        /// <para>Only the filter is kept. Rewritten data, errors and flags are discarded:
        /// the real chain run that follows produces them against the loaded state, and
        /// this probe must never be the thing that aborts a mutation.</para>
        /// </summary>
        private static async Task<(string WhereSuffix, IReadOnlyList<SqlParameterInfo> Parameters)> RenderRowScopeAsync(
            IDbTable table,
            Dictionary<string, object?> keyData,
            IMutationTransformers transformers,
            MutationTransformContext scopeContext,
            ISqlDialect dialect)
        {
            var probe = await transformers.TransformAsync(
                table,
                MutationType.Update,
                new Dictionary<string, object?>(keyData, StringComparer.OrdinalIgnoreCase),
                scopeContext);
            return RenderAdditionalFilter(probe.AdditionalFilter, dialect);
        }

        /// <summary>
        /// Reads the named columns of one keyed row inside the caller's transaction, for a
        /// hook that must observe the row itself rather than the mutation's write inputs —
        /// the change-history before-image (read before the write) and after-image (read
        /// after it, so DB defaults and triggers are reflected, not guessed).
        ///
        /// The predicate is the primary key ONLY: it is deliberately NOT narrowed by the
        /// mutation's tenant/policy/soft-delete filter. Narrowing here would need a second,
        /// parallel construction of that filter, and a subtly different one would silently
        /// drop a before-image for a legitimate write. The caller's own write carries the
        /// narrowed predicate, so an out-of-scope row is one the write affects zero rows of
        /// — and a zero-row write records no history, which discards the read. The row is
        /// therefore never recorded anywhere the narrowed write did not itself reach.
        ///
        /// Returns null when no row matches the key.
        ///
        /// <paramref name="forUpdate"/> makes the read take the dialect's update lock
        /// (<see cref="ISqlDialect.UpdateLockTableHint"/> / <see cref="ISqlDialect.UpdateLockClause"/>)
        /// so the row cannot be changed by a concurrent transaction between this read and
        /// the write it precedes. ONLY the before-image capture passes true: the
        /// after-image read-back runs after the write inside the same transaction, so the
        /// write itself already holds an exclusive lock on the row and a hint there would
        /// be redundant. Ordinary reads are unaffected.
        /// </summary>
        public static async Task<IReadOnlyDictionary<string, object?>?> LoadRowByKey(
            DbConnection conn,
            DbTransaction? transaction,
            ISqlDialect dialect,
            IDbTable table,
            IReadOnlyCollection<string> columns,
            Dictionary<string, object?> keyData,
            bool forUpdate = false,
            CancellationToken cancellationToken = default)
        {
            if (columns.Count == 0 || keyData.Count == 0)
                return null;

            var sql = BuildSelectRowByKeySql(dialect, table, columns, keyData.Keys, forUpdate);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = transaction;
            AddParameters(cmd, keyData);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = await reader.IsDBNullAsync(i, cancellationToken) ? null : reader.GetValue(i);
                row[reader.GetName(i)] = value;
            }
            return row;
        }

        /// <summary>
        /// Builds the keyed single-row SELECT run by <see cref="LoadRowByKey"/>. With
        /// <paramref name="forUpdate"/> the dialect's update-lock forms are applied: the
        /// table hint sits after the FROM table reference (SQL Server's
        /// <c>WITH (UPDLOCK)</c>) and the locking clause after the WHERE clause
        /// (Postgres/MySQL <c>FOR UPDATE</c>); SQLite emits neither (whole-database write
        /// locking already serializes writers). Without it the statement is a plain SELECT.
        /// </summary>
        public static string BuildSelectRowByKeySql(
            ISqlDialect dialect,
            IDbTable table,
            IReadOnlyCollection<string> columns,
            IEnumerable<string> keyColumns,
            bool forUpdate)
        {
            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var columnList = string.Join(",", columns.Select(dialect.EscapeIdentifier));
            var fromHint = forUpdate ? dialect.UpdateLockTableHint : "";
            var lockClause = forUpdate ? dialect.UpdateLockClause : "";
            return $"SELECT {columnList} FROM {tableRef}{fromHint} WHERE {BuildKeyPredicate(dialect, keyColumns)}{lockClause};";
        }

        /// <summary>
        /// Renders a transformer's <see cref="TableFilter"/> into an AND-prefixed WHERE
        /// suffix and its bound parameters (empty when no transformer contributed a
        /// filter). ANDed so it narrows — never replaces — the primary-key predicate.
        /// </summary>
        public static (string WhereSuffix, IReadOnlyList<SqlParameterInfo> Parameters) RenderAdditionalFilter(
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
