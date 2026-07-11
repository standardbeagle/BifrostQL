using System.Data.Common;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
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
        /// Builds the full <c>DELETE FROM tableRef WHERE …suffix;</c> statement shared by
        /// the single-row and batch hard-delete paths. <paramref name="whereSuffix"/>
        /// carries a transformer's ANDed AdditionalFilter (empty when none).
        /// </summary>
        public static string BuildDeleteSql(ISqlDialect dialect, string tableRef,
            IEnumerable<string> whereColumns, string whereSuffix)
            => $"DELETE FROM {tableRef} WHERE {BuildKeyPredicate(dialect, whereColumns)}{whereSuffix};";

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
        /// </summary>
        public static async Task<IReadOnlyDictionary<string, object?>?> LoadCurrentStateMachineRow(
            DbConnection conn,
            DbTransaction transaction,
            ISqlDialect dialect,
            IDbTable table,
            Dictionary<string, object?> keyData,
            CancellationToken cancellationToken = default)
        {
            var definition = StateMachineConfigCollector.FromTable(table);
            if (definition is null || keyData.Count == 0)
                return null;

            var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
            var stateColumn = dialect.EscapeIdentifier(definition.StateColumn);
            var whereClause = BuildKeyPredicate(dialect, keyData.Keys);
            var sql = $"SELECT {stateColumn} FROM {tableRef} WHERE {whereClause};";

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = transaction;
            AddParameters(cmd, keyData);
            var currentState = await cmd.ExecuteScalarAsync(cancellationToken);
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [definition.StateColumn] = currentState == DBNull.Value ? null : currentState,
            };
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
