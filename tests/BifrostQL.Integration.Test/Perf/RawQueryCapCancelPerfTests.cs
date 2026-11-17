using System.Diagnostics;
using BifrostQL.Core.Resolvers;
using Xunit.Abstractions;

namespace BifrostQL.Integration.Test.Perf;

/// <summary>
/// What stopping at <c>raw-sql-max-rows</c> costs on a large result: read cap+1 rows, then
/// either cancel the command before disposing the reader (what <see cref="RawSqlExecutor"/>
/// does) or dispose it straight away and let the provider drain what is left. Both arms run
/// the same loop on the same read-only scope; the production executor is timed beside them.
/// </summary>
public sealed class RawQueryCapCancelPerfTests(ITestOutputHelper output)
{
    private const int Cap = 100;

    [PerfFact]
    public Task SqlServer() => MeasureAsync(PerfEngine.SqlServer,
        "CREATE TABLE big (id INT PRIMARY KEY, payload NVARCHAR(100) NOT NULL);" +
        "WITH n AS (SELECT TOP (2000000) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS g FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c)" +
        " INSERT INTO big SELECT g, REPLICATE(N'x', 80) FROM n;",
        "SELECT id, payload FROM big");

    [PerfFact]
    public Task Postgres() => MeasureAsync(PerfEngine.Postgres,
        "CREATE TABLE big (id INT PRIMARY KEY, payload TEXT NOT NULL);" +
        "INSERT INTO big SELECT g, repeat('x', 80) FROM generate_series(1, 2000000) g; ANALYZE;",
        "SELECT id, payload FROM big");

    private async Task MeasureAsync(PerfEngine engine, string ddl, string query)
    {
        Skip.If(PerfDatabase.MasterFor(engine) is null, $"{engine} test connection string not set");
        await using var db = await PerfDatabase.CreateAsync(engine, "bifrost_perf_rawcap", ddl);

        foreach (var (name, run) in new (string, Func<Task>)[]
        {
            ("cancel-then-dispose", () => CappedReadAsync(db, query, cancel: true)),
            ("dispose-and-drain", () => CappedReadAsync(db, query, cancel: false)),
            ("RawSqlExecutor", async () =>
            {
                var result = await RawSqlExecutor.ExecuteReadOnlyAsync(db.Factory, query, null, 120, Cap, CancellationToken.None);
                if (!result.Truncated) throw new InvalidOperationException("cap not hit");
            }),
            ("full read (no cap)", () => FullReadAsync(db, query)),
        })
        {
            for (var i = 0; i < 2; i++) await run();
            var samples = new double[9];
            for (var i = 0; i < samples.Length; i++)
            {
                var sw = Stopwatch.StartNew();
                await run();
                samples[i] = sw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(samples);
            output.WriteLine($"{engine} {name}: median {samples[4]:F1} ms, min {samples[0]:F1} ms, max {samples[^1]:F1} ms");
        }
    }

    private static async Task CappedReadAsync(PerfDatabase db, string query, bool cancel)
    {
        await using var conn = db.Factory.GetConnection();
        await conn.OpenAsync();
        await using var scope = await db.Factory.Dialect.BeginReadOnlyScopeAsync(conn, CancellationToken.None);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = scope.Transaction;
        cmd.CommandText = query;
        cmd.CommandTimeout = 120;
        await using var reader = await cmd.ExecuteReaderAsync();
        var read = 0;
        while (await reader.ReadAsync())
        {
            if (read >= Cap)
            {
                if (cancel) cmd.Cancel();
                break;
            }
            _ = reader.GetValue(0);
            _ = reader.GetValue(1);
            read++;
        }
    }

    private static async Task FullReadAsync(PerfDatabase db, string query)
    {
        await using var conn = db.Factory.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = query;
        cmd.CommandTimeout = 120;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) { }
    }
}
