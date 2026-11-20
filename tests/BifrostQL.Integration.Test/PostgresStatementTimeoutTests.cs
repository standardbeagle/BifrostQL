using System.Collections.Concurrent;
using System.Net;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BifrostQL.Integration.Test;

/// <summary>
/// A statement the database cancels at its own <c>statement_timeout</c> (SQLSTATE <c>57014</c>)
/// while the request is still live is the host's timeout, not a GraphQL error: the write rolls
/// back and <see cref="BifrostStatementTimeoutException"/> leaves the BifrostQL middleware with
/// nothing written, so a middleware ahead of it can answer with its own status. The server's
/// connection runs with <c>statement_timeout</c> at 200 ms, and a second connection holds the
/// table's lock so the statement waits past it. A request cancelled by its own token is the
/// client leaving, and is never reported as a timeout.
/// </summary>
public sealed class PostgresStatementTimeoutTests : IAsyncLifetime
{
    private const string Ddl =
        """
        CREATE TABLE items (id SERIAL PRIMARY KEY, name TEXT NOT NULL UNIQUE);
        CREATE TABLE hook_side (id SERIAL PRIMARY KEY, note TEXT);
        CREATE TABLE plain (id SERIAL PRIMARY KEY, name TEXT NOT NULL);
        INSERT INTO items (name) VALUES ('seed');
        """;

    private const string HostAnswer = """{"error":"request_timeout"}""";

    private readonly ConcurrentQueue<bool> _caughtWithResponseStarted = new();
    private PostgresGraphQlHost? _host;

