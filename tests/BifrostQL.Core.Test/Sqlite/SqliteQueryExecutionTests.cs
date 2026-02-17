using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Integration tests that verify SQLite dialect SQL generation produces
/// executable SQL against real in-memory SQLite databases. Tests pagination,
/// filtering, sorting, inserts, updates, and deletes using SqliteDialect output.
/// </summary>
public sealed class SqliteQueryExecutionTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private readonly SqliteDialect _dialect = SqliteDialect.Instance;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        await using var cmd = new SqliteCommand(@"
            CREATE TABLE Products (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Price REAL NOT NULL,
                Stock INTEGER NOT NULL DEFAULT 0,
                Category TEXT NOT NULL
            )", _connection);
        await cmd.ExecuteNonQueryAsync();

        await SeedProductsAsync();
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    private async Task SeedProductsAsync()
    {
        var products = new (string name, double price, int stock, string category)[]
        {
            ("Alpha", 10.00, 100, "Electronics"),
            ("Beta", 25.50, 50, "Books"),
            ("Gamma", 5.99, 200, "Electronics"),
            ("Delta", 99.99, 10, "Sports"),
            ("Epsilon", 15.00, 75, "Books"),
            ("Zeta", 42.00, 30, "Electronics"),
            ("Eta", 8.50, 150, "Sports"),
            ("Theta", 120.00, 5, "Books"),
            ("Iota", 33.33, 60, "Electronics"),
            ("Kappa", 7.25, 180, "Sports"),
        };

        foreach (var (name, price, stock, category) in products)
        {
            await using var cmd = new SqliteCommand(
                $"INSERT INTO {_dialect.EscapeIdentifier("Products")} " +
                $"({_dialect.EscapeIdentifier("Name")}, {_dialect.EscapeIdentifier("Price")}, " +
                $"{_dialect.EscapeIdentifier("Stock")}, {_dialect.EscapeIdentifier("Category")}) " +
                $"VALUES (@name, @price, @stock, @category)",
                _connection);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@price", price);
            cmd.Parameters.AddWithValue("@stock", stock);
            cmd.Parameters.AddWithValue("@category", category);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    #region Pagination - LIMIT/OFFSET

    [Fact]
    public async Task Pagination_LimitOnly_ReturnsCorrectCount()
    {
        var pagination = _dialect.Pagination(null, null, 3);
        var sql = $"SELECT * FROM {_dialect.EscapeIdentifier("Products")}{pagination}";

        var count = await CountRowsAsync(sql);
        count.Should().Be(3);
    }

    [Fact]
    public async Task Pagination_LimitAndOffset_SkipsRows()
    {
        var sortCols = new[] { $"{_dialect.EscapeIdentifier("Id")} ASC" };
        var pagination = _dialect.Pagination(sortCols, 3, 3);
        var sql = $"SELECT {_dialect.EscapeIdentifier("Id")} FROM {_dialect.EscapeIdentifier("Products")}{pagination}";

        var ids = await ReadColumnAsync<long>(sql, "Id");
        ids.Should().Equal(4, 5, 6);
    }

    [Fact]
    public async Task Pagination_NullLimit_DefaultsTo100()
    {
        var pagination = _dialect.Pagination(null, null, null);
        var sql = $"SELECT * FROM {_dialect.EscapeIdentifier("Products")}{pagination}";

        var count = await CountRowsAsync(sql);
        count.Should().Be(10, "default limit of 100 should return all 10 rows");
    }

    [Fact]
    public async Task Pagination_LimitMinusOne_ReturnsAllRows()
    {
        var pagination = _dialect.Pagination(null, null, -1);
        var sql = $"SELECT * FROM {_dialect.EscapeIdentifier("Products")}{pagination}";

        var count = await CountRowsAsync(sql);
        count.Should().Be(10);
    }

    [Fact]
    public async Task Pagination_WithSort_OrdersCorrectly()
    {
        var sortCols = new[] { $"{_dialect.EscapeIdentifier("Price")} DESC" };
        var pagination = _dialect.Pagination(sortCols, 0, 3);
        var sql = $"SELECT {_dialect.EscapeIdentifier("Name")} FROM {_dialect.EscapeIdentifier("Products")}{pagination}";

        var names = await ReadColumnAsync<string>(sql, "Name");
        names.Should().Equal("Theta", "Delta", "Zeta");
    }

    [Fact]
    public async Task Pagination_MultipleSort_OrdersCorrectly()
    {
        var sortCols = new[]
        {
            $"{_dialect.EscapeIdentifier("Category")} ASC",
            $"{_dialect.EscapeIdentifier("Price")} DESC"
        };
        var pagination = _dialect.Pagination(sortCols, 0, 3);
        var sql = $"SELECT {_dialect.EscapeIdentifier("Name")}, {_dialect.EscapeIdentifier("Category")} " +
                  $"FROM {_dialect.EscapeIdentifier("Products")}{pagination}";

        var names = await ReadColumnAsync<string>(sql, "Name");
        // Books first (sorted by price desc): Theta (120), Beta (25.50), Epsilon (15)
        names.Should().Equal("Theta", "Beta", "Epsilon");
    }

    [Fact]
    public async Task Pagination_OffsetBeyondData_ReturnsEmpty()
    {
        var pagination = _dialect.Pagination(null, 100, 10);
        var sql = $"SELECT * FROM {_dialect.EscapeIdentifier("Products")}{pagination}";

        var count = await CountRowsAsync(sql);
        count.Should().Be(0);
    }

    #endregion

    #region Filtering with Dialect Operators

    [Fact]
    public async Task Filter_Equals_ReturnsMatchingRows()
    {
        var op = _dialect.GetOperator("_eq");
        var sql = $"SELECT * FROM {_dialect.EscapeIdentifier("Products")} " +
                  $"WHERE {_dialect.EscapeIdentifier("Category")} {op} @cat";

        await using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@cat", "Electronics");
        var count = await CountRowsFromReaderAsync(cmd);
        count.Should().Be(4);
    }

    [Fact]
    public async Task Filter_NotEquals_ExcludesMatchingRows()
    {
        var op = _dialect.GetOperator("_neq");
        var sql = $"SELECT * FROM {_dialect.EscapeIdentifier("Products")} " +
                  $"WHERE {_dialect.EscapeIdentifier("Category")} {op} @cat";

        await using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@cat", "Electronics");
        var count = await CountRowsFromReaderAsync(cmd);
        count.Should().Be(6);
    }

    [Fact]
    public async Task Filter_GreaterThan_ReturnsCorrectRows()
    {
        var op = _dialect.GetOperator("_gt");
        var sql = $"SELECT * FROM {_dialect.EscapeIdentifier("Products")} " +
                  $"WHERE {_dialect.EscapeIdentifier("Price")} {op} @price";

        await using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@price", 50.0);
        var count = await CountRowsFromReaderAsync(cmd);
        count.Should().Be(2); // Delta (99.99), Theta (120.00)
    }

    [Fact]
    public async Task Filter_LessThanOrEqual_ReturnsCorrectRows()
    {
        var op = _dialect.GetOperator("_lte");
        var sql = $"SELECT * FROM {_dialect.EscapeIdentifier("Products")} " +
                  $"WHERE {_dialect.EscapeIdentifier("Price")} {op} @price";

        await using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@price", 10.0);
        var count = await CountRowsFromReaderAsync(cmd);
        count.Should().Be(4); // Alpha (10), Gamma (5.99), Eta (8.50), Kappa (7.25)
    }

    [Fact]
    public async Task Filter_Like_ContainsPattern()
    {
        var op = _dialect.GetOperator("_contains");
        var pattern = _dialect.LikePattern("@search", Core.QueryModel.LikePatternType.Contains);
        var sql = $"SELECT * FROM {_dialect.EscapeIdentifier("Products")} " +
                  $"WHERE {_dialect.EscapeIdentifier("Name")} {op} {pattern}";

        await using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@search", "eta");
        var count = await CountRowsFromReaderAsync(cmd);
        count.Should().BeGreaterThan(0); // Beta, Zeta, Eta, Theta
    }

    [Fact]
    public async Task Filter_Like_StartsWithPattern()
    {
        var op = _dialect.GetOperator("_starts_with");
        var pattern = _dialect.LikePattern("@search", Core.QueryModel.LikePatternType.StartsWith);
        var sql = $"SELECT * FROM {_dialect.EscapeIdentifier("Products")} " +
                  $"WHERE {_dialect.EscapeIdentifier("Name")} {op} {pattern}";

        await using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@search", "Ep");
        var count = await CountRowsFromReaderAsync(cmd);
        count.Should().Be(1); // Epsilon
    }

    [Fact]
    public async Task Filter_Like_EndsWithPattern()
    {
        var op = _dialect.GetOperator("_ends_with");
        var pattern = _dialect.LikePattern("@search", Core.QueryModel.LikePatternType.EndsWith);
        var sql = $"SELECT * FROM {_dialect.EscapeIdentifier("Products")} " +
                  $"WHERE {_dialect.EscapeIdentifier("Name")} {op} {pattern}";

        await using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@search", "ta");
        var count = await CountRowsFromReaderAsync(cmd);
        count.Should().BeGreaterThan(0); // Beta, Zeta, Eta, Iota
    }

    [Fact]
    public async Task Filter_In_ReturnsMatchingRows()
    {
        var sql = $"SELECT * FROM {_dialect.EscapeIdentifier("Products")} " +
                  $"WHERE {_dialect.EscapeIdentifier("Category")} IN (@c1, @c2)";

        await using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@c1", "Books");
        cmd.Parameters.AddWithValue("@c2", "Sports");
        var count = await CountRowsFromReaderAsync(cmd);
        count.Should().Be(6); // 3 Books + 3 Sports
    }

    [Fact]
    public async Task Filter_Between_ReturnsRowsInRange()
    {
        var sql = $"SELECT * FROM {_dialect.EscapeIdentifier("Products")} " +
                  $"WHERE {_dialect.EscapeIdentifier("Price")} BETWEEN @lo AND @hi";

        await using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@lo", 10.0);
        cmd.Parameters.AddWithValue("@hi", 30.0);
        var count = await CountRowsFromReaderAsync(cmd);
        count.Should().Be(3); // Alpha (10), Beta (25.50), Epsilon (15)
    }

    #endregion

    #region Insert with AUTOINCREMENT

    [Fact]
    public async Task Insert_ReturnsLastInsertRowid()
    {
        var table = _dialect.TableReference(null, "Products");
        var insertSql = $"INSERT INTO {table} " +
                        $"({_dialect.EscapeIdentifier("Name")}, " +
                        $"{_dialect.EscapeIdentifier("Price")}, " +
                        $"{_dialect.EscapeIdentifier("Stock")}, " +
                        $"{_dialect.EscapeIdentifier("Category")}) " +
                        $"VALUES (@name, @price, @stock, @cat); " +
                        $"SELECT {_dialect.LastInsertedIdentity};";

        await using var cmd = new SqliteCommand(insertSql, _connection);
        cmd.Parameters.AddWithValue("@name", "Lambda");
        cmd.Parameters.AddWithValue("@price", 55.00);
        cmd.Parameters.AddWithValue("@stock", 20);
        cmd.Parameters.AddWithValue("@cat", "Electronics");

        var id = await cmd.ExecuteScalarAsync();
        ((long)id!).Should().Be(11, "next AUTOINCREMENT value after 10 seeded rows");
    }

    [Fact]
    public async Task Insert_AutoIncrementSequenceIncreases()
    {
        var table = _dialect.TableReference(null, "Products");
        var insertSql = $"INSERT INTO {table} " +
                        $"({_dialect.EscapeIdentifier("Name")}, " +
                        $"{_dialect.EscapeIdentifier("Price")}, " +
                        $"{_dialect.EscapeIdentifier("Stock")}, " +
                        $"{_dialect.EscapeIdentifier("Category")}) " +
                        $"VALUES (@name, @price, @stock, @cat); " +
                        $"SELECT {_dialect.LastInsertedIdentity};";

        long firstId;
        {
            await using var cmd = new SqliteCommand(insertSql, _connection);
            cmd.Parameters.AddWithValue("@name", "First");
            cmd.Parameters.AddWithValue("@price", 1.0);
            cmd.Parameters.AddWithValue("@stock", 1);
            cmd.Parameters.AddWithValue("@cat", "Test");
            firstId = (long)(await cmd.ExecuteScalarAsync())!;
        }

        long secondId;
        {
            await using var cmd = new SqliteCommand(insertSql, _connection);
            cmd.Parameters.AddWithValue("@name", "Second");
            cmd.Parameters.AddWithValue("@price", 2.0);
            cmd.Parameters.AddWithValue("@stock", 2);
            cmd.Parameters.AddWithValue("@cat", "Test");
            secondId = (long)(await cmd.ExecuteScalarAsync())!;
        }

        secondId.Should().Be(firstId + 1);
    }

    [Fact]
    public async Task Insert_InsertedDataIsQueryable()
    {
        var table = _dialect.TableReference(null, "Products");
        var insertSql = $"INSERT INTO {table} " +
                        $"({_dialect.EscapeIdentifier("Name")}, " +
                        $"{_dialect.EscapeIdentifier("Price")}, " +
                        $"{_dialect.EscapeIdentifier("Stock")}, " +
                        $"{_dialect.EscapeIdentifier("Category")}) " +
                        $"VALUES (@name, @price, @stock, @cat)";

        await using (var cmd = new SqliteCommand(insertSql, _connection))
        {
            cmd.Parameters.AddWithValue("@name", "Queryable");
            cmd.Parameters.AddWithValue("@price", 77.77);
            cmd.Parameters.AddWithValue("@stock", 42);
            cmd.Parameters.AddWithValue("@cat", "Test");
            await cmd.ExecuteNonQueryAsync();
        }

        var selectSql = $"SELECT {_dialect.EscapeIdentifier("Price")} FROM {table} " +
                        $"WHERE {_dialect.EscapeIdentifier("Name")} = @name";
        await using var selectCmd = new SqliteCommand(selectSql, _connection);
        selectCmd.Parameters.AddWithValue("@name", "Queryable");
        var price = (double)(await selectCmd.ExecuteScalarAsync())!;
        price.Should().BeApproximately(77.77, 0.001);
    }

    #endregion

    #region Update

    [Fact]
    public async Task Update_ModifiesExistingRow()
    {
        var table = _dialect.TableReference(null, "Products");
        var updateSql = $"UPDATE {table} SET " +
                        $"{_dialect.EscapeIdentifier("Price")} = @price " +
                        $"WHERE {_dialect.EscapeIdentifier("Id")} {_dialect.GetOperator("_eq")} @id";

        await using (var cmd = new SqliteCommand(updateSql, _connection))
        {
            cmd.Parameters.AddWithValue("@price", 999.99);
            cmd.Parameters.AddWithValue("@id", 1);
            var affected = await cmd.ExecuteNonQueryAsync();
            affected.Should().Be(1);
        }

        var selectSql = $"SELECT {_dialect.EscapeIdentifier("Price")} FROM {table} " +
                        $"WHERE {_dialect.EscapeIdentifier("Id")} = @id";
        await using var selectCmd = new SqliteCommand(selectSql, _connection);
        selectCmd.Parameters.AddWithValue("@id", 1);
        var price = (double)(await selectCmd.ExecuteScalarAsync())!;
        price.Should().BeApproximately(999.99, 0.001);
    }

    [Fact]
    public async Task Update_NonExistentRow_AffectsZeroRows()
    {
        var table = _dialect.TableReference(null, "Products");
        var updateSql = $"UPDATE {table} SET " +
                        $"{_dialect.EscapeIdentifier("Price")} = @price " +
                        $"WHERE {_dialect.EscapeIdentifier("Id")} {_dialect.GetOperator("_eq")} @id";

        await using var cmd = new SqliteCommand(updateSql, _connection);
        cmd.Parameters.AddWithValue("@price", 0.01);
        cmd.Parameters.AddWithValue("@id", 99999);
        var affected = await cmd.ExecuteNonQueryAsync();
        affected.Should().Be(0);
    }

    #endregion

    #region Delete

    [Fact]
    public async Task Delete_RemovesRow()
    {
        var table = _dialect.TableReference(null, "Products");
        var deleteSql = $"DELETE FROM {table} " +
                        $"WHERE {_dialect.EscapeIdentifier("Id")} {_dialect.GetOperator("_eq")} @id";

        await using (var cmd = new SqliteCommand(deleteSql, _connection))
        {
            cmd.Parameters.AddWithValue("@id", 10);
            var affected = await cmd.ExecuteNonQueryAsync();
            affected.Should().Be(1);
        }

        // Verify deletion
        var countSql = $"SELECT COUNT(*) FROM {table}";
        await using var countCmd = new SqliteCommand(countSql, _connection);
        var count = (long)(await countCmd.ExecuteScalarAsync())!;
        count.Should().Be(9);
    }

    [Fact]
    public async Task Delete_NonExistentRow_AffectsZeroRows()
    {
        var table = _dialect.TableReference(null, "Products");
        var deleteSql = $"DELETE FROM {table} " +
                        $"WHERE {_dialect.EscapeIdentifier("Id")} {_dialect.GetOperator("_eq")} @id";

        await using var cmd = new SqliteCommand(deleteSql, _connection);
        cmd.Parameters.AddWithValue("@id", 99999);
        var affected = await cmd.ExecuteNonQueryAsync();
        affected.Should().Be(0);
    }

    #endregion

    #region Joins with Dialect-Generated SQL

    [Fact]
    public async Task Join_WithForeignKey_ExecutesCorrectly()
    {
        // Create related tables for join testing
        await using (var cmd = new SqliteCommand(@"
            CREATE TABLE Categories (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL
            )", _connection))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new SqliteCommand(
            "INSERT INTO Categories (Name) VALUES ('Electronics'), ('Books')", _connection))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        var pTable = _dialect.EscapeIdentifier("Products");
        var cTable = _dialect.EscapeIdentifier("Categories");
        var sql = $"SELECT p.{_dialect.EscapeIdentifier("Name")} AS ProductName, " +
                  $"c.{_dialect.EscapeIdentifier("Name")} AS CategoryName " +
                  $"FROM {pTable} p " +
                  $"INNER JOIN {cTable} c ON p.{_dialect.EscapeIdentifier("Category")} = c.{_dialect.EscapeIdentifier("Name")} " +
                  $"WHERE c.{_dialect.EscapeIdentifier("Name")} {_dialect.GetOperator("_eq")} @cat";

        await using var joinCmd = new SqliteCommand(sql, _connection);
        joinCmd.Parameters.AddWithValue("@cat", "Electronics");
        var count = await CountRowsFromReaderAsync(joinCmd);
        count.Should().Be(4);
    }

    #endregion

    #region Combined Filter + Pagination

    [Fact]
    public async Task FilterAndPagination_WorkTogether()
    {
        var sortCols = new[] { $"{_dialect.EscapeIdentifier("Price")} ASC" };
        var pagination = _dialect.Pagination(sortCols, 0, 2);
        var op = _dialect.GetOperator("_eq");
        var sql = $"SELECT {_dialect.EscapeIdentifier("Name")}, {_dialect.EscapeIdentifier("Price")} " +
                  $"FROM {_dialect.EscapeIdentifier("Products")} " +
                  $"WHERE {_dialect.EscapeIdentifier("Category")} {op} @cat" +
                  pagination;

        await using var cmd = new SqliteCommand(sql, _connection);
        cmd.Parameters.AddWithValue("@cat", "Electronics");
        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        names.Should().HaveCount(2);
        // Electronics sorted by price ASC: Gamma (5.99), Alpha (10), Iota (33.33), Zeta (42)
        names.Should().Equal("Gamma", "Alpha");
    }

    #endregion

    #region Schema-Qualified Table Reference

    [Fact]
    public async Task SchemaQualifiedReference_ExecutesCorrectly()
    {
        var table = _dialect.TableReference("main", "Products");
        var sql = $"SELECT COUNT(*) FROM {table}";

        await using var cmd = new SqliteCommand(sql, _connection);
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        count.Should().Be(10);
    }

    [Fact]
    public async Task UnqualifiedReference_ExecutesCorrectly()
    {
        var table = _dialect.TableReference(null, "Products");
        var sql = $"SELECT COUNT(*) FROM {table}";

        await using var cmd = new SqliteCommand(sql, _connection);
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        count.Should().Be(10);
    }

    #endregion

    #region Helpers

    private async Task<int> CountRowsAsync(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _connection);
        return await CountRowsFromReaderAsync(cmd);
    }

    private static async Task<int> CountRowsFromReaderAsync(SqliteCommand cmd)
    {
        var count = 0;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) count++;
        return count;
    }

    private async Task<List<T>> ReadColumnAsync<T>(string sql, string column)
    {
        await using var cmd = new SqliteCommand(sql, _connection);
        var results = new List<T>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add((T)reader[column]);
        }
        return results;
    }

    #endregion
}
