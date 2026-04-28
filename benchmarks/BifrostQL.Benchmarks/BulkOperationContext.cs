using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Sqlite;
using Microsoft.Data.Sqlite;

namespace BifrostQL.Benchmarks;

/// <summary>
/// Provides database context and setup/teardown for bulk operation benchmarks.
/// Uses SQLite in-memory database for consistent, fast test execution.
/// </summary>
public sealed class BulkOperationContext : IDisposable
{
    private SqliteConnection? _connection;
    private bool _disposed;

    /// <summary>
    /// The database connection factory for creating new connections.
    /// </summary>
    public IDbConnFactory ConnFactory { get; private set; } = null!;

    /// <summary>
    /// The SQL dialect for generating SQL statements.
    /// </summary>
    public ISqlDialect Dialect => SqliteDialect.Instance;

    /// <summary>
    /// Gets a connection to the in-memory database.
    /// Note: For in-memory SQLite, the connection must remain open.
    /// </summary>
    public DbConnection Connection => _connection ?? throw new InvalidOperationException("Context not initialized");

    /// <summary>
    /// Initializes the database context with schema and test data.
    /// </summary>
    public void Initialize()
    {
        // SQLite in-memory database requires "Mode=Memory;Cache=Shared" for multiple connections
        // But for benchmarks, we'll use a single connection that's kept open
        var connectionString = "Data Source=:memory:";
        _connection = new SqliteConnection(connectionString);
        _connection.Open();

        ConnFactory = new SqliteDbConnFactory(connectionString);

        CreateSchema();
    }

    /// <summary>
    /// Creates the test database schema.
    /// </summary>
    private void CreateSchema()
    {
        using var cmd = _connection!.CreateCommand();
        
        // Create test table for bulk operations
        cmd.CommandText = @"
            CREATE TABLE benchmark_users (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                email TEXT NOT NULL,
                age INTEGER,
                balance REAL,
                is_active INTEGER DEFAULT 1,
                created_at TEXT DEFAULT CURRENT_TIMESTAMP,
                department TEXT
            );
            
            CREATE INDEX idx_users_email ON benchmark_users(email);
            CREATE INDEX idx_users_department ON benchmark_users(department);
        ";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Clears all data from the test table.
    /// </summary>
    public void ClearTable()
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "DELETE FROM benchmark_users; DELETE FROM sqlite_sequence WHERE name='benchmark_users';";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Gets the current row count in the test table.
    /// </summary>
    public int GetRowCount()
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM benchmark_users;";
        var result = cmd.ExecuteScalar();
        return Convert.ToInt32(result);
    }

