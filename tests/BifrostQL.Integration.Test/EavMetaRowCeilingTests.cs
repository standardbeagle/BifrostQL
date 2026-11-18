using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.Eav;
using BifrostQL.Integration.Test.Infrastructure;
using BifrostQL.Integration.Test.Perf;
using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;

namespace BifrostQL.Integration.Test;

/// <summary>
/// <c>_meta</c> reads at most the row ceiling's worth of attributes per parent. The window
/// is taken in SQL (ROW_NUMBER per parent, ordered by attribute key) with one sentinel row,
/// and the sibling <c>_metaTruncated</c> field reports a parent the window cut. Before the
/// sibling field, the cut was a reserved <c>"__truncated": true</c> key inside <c>_meta</c>,
/// and an attribute row keyed <c>__truncated</c> threw inside the batch provider, failing the
/// read of every row on the page.
/// </summary>
public abstract class EavMetaRowCeilingTestBase
{
    // max-query-rows 10: a probe window of 9 attributes plus the sentinel row.
    private const int Ceiling = 10;
    private const int Cap = Ceiling - 1;
    private const int HeavyAttributes = Cap + 5;

    protected abstract IIntegrationTestDatabase CreateDatabase();
    protected abstract bool Available { get; }
    protected abstract IReadOnlyList<string> Ddl { get; }

    [SkippableFact]
    public async Task A_parent_past_the_ceiling_returns_the_cap_and_says_it_was_truncated()
    {
        Skip.IfNot(Available, "test database connection string not set");
        await using var db = await SeedAsync();
        await using var runner = await CreateRunnerAsync(db);

        var rows = DataRows(await runner.RunAsync("{ eav_posts(sort: [id_asc]) { data { id _meta _metaTruncated } } }"));

        var heavy = rows[0];
        heavy.GetProperty("_meta").EnumerateObject().Select(p => p.Name).Should().Equal(
            Enumerable.Range(0, Cap).Select(i => $"k{i:D2}"),
            "the window keeps the first max-query-rows - 1 attributes in key order, with no marker key");
        heavy.GetProperty("_metaTruncated").GetBoolean().Should().BeTrue();

        // Control: a parent under the ceiling is complete and reports no cut.
        var light = rows[1];
        light.GetProperty("_meta").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("__truncated", "color", "size");
        light.GetProperty("_metaTruncated").GetBoolean().Should().BeFalse();
    }

    [SkippableFact]
    public async Task An_attribute_keyed_like_the_old_marker_reads_back_as_data()
    {
        Skip.IfNot(Available, "test database connection string not set");
        await using var db = await SeedAsync();
        await using var runner = await CreateRunnerAsync(db);

        // Only _meta is selected: the page must read even though one attribute row uses the
        // key the old in-band marker reserved.
        var rows = DataRows(await runner.RunAsync("{ eav_posts(sort: [id_asc]) { data { id _meta } } }"));

        rows[1].GetProperty("_meta").GetProperty("__truncated").GetString().Should().Be("not-a-marker");
        rows[0].GetProperty("_meta").EnumerateObject().Should().HaveCount(Cap,
            "the other parent on the page still reads");
    }

    private async Task<IIntegrationTestDatabase> SeedAsync()
    {
        var db = CreateDatabase();
        await db.InitializeAsync();
        await using var conn = db.ConnFactory.GetConnection();
        await conn.OpenAsync();
        var statements = Ddl.Concat(new[]
        {
            "INSERT INTO eav_posts (id, title) VALUES (1, 'heavy')",
            "INSERT INTO eav_posts (id, title) VALUES (2, 'light')",
        }).Concat(Enumerable.Range(0, HeavyAttributes).Select(i =>
            $"INSERT INTO eav_postmeta (id, post_id, meta_key, meta_value) VALUES ({i + 1}, 1, 'k{i:D2}', 'v{i}')"))
          .Concat(new[]
        {
            $"INSERT INTO eav_postmeta (id, post_id, meta_key, meta_value) VALUES ({HeavyAttributes + 1}, 2, 'color', 'red')",
            $"INSERT INTO eav_postmeta (id, post_id, meta_key, meta_value) VALUES ({HeavyAttributes + 2}, 2, 'size', 'L')",
            $"INSERT INTO eav_postmeta (id, post_id, meta_key, meta_value) VALUES ({HeavyAttributes + 3}, 2, '__truncated', 'not-a-marker')",
        });
        foreach (var sql in statements)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
        return db;
    }

