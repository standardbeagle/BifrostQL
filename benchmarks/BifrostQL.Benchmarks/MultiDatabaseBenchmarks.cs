using System.Data.Common;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Jobs;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Sqlite;
using Microsoft.Data.Sqlite;

namespace BifrostQL.Benchmarks;

/// <summary>
/// Cross-database performance benchmarks comparing SQL Server, PostgreSQL, MySQL, and SQLite.
/// Measures query execution, mutations, schema generation, and filter operations.
/// 
/// Note: SQL Server, PostgreSQL, and MySQL benchmarks require running database instances.
/// SQLite benchmarks run in-memory and are always available.
/// </summary>
[MemoryDiagnoser]
[HideColumns(Column.Error, Column.StdDev)]
[SimpleJob(launchCount: 1, warmupCount: 2, iterationCount: 5)]
public class MultiDatabaseBenchmarks : IDisposable
{
    private MultiDatabaseContext _context = null!;

    [Params(DatabaseProvider.Sqlite)]
    public DatabaseProvider Provider { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _context = new MultiDatabaseContext(Provider);
        _context.Initialize();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _context.Dispose();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _context.ResetData();
    }

    public void Dispose()
    {
        _context?.Dispose();
    }

    #region Simple SELECT Queries

    [Benchmark(Description = "Simple SELECT - Single table, all rows")]
    public int SimpleSelectAll()
    {
        return _context.ExecuteQuery($"SELECT * FROM {_context.Dialect.EscapeIdentifier("Categories")}");
    }

    [Benchmark(Description = "Simple SELECT - Single table, filtered")]
    public int SimpleSelectFiltered()
    {
        var prefix = _context.Dialect.ParameterPrefix;
        var sql = $"SELECT * FROM {_context.Dialect.EscapeIdentifier("Products")} WHERE {_context.Dialect.EscapeIdentifier("Price")} > {prefix}minPrice";
        return _context.ExecuteQuery(sql, new Dictionary<string, object?> { [$"{prefix}minPrice"] = 50.0m });
    }

    [Benchmark(Description = "Simple SELECT - Single table, paginated")]
    public int SimpleSelectPaginated()
    {
        var sql = $"SELECT * FROM {_context.Dialect.EscapeIdentifier("Products")}" + _context.Dialect.Pagination(null, 0, 10);
        return _context.ExecuteQuery(sql);
    }

    [Benchmark(Description = "Simple SELECT - Count aggregate")]
    public int SimpleSelectCount()
    {
        return _context.ExecuteScalar($"SELECT COUNT(*) FROM {_context.Dialect.EscapeIdentifier("Orders")}");
    }

    #endregion

    #region Complex JOIN Queries

    [Benchmark(Description = "Complex JOIN - Two tables")]
    public int JoinTwoTables()
    {
        var sql = $@"
            SELECT p.{_context.Dialect.EscapeIdentifier("Name")}, c.{_context.Dialect.EscapeIdentifier("Name")} 
            FROM {_context.Dialect.EscapeIdentifier("Products")} p
            JOIN {_context.Dialect.EscapeIdentifier("Categories")} c ON p.{_context.Dialect.EscapeIdentifier("CategoryId")} = c.{_context.Dialect.EscapeIdentifier("Id")}";
        return _context.ExecuteQuery(sql);
    }

    [Benchmark(Description = "Complex JOIN - Three tables")]
    public int JoinThreeTables()
    {
        var sql = $@"
            SELECT o.{_context.Dialect.EscapeIdentifier("Id")}, c.{_context.Dialect.EscapeIdentifier("Name")}, oi.{_context.Dialect.EscapeIdentifier("Quantity")}
            FROM {_context.Dialect.EscapeIdentifier("Orders")} o
            JOIN {_context.Dialect.EscapeIdentifier("Customers")} c ON o.{_context.Dialect.EscapeIdentifier("CustomerId")} = c.{_context.Dialect.EscapeIdentifier("Id")}
            JOIN {_context.Dialect.EscapeIdentifier("OrderItems")} oi ON oi.{_context.Dialect.EscapeIdentifier("OrderId")} = o.{_context.Dialect.EscapeIdentifier("Id")}";
        return _context.ExecuteQuery(sql);
    }

