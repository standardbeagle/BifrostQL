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
            Func<DbConnection, DbTransaction, Task> work)
        {
            await using var conn = connFactory.GetConnection();
            DbTransaction? transaction = null;
            try
            {
                await conn.OpenAsync();
                transaction = await conn.BeginTransactionAsync();
                await work(conn, transaction);
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
        }

        public static async ValueTask<object?> ExecuteScalar(DbConnection conn, DbTransaction transaction, string sql, Dictionary<string, object?> data)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = transaction;
            AddParameters(cmd, data);
            return await cmd.ExecuteScalarAsync();
        }

        public static async ValueTask<int> ExecuteNonQuery(DbConnection conn, DbTransaction transaction, string sql,
            Dictionary<string, object?> data, IReadOnlyList<SqlParameterInfo>? extraParameters = null)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = transaction;
            AddParameters(cmd, data);
            AddExtraParameters(cmd, extraParameters);
            return await cmd.ExecuteNonQueryAsync();
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
            Dictionary<string, object?> keyData)
        {
            var definition = StateMachineConfigCollector.FromTable(table);
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
