using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.Eav;
using BifrostQL.Integration.Test.Infrastructure;
using BifrostQL.Integration.Test.Perf;
using FluentAssertions;
using GraphQL.SystemTextJson;

namespace BifrostQL.Integration.Test;

/// <summary>
/// <c>_meta</c> reads at most the row ceiling's worth of attributes per parent. The window
/// is taken in SQL (ROW_NUMBER per parent, ordered by attribute key) with one sentinel row,
/// and a parent the window cut carries <see cref="EavMetaProvider.TruncatedKey"/>. Before
/// the window, a parent with N attribute rows returned all N however large N was.
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
        await using var db = CreateDatabase();
        await db.InitializeAsync();
        await using (var conn = db.ConnFactory.GetConnection())
        {
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
            });
            foreach (var sql in statements)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync();
            }
        }

        await using var runner = await PerfGraphQlRunner.CreateAsync(db.ConnFactory,
            new[]
            {
                $":root {{ {MetadataKeys.Model.MaxQueryRows}: {Ceiling} }}",
                "*.eav_postmeta { eav-parent: eav_posts; eav-fk: post_id; eav-key: meta_key; eav-value: meta_value }",
            },
            new EavMetaProvider());

        var result = await runner.RunAsync("{ eav_posts(sort: [id_asc]) { data { id _meta } } }");
        using var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(result));
        var rows = doc.RootElement.GetProperty("data").GetProperty("eav_posts").GetProperty("data");

        var heavy = rows[0].GetProperty("_meta");
        var heavyKeys = heavy.EnumerateObject().Select(p => p.Name).Where(n => n != EavMetaProvider.TruncatedKey).ToList();
        heavyKeys.Should().Equal(Enumerable.Range(0, Cap).Select(i => $"k{i:D2}"),
            "the window keeps the first max-query-rows - 1 attributes in key order");
        heavy.GetProperty(EavMetaProvider.TruncatedKey).GetBoolean().Should().BeTrue();

        // Control: a parent under the ceiling is complete and carries no marker.
        var light = rows[1].GetProperty("_meta");
        light.EnumerateObject().Select(p => p.Name).Should().Equal("color", "size");
        light.TryGetProperty(EavMetaProvider.TruncatedKey, out _).Should().BeFalse();
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
