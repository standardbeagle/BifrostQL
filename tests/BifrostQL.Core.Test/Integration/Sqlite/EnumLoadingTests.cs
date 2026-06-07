using BifrostQL.Core.Model;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Integration test for schema-build-time enum value loading over SQLite
/// (in-memory). Verifies that <see cref="DbModelLoader.LoadEnumValuesAsync"/>
/// reads the DISTINCT values of an <c>enum:</c>-marked lookup table and that
/// <see cref="EnumColumnMap.Build"/> resolves a referencing FK column to the
/// emitted enum type.
/// </summary>
public sealed class EnumLoadingTests : IAsyncLifetime
{
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;
    private DbModelLoader _loader = null!;
    private const string ConnString = "Data Source=bifrost_enum_test;Mode=Memory;Cache=Shared";

    // "*" matches any schema; SQLite tables live in "main". Marks the status
    // lookup table as an enum whose value column is "code".
    private const string Rule = "*.status { enum: code }";

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await CreateSchemaAsync(_keepAlive);

        var factory = new SqliteDbConnFactory(ConnString);
        _loader = new DbModelLoader(factory, new MetadataLoader(new[] { Rule }));
        _model = await _loader.LoadAsync();
    }

    public async Task DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
    }

    private static async Task CreateSchemaAsync(SqliteConnection conn)
    {
        var statements = new[]
        {
            "DROP TABLE IF EXISTS orders",
            "DROP TABLE IF EXISTS status",
            "CREATE TABLE status (id INTEGER PRIMARY KEY, code TEXT)",
            "INSERT INTO status(code) VALUES ('active'),('inactive')",
            "CREATE TABLE orders (id INTEGER PRIMARY KEY, status TEXT REFERENCES status(code))",
        };

        foreach (var sql in statements)
        {
            await using var cmd = new SqliteCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task LoadEnumValuesAsync_LoadsDistinctValues_AndResolvesValueColumn()
    {
        var result = await _loader.LoadEnumValuesAsync(_model);

        result.ValueColumns.Should().ContainKey("status").WhoseValue.Should().Be("code");

        result.Values.Should().ContainKey("status");
        var names = result.Values["status"].Select(e => e.GraphQlName).ToList();
        names.Should().Contain("ACTIVE");
        names.Should().Contain("INACTIVE");
    }

    [Fact]
    public async Task EnumColumnMap_ResolvesReferencingColumn_ToEnumType()
    {
        var result = await _loader.LoadEnumValuesAsync(_model);

        var map = EnumColumnMap.Build(_model, result.Values, result.ValueColumns);

        map.TryGetEnumType("orders", "status", out var name).Should().BeTrue();
        name.Should().Be("statusValues");
    }
}