    private static Task<PerfGraphQlRunner> CreateRunnerAsync(IIntegrationTestDatabase db) =>
        PerfGraphQlRunner.CreateAsync(db.ConnFactory,
            new[]
            {
                $":root {{ {MetadataKeys.Model.MaxQueryRows}: {Ceiling} }}",
                "*.eav_postmeta { eav-parent: eav_posts; eav-fk: post_id; eav-key: meta_key; eav-value: meta_value }",
            },
            new EavMetaProvider(), new EavMetaTruncatedProvider());

    private static JsonElement DataRows(ExecutionResult result)
    {
        using var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(result));
        return doc.RootElement.GetProperty("data").GetProperty("eav_posts").GetProperty("data").Clone();
    }
}

public sealed class SqliteEavMetaRowCeilingTests : EavMetaRowCeilingTestBase
{
    protected override IIntegrationTestDatabase CreateDatabase() => new SqliteTestDatabase();
    protected override bool Available => true;
    protected override IReadOnlyList<string> Ddl => new[]
    {
        "CREATE TABLE eav_posts (id INTEGER PRIMARY KEY, title TEXT NOT NULL)",
        "CREATE TABLE eav_postmeta (id INTEGER PRIMARY KEY, post_id INTEGER NOT NULL REFERENCES eav_posts(id), meta_key TEXT NOT NULL, meta_value TEXT)",
    };
}

public sealed class PostgresEavMetaRowCeilingTests : EavMetaRowCeilingTestBase
{
    protected override IIntegrationTestDatabase CreateDatabase() => new PostgresTestDatabase();
    protected override bool Available => PostgresTestDatabase.ConnectionString is not null;
    protected override IReadOnlyList<string> Ddl => new[]
    {
        "CREATE TABLE eav_posts (id INT PRIMARY KEY, title TEXT NOT NULL)",
        "CREATE TABLE eav_postmeta (id INT PRIMARY KEY, post_id INT NOT NULL REFERENCES eav_posts(id), meta_key TEXT NOT NULL, meta_value TEXT)",
    };
}

public sealed class MySqlEavMetaRowCeilingTests : EavMetaRowCeilingTestBase
{
    protected override IIntegrationTestDatabase CreateDatabase() => new MySqlTestDatabase();
    protected override bool Available => MySqlTestDatabase.ConnectionString is not null;
    protected override IReadOnlyList<string> Ddl => new[]
    {
        "CREATE TABLE eav_posts (id INT PRIMARY KEY, title VARCHAR(50) NOT NULL)",
        "CREATE TABLE eav_postmeta (id INT PRIMARY KEY, post_id INT NOT NULL, meta_key VARCHAR(50) NOT NULL, meta_value VARCHAR(50), FOREIGN KEY (post_id) REFERENCES eav_posts(id))",
    };
}

public sealed class SqlServerEavMetaRowCeilingTests : EavMetaRowCeilingTestBase
{
    protected override IIntegrationTestDatabase CreateDatabase() => new SqlServerTestDatabase();
    protected override bool Available => SqlServerTestDatabase.ConnectionString is not null;
    protected override IReadOnlyList<string> Ddl => new[]
    {
        "CREATE TABLE eav_posts (id INT PRIMARY KEY, title NVARCHAR(50) NOT NULL)",
        "CREATE TABLE eav_postmeta (id INT PRIMARY KEY, post_id INT NOT NULL REFERENCES eav_posts(id), meta_key NVARCHAR(50) NOT NULL, meta_value NVARCHAR(50))",
    };
}
