using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Integration.Test.Infrastructure;

/// <summary>
/// Executes GqlObjectQuery trees against a real database via IDbConnFactory.
/// Replicates the core logic of SqlExecutionManager.LoadDataParameterized.
/// </summary>
public static class QueryExecutor
{
    /// <summary>
    /// Generates SQL from a GqlObjectQuery, executes all statements, and returns named result sets.
    /// </summary>
    public static IDictionary<string, (IDictionary<string, int> index, IList<object?[]> data)> Execute(
        GqlObjectQuery query,
        IDbModel dbModel,
        IDbConnFactory connFactory)
    {
        var dialect = connFactory.Dialect;
        var parameters = new SqlParameterCollection();
        var sqlList = new Dictionary<string, ParameterizedSql>();
        query.AddSqlParameterized(dbModel, dialect, sqlList, parameters);

        var resultNames = sqlList.Keys.ToArray();
        var sql = string.Join(";\n", sqlList.Values.Select(p => p.Sql));

        using var conn = connFactory.GetConnection();
        conn.Open();

        var results = new Dictionary<string, (IDictionary<string, int> index, IList<object?[]> data)>();

        // SQLite doesn't support multiple result sets in a single command,
        // so we execute each statement individually.
        if (dialect is BifrostQL.Sqlite.SqliteDialect)
        {
            for (var i = 0; i < resultNames.Length; i++)
            {
                var pSql = sqlList[resultNames[i]];
                var cmd = conn.CreateCommand();
                cmd.CommandText = pSql.Sql;
                AddParameters(cmd, parameters.Parameters);

                using var reader = cmd.ExecuteReader();
                results[resultNames[i]] = ReadResultSet(reader);
            }
            return results;
        }

        // For SQL Server, PostgreSQL, MySQL: execute as batch with multiple result sets
        var batchCmd = conn.CreateCommand();
        batchCmd.CommandText = sql;
        AddParameters(batchCmd, parameters.Parameters);

        using var batchReader = batchCmd.ExecuteReader();
        var resultIndex = 0;
        do
        {
            results[resultNames[resultIndex++]] = ReadResultSet(batchReader);
        } while (batchReader.NextResult());

        return results;
    }

    /// <summary>
    /// Executes a simple SQL query and returns rows as dictionaries.
    /// Useful for verification queries.
    /// </summary>
    public static List<Dictionary<string, object?>> ExecuteRaw(
        IDbConnFactory connFactory,
        string sql,
        params (string name, object? value)[] parameters)
    {
        using var conn = connFactory.GetConnection();
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        using var reader = cmd.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }
        return rows;
    }

    private static (IDictionary<string, int> index, IList<object?[]> data) ReadResultSet(DbDataReader reader)
    {
        var index = Enumerable.Range(0, reader.FieldCount)
            .ToDictionary(i => reader.GetName(i), i => i, StringComparer.OrdinalIgnoreCase);
        var data = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            reader.GetValues(row!);
            // Normalize DBNull to null
            for (var i = 0; i < row.Length; i++)
            {
                if (row[i] is DBNull) row[i] = null;
            }
            data.Add(row);
        }
        return (index, data);
    }

    private static void AddParameters(DbCommand cmd, IReadOnlyList<SqlParameterInfo> parameters)
    {
        foreach (var param in parameters)
        {
            var dbParam = cmd.CreateParameter();
            dbParam.ParameterName = param.Name;
            dbParam.Value = param.Value ?? DBNull.Value;
            if (param.DbType != null)
            {
                dbParam.DbType = (System.Data.DbType)Enum.Parse(typeof(System.Data.DbType), param.DbType);
            }
            cmd.Parameters.Add(dbParam);
        }
    }
}
