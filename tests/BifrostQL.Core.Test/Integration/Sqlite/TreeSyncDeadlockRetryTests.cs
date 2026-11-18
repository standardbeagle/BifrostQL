using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// <see cref="TreeSyncExecutor.SyncAsync"/> reruns its whole transaction when the database
/// picks it as a deadlock victim (SQL Server error 1205), at most
/// <see cref="TreeSyncExecutor.MaxDeadlockVictimAttempts"/> runs, then rethrows the last
/// failure. SQL Server's <c>UPDLOCK, HOLDLOCK</c> child read can deadlock against a concurrent
/// writer, and the victim's transaction is already rolled back, so a rerun is safe. The
/// deadlock is injected on the sync's load statement, the way SqlClient reports it: a
/// <see cref="DbException"/> with no SqlState and the error number under
/// <c>Data["HelpLink.EvtID"]</c>.
/// </summary>
public sealed class TreeSyncDeadlockRetryTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"bifrost-sync-deadlock-{Guid.NewGuid():N}.db");
    private string ConnString => $"Data Source={_dbPath}";
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        await using (var conn = new SqliteConnection(ConnString))
        {
            await conn.OpenAsync();
            await using var cmd = new SqliteCommand(
                """
                CREATE TABLE blogs (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
                CREATE TABLE posts (id INTEGER PRIMARY KEY, blog_id INTEGER NOT NULL REFERENCES blogs(id), title TEXT NOT NULL);
                INSERT INTO blogs (id, name) VALUES (1, 'original');
                INSERT INTO posts (id, blog_id, title) VALUES (1, 1, 'keep');
                """, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        _model = await new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(Array.Empty<string>())).LoadAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_deadlock_victim_reruns_the_sync_and_commits()
    {
        var injected = new List<DbException>();
        var loads = 0;

        await SyncRenameAsync(sql =>
        {
            if (!IsRootLoad(sql)) return;
            loads++;
            if (loads < TreeSyncExecutor.MaxDeadlockVictimAttempts)
                Throw(injected, SqlServerError(DeadlockVictim));
        });

        loads.Should().Be(TreeSyncExecutor.MaxDeadlockVictimAttempts, "every victim run reloads from scratch");
        (await BlogNameAsync()).Should().Be("renamed");
    }

    [Fact]
    public async Task A_sync_that_stays_the_victim_rethrows_the_last_deadlock_after_the_bounded_runs()
    {
        var injected = new List<DbException>();
        var loads = 0;

        var act = () => SyncRenameAsync(sql =>
        {
            if (!IsRootLoad(sql)) return;
            loads++;
            Throw(injected, SqlServerError(DeadlockVictim));
        });

        var thrown = (await act.Should().ThrowAsync<Exception>()).Which;
        loads.Should().Be(TreeSyncExecutor.MaxDeadlockVictimAttempts);
        Chain(thrown).Should().Contain(injected[^1], "the original driver error is what surfaces");
        Chain(thrown).Should().NotContain(injected.Take(injected.Count - 1), "earlier runs' errors are not what surfaces");
        (await BlogNameAsync()).Should().Be("original");
    }

    /// <summary>Control: a driver error that is not a deadlock runs once.</summary>
    [Fact]
    public async Task Any_other_driver_error_is_not_retried()
    {
        var injected = new List<DbException>();
        var loads = 0;

        var act = () => SyncRenameAsync(sql =>
        {
            if (!IsRootLoad(sql)) return;
            loads++;
            Throw(injected, SqlServerError(LockRequestTimeout));
        });

        var thrown = (await act.Should().ThrowAsync<Exception>()).Which;
        loads.Should().Be(1);
        Chain(thrown).Should().Contain(injected[0]);
    }

    private const string DeadlockVictim = "1205";
    private const string LockRequestTimeout = "1222";

    private async Task SyncRenameAsync(Action<string> beforeExecute)
    {
        var factory = new SqlLoggingConnFactory(new SqliteDbConnFactory(ConnString), new List<string>(), null, beforeExecute);
        var blogs = _model.GetTableFromDbName("blogs");
        var userContext = new Dictionary<string, object?>();
        var tree = new Dictionary<string, object?> { ["id"] = 1L, ["name"] = "renamed" };
        await new TreeSyncExecutor(factory.Dialect).SyncAsync(
            blogs, tree, new TreeSyncStateLoader(factory.Dialect, _model, userContext), new TreeSyncEngine(_model),
            factory, new MutationTransformersWrap(), _model, userContext, services: null);
    }

    // The loader's root read: SELECT ... FROM "main"."blogs" ... (the root update is an UPDATE).
    private static bool IsRootLoad(string sql)
        => sql.StartsWith("SELECT", StringComparison.Ordinal) && sql.Contains("\"blogs\"", StringComparison.Ordinal);

    private static void Throw(List<DbException> injected, DbException error)
    {
        injected.Add(error);
        throw error;
    }

    private async Task<string?> BlogNameAsync()
    {
        await using var conn = new SqliteConnection(ConnString);
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand("SELECT name FROM blogs WHERE id = 1", conn);
        return (string?)await cmd.ExecuteScalarAsync();
    }

    private static List<Exception> Chain(Exception ex)
    {
        var chain = new List<Exception>();
        for (Exception? e = ex; e is not null; e = e.InnerException) chain.Add(e);
        return chain;
    }

    private static DbException SqlServerError(string number) => new SqlServerShapedError(number);

    /// <summary>A SqlClient-shaped error: no SqlState, the server error number in Data.</summary>
    private sealed class SqlServerShapedError : DbException
    {
        public SqlServerShapedError(string number) : base($"injected SQL Server error {number}")
        {
            Data["HelpLink.EvtID"] = number;
        }
    }
}
