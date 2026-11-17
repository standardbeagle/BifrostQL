using BifrostQL.Core.Modules.Eav;
using Xunit.Abstractions;

namespace BifrostQL.Integration.Test.Perf;

/// <summary>
/// <c>_meta</c> on an EAV parent page: commands per request and wall time for a page of
/// N parents with 5 attribute rows each. The floor is one statement for the page plus one
/// for the attribute rows of every parent on it (more only when the key list is chunked).
/// </summary>
public sealed class EavMetaBatchPerfTests(ITestOutputHelper output)
{
    private const int Parents = 2000;
    private const int AttributesPerParent = 5;

    [PerfFact]
    public Task Postgres() => MeasureAsync(PerfEngine.Postgres,
        $"""
        CREATE TABLE posts (id INT PRIMARY KEY, title TEXT NOT NULL);
        CREATE TABLE postmeta (id SERIAL PRIMARY KEY, post_id INT NOT NULL REFERENCES posts(id), meta_key TEXT NOT NULL, meta_value TEXT);
        CREATE INDEX ix_postmeta_post ON postmeta(post_id);
        INSERT INTO posts SELECT g, 'p' || g FROM generate_series(1, {Parents}) g;
        INSERT INTO postmeta(post_id, meta_key, meta_value)
          SELECT p, 'k' || a, 'v' || a FROM generate_series(1, {Parents}) p, generate_series(1, {AttributesPerParent}) a;
        ANALYZE;
        """);

    [PerfFact]
    public Task SqlServer() => MeasureAsync(PerfEngine.SqlServer,
        $"""
        CREATE TABLE posts (id INT PRIMARY KEY, title NVARCHAR(50) NOT NULL);
        CREATE TABLE postmeta (id INT IDENTITY PRIMARY KEY, post_id INT NOT NULL REFERENCES posts(id), meta_key NVARCHAR(50) NOT NULL, meta_value NVARCHAR(50));
        CREATE INDEX ix_postmeta_post ON postmeta(post_id);
        WITH n AS (SELECT TOP ({Parents}) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS g FROM sys.all_objects a CROSS JOIN sys.all_objects b)
        INSERT INTO posts SELECT g, CONCAT('p', g) FROM n;
        WITH a AS (SELECT TOP ({AttributesPerParent}) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS k FROM sys.all_objects)
        INSERT INTO postmeta(post_id, meta_key, meta_value) SELECT p.id, CONCAT('k', a.k), CONCAT('v', a.k) FROM posts p CROSS JOIN a;
        """);

    private async Task MeasureAsync(PerfEngine engine, string ddl)
    {
        Skip.If(PerfDatabase.MasterFor(engine) is null, $"{engine} test connection string not set");
        await using var db = await PerfDatabase.CreateAsync(engine, "bifrost_perf_eav", ddl);
        await using var runner = await PerfGraphQlRunner.CreateAsync(db.Factory,
            new[] { "*.postmeta { eav-parent: posts; eav-fk: post_id; eav-key: meta_key; eav-value: meta_value }" },
            new EavMetaProvider());

        foreach (var page in new[] { 10, 100, 1000 })
        {
            var (median, p90, commands) = await runner.MeasureAsync(
                $"{{ posts(limit: {page}, sort: [id_asc]) {{ data {{ id _meta }} }} }}", warmup: 3, runs: 15);
            output.WriteLine($"{engine} page={page}: {commands} commands/request, median {median:F1} ms, p90 {p90:F1} ms");
        }
    }
}