    [Benchmark(Description = "Complex JOIN - Four tables with aggregation")]
    public int JoinFourTablesWithAggregation()
    {
        var sql = $@"
            SELECT c.{_context.Dialect.EscapeIdentifier("Name")}, COUNT(DISTINCT o.{_context.Dialect.EscapeIdentifier("Id")}) as OrderCount, SUM(oi.{_context.Dialect.EscapeIdentifier("Quantity")} * oi.{_context.Dialect.EscapeIdentifier("UnitPrice")}) as TotalRevenue
            FROM {_context.Dialect.EscapeIdentifier("Customers")} c
            JOIN {_context.Dialect.EscapeIdentifier("Orders")} o ON o.{_context.Dialect.EscapeIdentifier("CustomerId")} = c.{_context.Dialect.EscapeIdentifier("Id")}
            JOIN {_context.Dialect.EscapeIdentifier("OrderItems")} oi ON oi.{_context.Dialect.EscapeIdentifier("OrderId")} = o.{_context.Dialect.EscapeIdentifier("Id")}
            JOIN {_context.Dialect.EscapeIdentifier("Products")} p ON p.{_context.Dialect.EscapeIdentifier("Id")} = oi.{_context.Dialect.EscapeIdentifier("ProductId")}
            GROUP BY c.{_context.Dialect.EscapeIdentifier("Id")}, c.{_context.Dialect.EscapeIdentifier("Name")}";
        return _context.ExecuteQuery(sql);
    }

    #endregion

    #region INSERT Mutations

    [Benchmark(Description = "INSERT - Single row")]
    public int InsertSingleRow()
    {
        var prefix = _context.Dialect.ParameterPrefix;
        var sql = $@"
            INSERT INTO {_context.Dialect.EscapeIdentifier("Categories")} ({_context.Dialect.EscapeIdentifier("Name")}, {_context.Dialect.EscapeIdentifier("Description")})
            VALUES ({prefix}name, {prefix}desc)";
        return _context.ExecuteNonQuery(sql, new Dictionary<string, object?>
        {
            [$"{prefix}name"] = "New Category",
            [$"{prefix}desc"] = "New Description"
        });
    }

    [Benchmark(Description = "INSERT - Batch 10 rows")]
    public int InsertBatch10()
    {
        return _context.ExecuteBatchInsert(10);
    }

    [Benchmark(Description = "INSERT - Batch 100 rows")]
    public int InsertBatch100()
    {
        return _context.ExecuteBatchInsert(100);
    }

    #endregion

    #region UPDATE Mutations

    [Benchmark(Description = "UPDATE - Single row")]
    public int UpdateSingleRow()
    {
        var prefix = _context.Dialect.ParameterPrefix;
        var sql = $@"
            UPDATE {_context.Dialect.EscapeIdentifier("Products")}
            SET {_context.Dialect.EscapeIdentifier("Stock")} = {prefix}newStock
            WHERE {_context.Dialect.EscapeIdentifier("Id")} = {prefix}id";
        return _context.ExecuteNonQuery(sql, new Dictionary<string, object?>
        {
            [$"{prefix}newStock"] = 999,
            [$"{prefix}id"] = 1
        });
    }

    [Benchmark(Description = "UPDATE - Multiple rows with filter")]
    public int UpdateMultipleRows()
    {
        var prefix = _context.Dialect.ParameterPrefix;
        var sql = $@"
            UPDATE {_context.Dialect.EscapeIdentifier("Products")}
            SET {_context.Dialect.EscapeIdentifier("Price")} = {_context.Dialect.EscapeIdentifier("Price")} * {prefix}multiplier
            WHERE {_context.Dialect.EscapeIdentifier("CategoryId")} = {prefix}categoryId";
        return _context.ExecuteNonQuery(sql, new Dictionary<string, object?>
        {
            [$"{prefix}multiplier"] = 1.1m,
            [$"{prefix}categoryId"] = 1
        });
    }

    #endregion

    #region DELETE Mutations

