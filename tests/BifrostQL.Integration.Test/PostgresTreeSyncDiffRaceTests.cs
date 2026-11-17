using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BifrostQL.Integration.Test;

/// <summary>
/// A nested sync diffs the submitted tree against the rows it loaded, then writes the
/// difference. The load must happen inside the write transaction and keep the loaded rows
/// locked until the commit; otherwise another writer can change the subtree between the
/// diff and the write, and the sync commits a result computed from a state that no longer
/// exists. Here a concurrent writer tries to add a child to the synced parent while the
/// sync's transaction is open, between its first write and its commit.
/// </summary>
public sealed class PostgresTreeSyncDiffRaceTests : IAsyncLifetime
{
    private const string Ddl =
        """
        CREATE TABLE blogs (id SERIAL PRIMARY KEY, name TEXT NOT NULL);
        CREATE TABLE posts (id SERIAL PRIMARY KEY, blog_id INT NOT NULL REFERENCES blogs(id), title TEXT NOT NULL);
        INSERT INTO blogs (name) VALUES ('original');
        INSERT INTO posts (blog_id, title) VALUES (1, 'keep'), (1, 'drop');
        """;

    private readonly ConcurrentWriterHook _hook = new();
    private PostgresGraphQlHost? _host;

    public async Task InitializeAsync()
    {
        if (PostgresGraphQlHost.Master is null) return;
        _host = await PostgresGraphQlHost.StartAsync("bifrost_sync_race", Ddl, Array.Empty<string>(),
            services => services.AddSingleton<IInTransactionMutationHook>(_hook));
        _hook.ConnectionString = _host.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task ConcurrentChildInsert_CannotLandBetweenTheDiffAndTheCommit()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        PostgresGraphQlHost.AssertNoErrors(await _host!.PostAsync(
            """mutation { blogs(sync: { id: 1, name: "renamed", posts: [ { id: 1, title: "keep" } ] }) }"""));

        _hook.Attempted.Should().BeTrue("the hook runs inside the sync's transaction, after its root write");
        _hook.Failure.Should().NotBeNull("the parent the sync loaded stays locked until it commits");
        _hook.Failure!.SqlState.Should().Be(PostgresErrorCodes.LockNotAvailable);

        var titles = (await _host.RowsAsync("SELECT title FROM posts WHERE blog_id = 1 ORDER BY id")).Select(r => r["title"]);
        titles.Should().Equal(new[] { "keep" }, "the committed children are exactly the submitted set");
    }

    /// <summary>
    /// Plays the concurrent writer once, from inside the sync's transaction: a separate
    /// connection inserts a post under blog 1 with a short lock timeout and records how
    /// it ended.
    /// </summary>
    private sealed class ConcurrentWriterHook : IInTransactionMutationHook
    {
        public string ConnectionString { get; set; } = "";
        public bool Attempted { get; private set; }
        public PostgresException? Failure { get; private set; }

        public bool AppliesTo(IDbTable table) => string.Equals(table.DbName, "blogs", StringComparison.Ordinal);

        public async ValueTask AfterWriteInTransactionAsync(MutationObserverContext context)
        {
            if (Attempted) return;
            Attempted = true;
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SET lock_timeout = '1s'; INSERT INTO posts (blog_id, title) VALUES (1, 'concurrent');", conn);
            try
            {
                await cmd.ExecuteNonQueryAsync();
            }
            catch (PostgresException ex)
            {
                Failure = ex;
            }
        }
    }
}
