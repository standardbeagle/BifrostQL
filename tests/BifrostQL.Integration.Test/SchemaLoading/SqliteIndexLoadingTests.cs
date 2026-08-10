using System.Linq;
using BifrostQL.Core.Model;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace BifrostQL.Integration.Test.SchemaLoading;

/// <summary>
/// End-to-end index discovery through DbModelLoader on SQLite (always runs,
/// in-memory). Exercises the whole chain the _dbSchema surface depends on:
/// reader &rarr; SchemaData.Indexes &rarr; AttachIndexes on the per-build clones.
/// Partial and expression-key indexes must be dropped whole — a client cannot
/// order by a predicate or an expression, so listing them would misrepresent
/// the table's access paths.
/// </summary>
[Collection("SqliteSchemaLoading")]
public class SqliteIndexLoadingTests : IAsyncLifetime
{
    private string? _connectionString;
    private SqliteConnection? _keepAliveConnection;
    private IDbModel? _model;

    public async Task InitializeAsync()
    {
        _connectionString = $"Data Source=bifrost_idx_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        await _keepAliveConnection.OpenAsync();

        var statements = new[]
        {
            @"CREATE TABLE Claims (
                ClaimId INTEGER PRIMARY KEY AUTOINCREMENT,
                Code TEXT NOT NULL,
                City TEXT NOT NULL,
                Year INTEGER NOT NULL,
                Amount REAL NOT NULL
            )",
            "CREATE INDEX IX_Claims_Code_City ON Claims (Code, City)",
            "CREATE UNIQUE INDEX UX_Claims_Code_Year ON Claims (Code, Year)",
            "CREATE INDEX IX_Claims_Partial ON Claims (Amount) WHERE Amount > 0",
            "CREATE INDEX IX_Claims_Expr ON Claims (lower(City))",
        };
        foreach (var sql in statements)
        {
            await using var cmd = _keepAliveConnection.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }

        var factory = new SqliteDbConnFactory(_connectionString);
        var loader = new DbModelLoader(factory, new MetadataLoader(Array.Empty<string>()));
        _model = await loader.LoadAsync();
    }

    public async Task DisposeAsync()
    {
        if (_keepAliveConnection != null)
            await _keepAliveConnection.DisposeAsync();
    }

    private IDbTable Claims() => _model!.Tables.Single(t => t.DbName == "Claims");

    [Fact]
    public void CompositeIndex_KeepsKeyColumnOrder()
    {
        var index = Claims().Indexes.Single(i => i.Name == "IX_Claims_Code_City");

        index.ColumnNames.Should().Equal("Code", "City");
        index.IsUnique.Should().BeFalse();
        index.IsPrimaryKey.Should().BeFalse();
    }

    [Fact]
    public void UniqueIndex_IsFlaggedUnique()
    {
        var index = Claims().Indexes.Single(i => i.Name == "UX_Claims_Code_Year");

        index.IsUnique.Should().BeTrue();
        index.ColumnNames.Should().Equal("Code", "Year");
    }

    [Fact]
    public void PartialAndExpressionIndexes_AreDroppedWhole()
    {
        Claims().Indexes.Select(i => i.Name)
            .Should().NotContain(new[] { "IX_Claims_Partial", "IX_Claims_Expr" });
    }
}