    [Benchmark(Description = "DELETE - Single row")]
    public int DeleteSingleRow()
    {
        // Insert a row first to delete
        var prefix = _context.Dialect.ParameterPrefix;
        var insertSql = $@"
            INSERT INTO {_context.Dialect.EscapeIdentifier("Categories")} ({_context.Dialect.EscapeIdentifier("Name")}, {_context.Dialect.EscapeIdentifier("Description")})
            VALUES ({prefix}name, {prefix}desc)";
        _context.ExecuteNonQuery(insertSql, new Dictionary<string, object?>
        {
            [$"{prefix}name"] = "Temp Category",
            [$"{prefix}desc"] = "To be deleted"
        });

        var deleteSql = $@"
            DELETE FROM {_context.Dialect.EscapeIdentifier("Categories")}
            WHERE {_context.Dialect.EscapeIdentifier("Name")} = {prefix}name";
        return _context.ExecuteNonQuery(deleteSql, new Dictionary<string, object?> { [$"{prefix}name"] = "Temp Category" });
    }

    #endregion

    #region Filter Operations

    [Benchmark(Description = "Filter - Equality")]
    public int FilterEquality()
    {
        var prefix = _context.Dialect.ParameterPrefix;
        var sql = $@"
            SELECT * FROM {_context.Dialect.EscapeIdentifier("Orders")}
            WHERE {_context.Dialect.EscapeIdentifier("Status")} = {prefix}status";
        return _context.ExecuteQuery(sql, new Dictionary<string, object?> { [$"{prefix}status"] = "Pending" });
    }

    [Benchmark(Description = "Filter - Range (BETWEEN)")]
    public int FilterRange()
    {
        var prefix = _context.Dialect.ParameterPrefix;
        var sql = $@"
            SELECT * FROM {_context.Dialect.EscapeIdentifier("Products")}
            WHERE {_context.Dialect.EscapeIdentifier("Price")} BETWEEN {prefix}min AND {prefix}max";
        return _context.ExecuteQuery(sql, new Dictionary<string, object?>
        {
            [$"{prefix}min"] = 20.0m,
            [$"{prefix}max"] = 100.0m
        });
    }

    [Benchmark(Description = "Filter - LIKE pattern")]
    public int FilterLikePattern()
    {
        var prefix = _context.Dialect.ParameterPrefix;
        var pattern = _context.Dialect.LikePattern($"{prefix}pattern", LikePatternType.Contains);
        var sql = $@"
            SELECT * FROM {_context.Dialect.EscapeIdentifier("Customers")}
            WHERE {_context.Dialect.EscapeIdentifier("Name")} LIKE {pattern}";
        return _context.ExecuteQuery(sql, new Dictionary<string, object?> { [$"{prefix}pattern"] = "John" });
    }

    [Benchmark(Description = "Filter - IN clause")]
    public int FilterInClause()
    {
        var prefix = _context.Dialect.ParameterPrefix;
        var sql = $@"
            SELECT * FROM {_context.Dialect.EscapeIdentifier("Products")}
            WHERE {_context.Dialect.EscapeIdentifier("CategoryId")} IN ({prefix}c1, {prefix}c2, {prefix}c3)";
        return _context.ExecuteQuery(sql, new Dictionary<string, object?>
        {
            [$"{prefix}c1"] = 1,
            [$"{prefix}c2"] = 2,
            [$"{prefix}c3"] = 3
        });
    }

    [Benchmark(Description = "Filter - Complex AND/OR")]
    public int FilterComplex()
    {
        var prefix = _context.Dialect.ParameterPrefix;
        var sql = $@"
            SELECT * FROM {_context.Dialect.EscapeIdentifier("Products")}
            WHERE ({_context.Dialect.EscapeIdentifier("Price")} < {prefix}maxPrice AND {_context.Dialect.EscapeIdentifier("Stock")} > {prefix}minStock)
               OR {_context.Dialect.EscapeIdentifier("CategoryId")} = {prefix}categoryId";
        return _context.ExecuteQuery(sql, new Dictionary<string, object?>
        {
            [$"{prefix}maxPrice"] = 100.0m,
            [$"{prefix}minStock"] = 10,
            [$"{prefix}categoryId"] = 1
        });
    }

    #endregion

    #region Schema Operations

    [Benchmark(Description = "Schema - Get table list")]
    public int SchemaGetTables()
    {
        return _context.GetTableCount();
    }

    [Benchmark(Description = "Schema - Get column metadata")]
    public int SchemaGetColumns()
    {
        return _context.GetColumnCount("Products");
    }

    #endregion
}

