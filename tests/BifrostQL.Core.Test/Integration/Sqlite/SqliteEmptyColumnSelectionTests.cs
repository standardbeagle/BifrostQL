using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Regression test for the empty-column SELECT bug.
///
/// When a paged GraphQL query asks only for meta fields (e.g. `labels { total }`)
/// without selecting any `data { ... }` children, GqlObjectQuery.AddSqlParameterized
/// previously emitted `SELECT  FROM labels ...` which produced a
/// "near 'FROM': syntax error" on every dialect — most visibly on SQLite where
/// the table-list sidebar of the edit-db UI showed "err" for every row count.
///
/// The fix skips the data SQL entirely when no scalar columns and no joins are
/// requested, while still emitting the COUNT(*) statement that powers `total`.
/// </summary>
public sealed class SqliteEmptyColumnSelectionTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_empty_select_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await using (var drop = new SqliteCommand("DROP TABLE IF EXISTS labels", _keepAlive))
            await drop.ExecuteNonQueryAsync();

        await using (var create = new SqliteCommand(
            "CREATE TABLE labels (id INTEGER PRIMARY KEY, name TEXT NOT NULL)", _keepAlive))
            await create.ExecuteNonQueryAsync();

        await using (var insert = new SqliteCommand(
            "INSERT INTO labels(name) VALUES ('alpha'),('beta'),('gamma')", _keepAlive))
            await insert.ExecuteNonQueryAsync();

        var factory = new SqliteDbConnFactory(ConnString);
        var metadataLoader = new MetadataLoader(Array.Empty<string>());
        var loader = new DbModelLoader(factory, metadataLoader);
        _model = await loader.LoadAsync();
    }

    public async Task DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
    }

    [Fact]
    public async Task PagedQueryAskingOnlyForTotal_ProducesValidExecutableCountSql()
    {
        // Mirror the frontend query: `labels(limit: 1) { total }` — no `data { ... }`,
        // so ScalarColumns and Joins are both empty but IncludeResult is true.
        var labels = _model.GetTableFromDbName("labels");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(labels)
            .WithLimit(1)
            .IncludeResult()
            .Build();

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(_model, SqliteDialect.Instance, sqls, parameters);

        // No data SELECT — only the count statement that backs the `total` field.
        sqls.Should().NotContainKey(query.KeyName,
            "the data query has no columns to select and must be skipped");
        sqls.Should().ContainKey($"{query.KeyName}=>count");

        var countSql = sqls[$"{query.KeyName}=>count"].Sql;
        countSql.Should().StartWith("SELECT COUNT(*) FROM");

        // Execute the generated count SQL against the real SQLite connection;
        // it must run without a syntax error and return the seeded row count.
        await using var cmd = new SqliteCommand(countSql, _keepAlive);
        var result = await cmd.ExecuteScalarAsync();
        Convert.ToInt32(result).Should().Be(3);
    }
}