    /// <summary>
    /// Generates test user data for bulk insert operations.
    /// </summary>
    public List<Dictionary<string, object?>> GenerateInsertData(int count, int startId = 1)
    {
        var data = new List<Dictionary<string, object?>>(count);
        var departments = new[] { "Engineering", "Sales", "Marketing", "HR", "Finance", null };
        
        for (int i = 0; i < count; i++)
        {
            var id = startId + i;
            data.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = $"User {id}",
                ["email"] = $"user{id}@example.com",
                ["age"] = 20 + (i % 50),
                ["balance"] = 1000.0 + (i * 17.5),
                ["is_active"] = i % 3 != 0 ? 1 : 0,
                ["department"] = departments[i % departments.Length],
            });
        }
        
        return data;
    }

    /// <summary>
    /// Performs raw ADO.NET bulk insert using a transaction and prepared command.
    /// This is the baseline for comparison.
    /// </summary>
    public int RawAdoNetBulkInsert(List<Dictionary<string, object?>> data)
    {
        using var transaction = _connection!.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        
        cmd.CommandText = @"
            INSERT INTO benchmark_users (name, email, age, balance, is_active, department)
            VALUES (@name, @email, @age, @balance, @is_active, @department);
        ";
        
        var nameParam = cmd.CreateParameter();
        nameParam.ParameterName = "@name";
        cmd.Parameters.Add(nameParam);
        
        var emailParam = cmd.CreateParameter();
        emailParam.ParameterName = "@email";
        cmd.Parameters.Add(emailParam);
        
        var ageParam = cmd.CreateParameter();
        ageParam.ParameterName = "@age";
        cmd.Parameters.Add(ageParam);
        
        var balanceParam = cmd.CreateParameter();
        balanceParam.ParameterName = "@balance";
        cmd.Parameters.Add(balanceParam);
        
        var isActiveParam = cmd.CreateParameter();
        isActiveParam.ParameterName = "@is_active";
        cmd.Parameters.Add(isActiveParam);
        
        var deptParam = cmd.CreateParameter();
        deptParam.ParameterName = "@department";
        cmd.Parameters.Add(deptParam);
        
        cmd.Prepare();
        
        int affected = 0;
        foreach (var row in data)
        {
            nameParam.Value = row["name"] ?? DBNull.Value;
            emailParam.Value = row["email"] ?? DBNull.Value;
            ageParam.Value = row["age"] ?? DBNull.Value;
            balanceParam.Value = row["balance"] ?? DBNull.Value;
            isActiveParam.Value = row["is_active"] ?? DBNull.Value;
            deptParam.Value = row["department"] ?? DBNull.Value;
            
            affected += cmd.ExecuteNonQuery();
        }
        
        transaction.Commit();
        return affected;
    }

    /// <summary>
    /// Performs raw ADO.NET bulk update using a transaction and prepared command.
    /// </summary>
    public int RawAdoNetBulkUpdate(List<Dictionary<string, object?>> data)
    {
        using var transaction = _connection!.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        
        cmd.CommandText = @"
            UPDATE benchmark_users 
            SET balance = @balance, is_active = @is_active
            WHERE id = @id;
        ";
        
        var idParam = cmd.CreateParameter();
        idParam.ParameterName = "@id";
        cmd.Parameters.Add(idParam);
        
        var balanceParam = cmd.CreateParameter();
        balanceParam.ParameterName = "@balance";
        cmd.Parameters.Add(balanceParam);
        
        var isActiveParam = cmd.CreateParameter();
        isActiveParam.ParameterName = "@is_active";
        cmd.Parameters.Add(isActiveParam);
        
        cmd.Prepare();
        
        int affected = 0;
        foreach (var row in data)
        {
            idParam.Value = row["id"] ?? DBNull.Value;
            balanceParam.Value = row["balance"] ?? DBNull.Value;
            isActiveParam.Value = row["is_active"] ?? DBNull.Value;
            
            affected += cmd.ExecuteNonQuery();
        }
        
        transaction.Commit();
        return affected;
    }

    /// <summary>
    /// Performs raw ADO.NET bulk delete using a transaction and prepared command.
    /// </summary>
    public int RawAdoNetBulkDelete(List<int> ids)
    {
        using var transaction = _connection!.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        
        cmd.CommandText = "DELETE FROM benchmark_users WHERE id = @id;";
        
        var idParam = cmd.CreateParameter();
        idParam.ParameterName = "@id";
        cmd.Parameters.Add(idParam);
        
        cmd.Prepare();
        
        int affected = 0;
        foreach (var id in ids)
        {
            idParam.Value = id;
            affected += cmd.ExecuteNonQuery();
        }
        
        transaction.Commit();
        return affected;
    }

    /// <summary>
    /// Performs bulk insert using individual INSERT statements (simulating BifrostQL approach).
    /// </summary>
    public int IndividualInsertBulk(List<Dictionary<string, object?>> data)
    {
        using var transaction = _connection!.BeginTransaction();
        
        int affected = 0;
        foreach (var row in data)
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
            
            var columns = string.Join(", ", row.Keys.Select(k => $"\"{k}\""));
            var values = string.Join(", ", row.Keys.Select(k => $"@{k}"));
            
            cmd.CommandText = $"INSERT INTO benchmark_users ({columns}) VALUES ({values});";
            
            foreach (var kv in row)
            {
                var param = cmd.CreateParameter();
                param.ParameterName = $"@{kv.Key}";
                param.Value = kv.Value ?? DBNull.Value;
                cmd.Parameters.Add(param);
            }
            
            affected += cmd.ExecuteNonQuery();
        }
        
        transaction.Commit();
        return affected;
    }

    /// <summary>
    /// Performs bulk insert using SQLite's multi-row VALUES syntax.
    /// This is more efficient for large batches.
    /// </summary>
    public int MultiRowInsertBulk(List<Dictionary<string, object?>> data)
    {
        if (data.Count == 0) return 0;
        
        using var transaction = _connection!.BeginTransaction();
        
        // Build multi-row INSERT: INSERT INTO table (cols) VALUES (row1), (row2), ...
        var columns = data[0].Keys.ToList();
        var columnList = string.Join(", ", columns.Select(c => $"\"{c}\""));
        
        var valueClauses = new List<string>();
        var allParams = new List<SqliteParameter>();
        int paramIndex = 0;
        
        foreach (var row in data)
        {
            var rowParams = new List<string>();
            foreach (var col in columns)
            {
                var paramName = $"@p{paramIndex++}";
                rowParams.Add(paramName);
                allParams.Add(new SqliteParameter(paramName, row[col] ?? DBNull.Value));
            }
            valueClauses.Add($"({string.Join(", ", rowParams)})");
        }
        
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"INSERT INTO benchmark_users ({columnList}) VALUES {string.Join(", ", valueClauses)};";
        cmd.Parameters.AddRange(allParams.ToArray());
        
        int affected = cmd.ExecuteNonQuery();
        transaction.Commit();
        return affected;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _connection?.Dispose();
            _disposed = true;
        }
    }
}