/// <summary>
/// Database providers supported by the benchmarks.
/// </summary>
public enum DatabaseProvider
{
    Sqlite,
    SqlServer,
    PostgreSQL,
    MySQL
}

/// <summary>
/// Provides database context and operations for multi-database benchmarks.
/// Uses SQLite in-memory as the default; other providers require external setup.
/// </summary>
public sealed class MultiDatabaseContext : IDisposable
{
    private readonly DatabaseProvider _provider;
    private SqliteConnection? _sqliteConnection;
    private bool _disposed;

    public ISqlDialect Dialect { get; private set; } = null!;
    public IDbConnFactory ConnFactory { get; private set; } = null!;

    public MultiDatabaseContext(DatabaseProvider provider)
    {
        _provider = provider;
    }

    public void Initialize()
    {
        switch (_provider)
        {
            case DatabaseProvider.Sqlite:
                InitializeSqlite();
                break;
            case DatabaseProvider.SqlServer:
            case DatabaseProvider.PostgreSQL:
            case DatabaseProvider.MySQL:
                // These would require external database instances
                // For now, throw to indicate they need setup
                throw new NotSupportedException(
                    $"{_provider} benchmarks require a running database instance. " +
                    "Please configure connection strings via environment variables: " +
                    "BIFROST_BENCH_SQLSERVER, BIFROST_BENCH_POSTGRES, BIFROST_BENCH_MYSQL");
            default:
                throw new ArgumentOutOfRangeException();
        }

        CreateSchema();
        SeedData();
    }

    private void InitializeSqlite()
    {
        var connectionString = "Data Source=:memory:";
        _sqliteConnection = new SqliteConnection(connectionString);
        _sqliteConnection.Open();

        Dialect = SqliteDialect.Instance;
        ConnFactory = new SqliteDbConnFactory(connectionString);
    }

    private void CreateSchema()
    {
        var ddl = GetCreateTablesSql(Dialect);
        using var cmd = GetConnection().CreateCommand();
        cmd.CommandText = ddl;
        cmd.ExecuteNonQuery();
    }

    private void SeedData()
    {
        var conn = GetConnection();
        var prefix = Dialect.ParameterPrefix;

        // 5 categories
        var categories = new[] { "Electronics", "Books", "Clothing", "Home", "Sports" };
        for (int i = 0; i < categories.Length; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                INSERT INTO {Dialect.EscapeIdentifier("Categories")} ({Dialect.EscapeIdentifier("Name")}, {Dialect.EscapeIdentifier("Description")})
                VALUES ({prefix}name, {prefix}desc)";
            AddParam(cmd, $"{prefix}name", categories[i]);
            AddParam(cmd, $"{prefix}desc", $"Description for {categories[i]}");
            cmd.ExecuteNonQuery();
        }

        // 20 products (4 per category)
        var random = new Random(42); // Seeded for reproducibility
        for (int i = 1; i <= 20; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                INSERT INTO {Dialect.EscapeIdentifier("Products")} 
                ({Dialect.EscapeIdentifier("CategoryId")}, {Dialect.EscapeIdentifier("Name")}, {Dialect.EscapeIdentifier("Price")}, {Dialect.EscapeIdentifier("Stock")})
                VALUES ({prefix}catId, {prefix}name, {prefix}price, {prefix}stock)";
            AddParam(cmd, $"{prefix}catId", ((i - 1) % 5) + 1);
            AddParam(cmd, $"{prefix}name", $"Product {i}");
            AddParam(cmd, $"{prefix}price", random.Next(10, 500) + 0.99m);
            AddParam(cmd, $"{prefix}stock", random.Next(0, 200));
            cmd.ExecuteNonQuery();
        }

        // 10 customers
        var cities = new[] { "New York", "Los Angeles", "Chicago", "Houston", "Phoenix" };
        for (int i = 1; i <= 10; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                INSERT INTO {Dialect.EscapeIdentifier("Customers")}
                ({Dialect.EscapeIdentifier("Name")}, {Dialect.EscapeIdentifier("Email")}, {Dialect.EscapeIdentifier("City")})
                VALUES ({prefix}name, {prefix}email, {prefix}city)";
            AddParam(cmd, $"{prefix}name", $"Customer {i}");
            AddParam(cmd, $"{prefix}email", $"customer{i}@example.com");
            AddParam(cmd, $"{prefix}city", cities[i % cities.Length]);
            cmd.ExecuteNonQuery();
        }

        // 30 orders (3 per customer)
        var statuses = new[] { "Pending", "Shipped", "Delivered" };
        var baseDate = new DateTime(2024, 1, 1);
        for (int i = 1; i <= 30; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                INSERT INTO {Dialect.EscapeIdentifier("Orders")}
                ({Dialect.EscapeIdentifier("CustomerId")}, {Dialect.EscapeIdentifier("OrderDate")}, {Dialect.EscapeIdentifier("Total")}, {Dialect.EscapeIdentifier("Status")})
                VALUES ({prefix}custId, {prefix}date, {prefix}total, {prefix}status)";
            AddParam(cmd, $"{prefix}custId", ((i - 1) % 10) + 1);
            AddParam(cmd, $"{prefix}date", baseDate.AddDays(i));
            AddParam(cmd, $"{prefix}total", random.Next(50, 1000) + 0.99m);
            AddParam(cmd, $"{prefix}status", statuses[i % statuses.Length]);
            cmd.ExecuteNonQuery();
        }

        // 60 order items (2 per order)
        for (int i = 1; i <= 60; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                INSERT INTO {Dialect.EscapeIdentifier("OrderItems")}
                ({Dialect.EscapeIdentifier("OrderId")}, {Dialect.EscapeIdentifier("ProductId")}, {Dialect.EscapeIdentifier("Quantity")}, {Dialect.EscapeIdentifier("UnitPrice")})
                VALUES ({prefix}orderId, {prefix}productId, {prefix}qty, {prefix}price)";
            AddParam(cmd, $"{prefix}orderId", ((i - 1) % 30) + 1);
            AddParam(cmd, $"{prefix}productId", ((i - 1) % 20) + 1);
            AddParam(cmd, $"{prefix}qty", random.Next(1, 5));
            AddParam(cmd, $"{prefix}price", random.Next(10, 500) + 0.99m);
            cmd.ExecuteNonQuery();
        }
    }