    public async Task InitializeAsync()
    {
        if (PostgresGraphQlHost.Master is null) return;
        _host = await PostgresGraphQlHost.StartAsync(
            "bifrost_statement_timeout", Ddl, Array.Empty<string>(),
            services: s => s.AddSingleton<IInTransactionMutationHook>(new ReadsHookSide()),
            beforeBifrost: app => app.Use(CatchTimeout),
            serverConnection: b => b.Options = "-c statement_timeout=200");
        // Load the schema now: the first request loads it, and a held lock must meet execution.
        PostgresGraphQlHost.AssertNoErrors(await _host.PostAsync("{ items { data { id } } }"));
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Read_CancelledByStatementTimeout_ThrowsToTheHost_WithNoResponseStarted()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        await using (await LockAsync(_host!.ConnectionString, "items"))
        {
            using var response = await _host.PostRawAsync("{ items { data { id name } } }", CancellationToken.None);

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, await response.Content.ReadAsStringAsync());
            (await response.Content.ReadAsStringAsync()).Should().Be(HostAnswer);
        }
        _caughtWithResponseStarted.Should().Equal(false);
    }

    [SkippableFact]
    public async Task Write_CancelledByStatementTimeout_RollsBack_AndThrowsToTheHost()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        await using (await LockAsync(_host!.ConnectionString, "items"))
        {
            using var response = await _host.PostRawAsync("""mutation { items(insert: { name: "late" }) }""", CancellationToken.None);

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await response.Content.ReadAsStringAsync()).Should().Be(HostAnswer);
        }
        _caughtWithResponseStarted.Should().Equal(false);
        (await _host.CountAsync("SELECT count(*) FROM items WHERE name = 'late'")).Should().Be(0);
    }

    [SkippableFact]
    public async Task HookStatement_CancelledByStatementTimeout_RollsTheWriteBack_AndThrowsToTheHost()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        // The write's own table is free; the in-transaction hook's read of hook_side waits.
        await using (await LockAsync(_host!.ConnectionString, "hook_side"))
        {
            using var response = await _host.PostRawAsync("""mutation { plain(insert: { name: "hooked" }) }""", CancellationToken.None);

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }
        _caughtWithResponseStarted.Should().Equal(false);
        (await _host.CountAsync("SELECT count(*) FROM plain")).Should().Be(0);
    }

    [SkippableFact]
    public async Task OtherDatabaseError_StillAnswersAsAGraphQlError_UnderStatus200()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        // Control: the classification is the timeout's alone. A unique violation keeps its
        // GraphQL answer and never reaches the host's handler.
        using var response = await _host!.PostRawAsync("""mutation { items(insert: { name: "seed" }) }""", CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain(BifrostExecutionError.ConflictCode);
        _caughtWithResponseStarted.Should().BeEmpty();
    }

    [SkippableFact]
    public async Task Request_CancelledByItsOwnToken_StillWritesNothing_AndDoesNotThrowIt()
    {
        Skip.If(PostgresGraphQlHost.Master is null, PostgresGraphQlHost.SkipReason);

        // Its own server with no statement_timeout, so only the request's token can end the wait.
        var caught = new ConcurrentQueue<bool>();
        await using var host = await PostgresGraphQlHost.StartAsync(
            "bifrost_statement_cancel", Ddl, Array.Empty<string>(),
            beforeBifrost: app => app.Use(async (context, next) =>
            {
                try { await next(context); }
                catch (BifrostStatementTimeoutException) { caught.Enqueue(context.Response.HasStarted); throw; }
            }));
        PostgresGraphQlHost.AssertNoErrors(await host.PostAsync("{ items { data { id } } }"));

        await using (await LockAsync(host.ConnectionString, "items"))
        {
            using var cancel = new CancellationTokenSource();
            var send = host.PostRawAsync("""mutation { items(insert: { name: "abandoned" }) }""", cancel.Token);
            await WaitForLockWaiterAsync(host.ConnectionString, present: true);
            cancel.Cancel();

            await FluentActions.Awaiting(() => send).Should().ThrowAsync<OperationCanceledException>();
            // The server's statement is cancelled while the lock is still held, so releasing the
            // lock afterwards cannot let the insert through.
            await WaitForLockWaiterAsync(host.ConnectionString, present: false);
        }
        caught.Should().BeEmpty();
        (await host.CountAsync("SELECT count(*) FROM items WHERE name = 'abandoned'")).Should().Be(0);
    }

    private async Task CatchTimeout(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (BifrostStatementTimeoutException)
        {
            _caughtWithResponseStarted.Enqueue(context.Response.HasStarted);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync(HostAnswer);
        }
    }

    /// <summary>Holds an ACCESS EXCLUSIVE lock on <paramref name="table"/> until disposed.</summary>
    private static async Task<IAsyncDisposable> LockAsync(string connectionString, string table)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        var transaction = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand($"LOCK TABLE {table} IN ACCESS EXCLUSIVE MODE", conn, transaction))
            await cmd.ExecuteNonQueryAsync();
        return new HeldLock(conn, transaction);
    }

    /// <summary>
    /// Waits, up to 10 s, until a backend of this database is (or no longer is) waiting on a lock.
    /// </summary>
    private static async Task WaitForLockWaiterAsync(string connectionString, bool present)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", conn);
            var waiting = (long)(await cmd.ExecuteScalarAsync())! > 0;
            if (waiting == present) return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"No lock waiter reached present={present} within 10 s.");
            await Task.Delay(25);
        }
    }

    private sealed class HeldLock(NpgsqlConnection conn, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await transaction.RollbackAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>On a write to <c>plain</c>, reads <c>hook_side</c> on the write's own connection.</summary>
    private sealed class ReadsHookSide : IInTransactionMutationHook
    {
        public bool AppliesTo(IDbTable table) => table.DbName == "plain";

        public async ValueTask AfterWriteInTransactionAsync(MutationObserverContext context)
        {
            await using var cmd = context.Connection!.CreateCommand();
            cmd.Transaction = context.Transaction;
            cmd.CommandText = "SELECT count(*) FROM hook_side";
            await cmd.ExecuteScalarAsync(context.CancellationToken);
        }
    }
}
