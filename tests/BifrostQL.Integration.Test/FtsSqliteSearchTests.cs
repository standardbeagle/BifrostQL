using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
using GraphQL.Types;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Xunit;

namespace BifrostQL.Integration.Test;

/// <summary>
/// FTS slice 3 — behavioral coverage of the <c>_search</c> operator end-to-end against a
/// REAL SQLite FTS5 index (Microsoft.Data.Sqlite bundles FTS5). Proves the per-dialect
/// lowering actually matches rows, honors the pinned multi-term AND semantic, and — the
/// security-critical part — that <c>_search</c> composes as an AND with another predicate
/// so it can never return rows outside the ANDed scope (a stand-in here for the tenant /
/// soft-delete filters, whose AND composition inside QueryTransformerService is proven
/// structurally per dialect in FtsCompositionTests). The FTS5 external-content companion
/// table is created AFTER the model loads, so it (and its shadow tables) never enter the
/// published model — only the base Articles table is searchable.
/// </summary>
[Collection("FtsSqliteSearch")]
public sealed class FtsSqliteSearchTests : IAsyncLifetime
{
    private string _connectionString = null!;
    private SqliteConnection _keepAlive = null!;
    private SqliteDbConnFactory _connFactory = null!;
    private BifrostQL.Core.Model.IDbModel _model = null!;
    private ISchema _schema = null!;

    // Articles is searchable over Title + Body. Schema is 'main' for SQLite.
    private static readonly string[] SearchMetadata =
    {
        "main.Articles { search: Title,Body }",
    };

    public async Task InitializeAsync()
    {
        _connectionString = $"Data Source=bifrost_fts_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();
        _connFactory = new SqliteDbConnFactory(_connectionString);

        await using (var conn = new SqliteConnection(_connectionString))
        {
            await conn.OpenAsync();
            await Exec(conn,
                @"CREATE TABLE Articles (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    TenantId INTEGER NOT NULL,
                    Title TEXT NOT NULL,
                    Body TEXT NOT NULL,
                    IsDeleted INTEGER NOT NULL DEFAULT 0
                )");
            await Exec(conn,
                @"INSERT INTO Articles (TenantId, Title, Body, IsDeleted) VALUES
                    (1, 'Quick Brown Fox', 'jumps over the lazy dog', 0),
                    (2, 'Quick Brown Bear', 'runs through the forest', 0),
                    (1, 'Slow Green Turtle', 'crawls along the beach', 0),
                    (1, 'Quick Brown Ghost', 'this row is deleted', 1)");
        }

        // Load the model from the BASE table only — the FTS5 companion table does not
        // exist yet, so neither it nor its shadow tables pollute the published model.
        var loader = new DbModelLoader(_connFactory, new MetadataLoader(SearchMetadata));
        _model = await loader.LoadAsync();
        _schema = DbSchema.FromModel(_model);

        // Now create the FTS5 external-content index the SQLite dialect targets
        // (<table>_fts, correlated by rowid = the integer PK) and populate it.
        await using (var conn = new SqliteConnection(_connectionString))
        {
            await conn.OpenAsync();
            await Exec(conn,
                "CREATE VIRTUAL TABLE Articles_fts USING fts5(Title, Body, content='Articles', content_rowid='Id')");
            await Exec(conn, "INSERT INTO Articles_fts(Articles_fts) VALUES('rebuild')");
        }
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private static async Task Exec(SqliteConnection conn, string sql)
    {
        var cmd = new SqliteCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<ExecutionResult> ExecuteAsync(string query)
    {
        var executor = new SqlExecutionManager(_model, _schema);
        var extensions = new Dictionary<string, object?>
        {
            { "connFactory", _connFactory },
            { "model", _model },
            { "tableReaderFactory", executor },
        };
        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = _schema;
            options.Query = query;
            options.Extensions = new Inputs(extensions);
        });
    }

    private async Task<HashSet<int>> SearchIdsAsync(string filter)
    {
        var result = await ExecuteAsync($"query {{ articles(filter: {filter}) {{ data {{ id }} }} }}");
        result.Errors.Should().BeNullOrEmpty();

        var json = new GraphQLSerializer().Serialize(result);
        using var doc = JsonDocument.Parse(json);
        var rows = doc.RootElement.GetProperty("data").GetProperty("articles").GetProperty("data");
        return rows.EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ToHashSet();
    }

    [Fact]
    public async Task Search_MultiTerm_MatchesRowsContainingEveryTerm()
    {
        // "quick brown" -> AND of both terms. Rows 1,2,4 contain both; row 3 (Slow Green)
        // contains neither, so it is excluded. Proves real FTS5 multi-term AND matching.
        var ids = await SearchIdsAsync("{ _search: \"quick brown\" }");
        ids.Should().BeEquivalentTo(new[] { 1, 2, 4 });
    }

    [Fact]
    public async Task Search_AndedWithTenantPredicate_CannotReturnOtherTenantsRows()
    {
        // _search AND tenantId=1: row 2 (tenant 2) also matches "quick brown" but MUST be
        // excluded by the ANDed tenant predicate — the cross-tenant non-leak guarantee.
        var ids = await SearchIdsAsync("{ _search: \"quick brown\", tenantId: { _eq: 1 } }");
        ids.Should().BeEquivalentTo(new[] { 1, 4 });
        ids.Should().NotContain(2);
    }

    [Fact]
    public async Task Search_AndedWithSoftDeletePredicate_CannotReturnDeletedRows()
    {
        // _search AND isDeleted=0: row 4 matches "quick brown" but is deleted and MUST be
        // excluded by the ANDed predicate — the soft-delete non-leak guarantee.
        var ids = await SearchIdsAsync("{ _search: \"quick brown\", isDeleted: { _eq: 0 } }");
        ids.Should().BeEquivalentTo(new[] { 1, 2 });
        ids.Should().NotContain(4);
    }

    [Fact]
    public async Task Search_ComposesWithPagingAndSort()
    {
        // _search must coexist with sort + paging (relevance ranking is out of scope — see
        // the FTS guide). Sort the three "quick brown" matches by id desc, take the first.
        var result = await ExecuteAsync(
            "query { articles(filter: { _search: \"quick brown\" }, sort: [id_desc], limit: 1) { data { id } } }");
        result.Errors.Should().BeNullOrEmpty();
        var json = new GraphQLSerializer().Serialize(result);
        using var doc = JsonDocument.Parse(json);
        var rows = doc.RootElement.GetProperty("data").GetProperty("articles").GetProperty("data");
        rows.GetArrayLength().Should().Be(1);
        rows[0].GetProperty("id").GetInt32().Should().Be(4);
    }
}