    public void ResetData()
    {
        using var cmd = GetConnection().CreateCommand();
        cmd.CommandText = $@"
            DELETE FROM {Dialect.EscapeIdentifier("OrderItems")};
            DELETE FROM {Dialect.EscapeIdentifier("Orders")};
            DELETE FROM {Dialect.EscapeIdentifier("Customers")};
            DELETE FROM {Dialect.EscapeIdentifier("Products")};
            DELETE FROM {Dialect.EscapeIdentifier("Categories")};
        ";
        cmd.ExecuteNonQuery();

        // Reset SQLite sequences
        if (_provider == DatabaseProvider.Sqlite)
        {
            cmd.CommandText = @"
                DELETE FROM sqlite_sequence WHERE name='Categories';
                DELETE FROM sqlite_sequence WHERE name='Products';
                DELETE FROM sqlite_sequence WHERE name='Customers';
                DELETE FROM sqlite_sequence WHERE name='Orders';
                DELETE FROM sqlite_sequence WHERE name='OrderItems';
            ";
            cmd.ExecuteNonQuery();
        }

        SeedData();
    }

    public int ExecuteQuery(string sql, Dictionary<string, object?>? parameters = null)
    {
        using var cmd = GetConnection().CreateCommand();
        cmd.CommandText = sql;
        
        if (parameters != null)
        {
            foreach (var param in parameters)
            {
                AddParam(cmd, param.Key, param.Value);
            }
        }

        int count = 0;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            count++;
        }
        return count;
    }

    public int ExecuteScalar(string sql, Dictionary<string, object?>? parameters = null)
    {
        using var cmd = GetConnection().CreateCommand();
        cmd.CommandText = sql;
        
        if (parameters != null)
        {
            foreach (var param in parameters)
            {
                AddParam(cmd, param.Key, param.Value);
            }
        }

        var result = cmd.ExecuteScalar();
        return Convert.ToInt32(result);
    }

    public int ExecuteNonQuery(string sql, Dictionary<string, object?>? parameters = null)
    {
        using var cmd = GetConnection().CreateCommand();
        cmd.CommandText = sql;
        
        if (parameters != null)
        {
            foreach (var param in parameters)
            {
                AddParam(cmd, param.Key, param.Value);
            }
        }

        return cmd.ExecuteNonQuery();
    }

    public int ExecuteBatchInsert(int rowCount)
    {
        var conn = GetConnection();
        var prefix = Dialect.ParameterPrefix;
        
        using var transaction = conn.BeginTransaction();
        int totalInserted = 0;

        for (int i = 0; i < rowCount; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = $@"
                INSERT INTO {Dialect.EscapeIdentifier("Categories")} ({Dialect.EscapeIdentifier("Name")}, {Dialect.EscapeIdentifier("Description")})
                VALUES ({prefix}name, {prefix}desc)";
            AddParam(cmd, $"{prefix}name", $"Batch Category {i}");
            AddParam(cmd, $"{prefix}desc", $"Description {i}");
            totalInserted += cmd.ExecuteNonQuery();
        }

        transaction.Commit();
        return totalInserted;
    }

    public int GetTableCount()
    {
        return _provider switch
        {
            DatabaseProvider.Sqlite => ExecuteScalar(@"
                SELECT COUNT(*) FROM sqlite_master 
                WHERE type='table' AND name NOT LIKE 'sqlite_%'"),
            _ => 5 // Default for other providers
        };
    }

    public int GetColumnCount(string tableName)
    {
        return _provider switch
        {
            DatabaseProvider.Sqlite => ExecuteScalar(
                $"SELECT COUNT(*) FROM pragma_table_info({Dialect.EscapeIdentifier(tableName)})"),
            _ => 5 // Default
        };
    }

    private DbConnection GetConnection()
    {
        return _provider switch
        {
            DatabaseProvider.Sqlite => _sqliteConnection!,
            _ => throw new NotSupportedException($"Provider {_provider} not implemented")
        };
    }

    private static void AddParam(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    private static string GetCreateTablesSql(ISqlDialect dialect)
    {
        if (dialect is SqliteDialect)
        {
            return @"
                CREATE TABLE IF NOT EXISTS Categories (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    Description TEXT
                );

                CREATE TABLE IF NOT EXISTS Products (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    CategoryId INTEGER NOT NULL,
                    Name TEXT NOT NULL,
                    Price REAL NOT NULL,
                    Stock INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY (CategoryId) REFERENCES Categories(Id)
                );

                CREATE TABLE IF NOT EXISTS Customers (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    Email TEXT NOT NULL,
                    City TEXT
                );

                CREATE TABLE IF NOT EXISTS Orders (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    CustomerId INTEGER NOT NULL,
                    OrderDate TEXT NOT NULL,
                    Total REAL NOT NULL,
                    Status TEXT NOT NULL DEFAULT 'Pending',
                    FOREIGN KEY (CustomerId) REFERENCES Customers(Id)
                );

                CREATE TABLE IF NOT EXISTS OrderItems (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    OrderId INTEGER NOT NULL,
                    ProductId INTEGER NOT NULL,
                    Quantity INTEGER NOT NULL,
                    UnitPrice REAL NOT NULL,
                    FOREIGN KEY (OrderId) REFERENCES Orders(Id),
                    FOREIGN KEY (ProductId) REFERENCES Products(Id)
                );

                CREATE INDEX idx_products_category ON Products(CategoryId);
                CREATE INDEX idx_products_price ON Products(Price);
                CREATE INDEX idx_orders_customer ON Orders(CustomerId);
                CREATE INDEX idx_orders_status ON Orders(Status);
                CREATE INDEX idx_orderitems_order ON OrderItems(OrderId);
                CREATE INDEX idx_customers_city ON Customers(City);
            ";
        }

        throw new NotSupportedException($"DDL not implemented for dialect: {dialect.GetType().Name}");
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _sqliteConnection?.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Dialect-specific SQL generation benchmarks.
/// Measures the performance of SQL dialect operations without database execution.
/// </summary>
[MemoryDiagnoser]
[HideColumns(Column.Error, Column.StdDev)]
public class SqlDialectBenchmarks
{
    [ParamsAllValues]
    public DatabaseProvider Provider { get; set; }

    private ISqlDialect GetDialect() => Provider switch
    {
        DatabaseProvider.Sqlite => SqliteDialect.Instance,
        DatabaseProvider.SqlServer => SqlServerDialect.Instance,
        DatabaseProvider.PostgreSQL => BifrostQL.Ngsql.PostgresDialect.Instance,
        DatabaseProvider.MySQL => BifrostQL.MySql.MySqlDialect.Instance,
        _ => throw new ArgumentOutOfRangeException()
    };

    [Benchmark(Description = "Dialect - Escape identifier")]
    public string EscapeIdentifier()
    {
        var dialect = GetDialect();
        var result = "";
        for (int i = 0; i < 1000; i++)
        {
            result = dialect.EscapeIdentifier("TestColumnName");
        }
        return result;
    }

    [Benchmark(Description = "Dialect - Table reference")]
    public string TableReference()
    {
        var dialect = GetDialect();
        var result = "";
        for (int i = 0; i < 1000; i++)
        {
            result = dialect.TableReference("dbo", "Users");
        }
        return result;
    }

    [Benchmark(Description = "Dialect - Pagination")]
    public string Pagination()
    {
        var dialect = GetDialect();
        var result = "";
        var sortColumns = new[] { "Name", "Id" };
        for (int i = 0; i < 1000; i++)
        {
            result = dialect.Pagination(sortColumns, 20, 10);
        }
        return result;
    }

    [Benchmark(Description = "Dialect - LIKE pattern")]
    public string LikePattern()
    {
        var dialect = GetDialect();
        var result = "";
        for (int i = 0; i < 1000; i++)
        {
            result = dialect.LikePattern("@param", LikePatternType.Contains);
        }
        return result;
    }

    [Benchmark(Description = "Dialect - Get operator")]
    public string GetOperator()
    {
        var dialect = GetDialect();
        var result = "";
        var operators = new[] { "_eq", "_neq", "_lt", "_gt", "_contains", "_in" };
        for (int i = 0; i < 1000; i++)
        {
            result = dialect.GetOperator(operators[i % operators.Length]);
        }
        return result;
    }
}

/// <summary>
/// Throughput-focused benchmarks for multi-database operations.
/// </summary>
[MemoryDiagnoser]
[HideColumns(Column.Error, Column.StdDev)]
[SimpleJob(launchCount: 1, warmupCount: 2, iterationCount: 3)]
public class MultiDatabaseThroughputBenchmarks : IDisposable
{
    private MultiDatabaseContext _context = null!;

    [Params(DatabaseProvider.Sqlite)]
    public DatabaseProvider Provider { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _context = new MultiDatabaseContext(Provider);
        _context.Initialize();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _context.Dispose();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _context.ResetData();
    }

    public void Dispose()
    {
        _context?.Dispose();
    }

    [Benchmark(OperationsPerInvoke = 100, Description = "Throughput - Simple SELECT queries")]
    public int SimpleSelectThroughput()
    {
        var sql = $"SELECT * FROM {_context.Dialect.EscapeIdentifier("Categories")}";
        int total = 0;
        for (int i = 0; i < 100; i++)
        {
            total += _context.ExecuteQuery(sql);
        }
        return total;
    }

    [Benchmark(OperationsPerInvoke = 100, Description = "Throughput - Filtered SELECT queries")]
    public int FilteredSelectThroughput()
    {
        var prefix = _context.Dialect.ParameterPrefix;
        var sql = $"SELECT * FROM {_context.Dialect.EscapeIdentifier("Products")} WHERE {_context.Dialect.EscapeIdentifier("Price")} > {prefix}minPrice";
        int total = 0;
        for (int i = 0; i < 100; i++)
        {
            total += _context.ExecuteQuery(sql, new Dictionary<string, object?> { [$"{prefix}minPrice"] = 50.0m });
        }
        return total;
    }

    [Benchmark(OperationsPerInvoke = 50, Description = "Throughput - JOIN queries")]
    public int JoinThroughput()
    {
        var sql = $@"
            SELECT p.{_context.Dialect.EscapeIdentifier("Name")}, c.{_context.Dialect.EscapeIdentifier("Name")} 
            FROM {_context.Dialect.EscapeIdentifier("Products")} p
            JOIN {_context.Dialect.EscapeIdentifier("Categories")} c ON p.{_context.Dialect.EscapeIdentifier("CategoryId")} = c.{_context.Dialect.EscapeIdentifier("Id")}";
        int total = 0;
        for (int i = 0; i < 50; i++)
        {
            total += _context.ExecuteQuery(sql);
        }
        return total;
    }
}
