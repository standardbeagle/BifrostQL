using System.Text.Json;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;
using Npgsql;

namespace BifrostQL.Integration.Test;

/// <summary>
/// Two requests carrying the same operation id run as two real, overlapping transactions on
/// Postgres. The first claims the id and writes; the second's claim waits on the first's
/// uncommitted record, and once the first commits it answers from the record: one effect, one
/// record, two equal replies. A second request whose arguments differ is refused as a reused id;
/// one that follows a rolled-back first claims the id and writes.
///
/// <para>The overlap is forced, not hoped for: the test holds <c>SHARE</c> on the written table,
/// so the first request stops after its claim, inside its transaction; the second is sent only
/// once the first is seen waiting, and the lock is released only once both are.</para>
/// </summary>
public sealed class OperationIdConcurrencyTests : IAsyncLifetime
{
    private const string Ddl =
        """
        CREATE TABLE notes (
            id SERIAL PRIMARY KEY,
            title TEXT NOT NULL);
        CREATE TABLE operation_ids (
            tenant TEXT NOT NULL,
            caller TEXT NOT NULL,
            operation_id TEXT NOT NULL,
            table_name TEXT NOT NULL,
            action TEXT NOT NULL,
            target_key TEXT NOT NULL,
            input_hash TEXT NOT NULL,
            result TEXT NULL,
            created_at TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (tenant, caller, operation_id));
        -- Refuses the first 'flaky' insert only: a sequence advances outside the transaction,
        -- so the rolled-back attempt still counts.
        CREATE SEQUENCE flaky_attempts;
        CREATE FUNCTION refuse_first_flaky() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF NEW.title = 'flaky' AND nextval('flaky_attempts') = 1 THEN
                RAISE EXCEPTION 'first flaky insert refused';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER notes_refuse_first_flaky BEFORE INSERT ON notes
            FOR EACH ROW EXECUTE FUNCTION refuse_first_flaky();
        """;

    private static readonly string[] Metadata =
    {
        ":root { operation-id-store: public.operation_ids }",
        "public.operation_ids { visibility: hidden }",
        "public.notes { operation-id: optional }",
    };

    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(30);

    private PostgresGraphQlHost? _host;

    public async Task InitializeAsync()
    {
        if (PostgresGraphQlHost.Master is null) return;
        _host = await PostgresGraphQlHost.StartAsync("bifrost_operation_id_concurrency", Ddl, Metadata);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Concurrent_duplicates_commit_one_effect_and_replay_it()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);
        const string mutation = """mutation { notes(insert: { title: "once" }, _operationId: "op-race") }""";

        var (first, second) = await RaceAsync(mutation, mutation);

        PostgresGraphQlHost.AssertNoErrors(first);
        PostgresGraphQlHost.AssertNoErrors(second);
        first.RootElement.GetRawText().Should().Be("""{"data":{"notes":1}}""");
        second.RootElement.GetRawText().Should().Be(first.RootElement.GetRawText());
        (await _host!.CountAsync("SELECT count(*) FROM notes")).Should().Be(1);
        (await _host.CountAsync("SELECT count(*) FROM notes WHERE id = 1 AND title = 'once'")).Should().Be(1);
        (await _host.RowsAsync("SELECT operation_id, result FROM operation_ids")).Should().ContainSingle()
            .Which.Should().Equal(new Dictionary<string, string?> { ["operation_id"] = "op-race", ["result"] = "1" });
    }

    [SkippableFact]
    public async Task Concurrent_reuse_with_different_arguments_is_refused_after_the_first_commits()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var (first, second) = await RaceAsync(
            """mutation { notes(insert: { title: "first" }, _operationId: "op-race") }""",
            """mutation { notes(insert: { title: "second" }, _operationId: "op-race") }""");

        PostgresGraphQlHost.AssertNoErrors(first);
        var error = PostgresGraphQlHost.FirstError(second);
        error.GetProperty("extensions").GetProperty("code").GetString().Should().Be("OPERATION_ID_REUSED", second.RootElement.GetRawText());
        error.GetProperty("message").GetString().Should().Be("This operation id was already used for a different write.");
        (await _host!.CountAsync("SELECT count(*) FROM notes")).Should().Be(1);
        (await _host.CountAsync("SELECT count(*) FROM notes WHERE title = 'first'")).Should().Be(1);
        (await _host.CountAsync("SELECT count(*) FROM operation_ids")).Should().Be(1);
    }

    [SkippableFact]
    public async Task Concurrent_duplicate_claims_the_id_when_the_first_rolls_back()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);
        const string mutation = """mutation { notes(insert: { title: "flaky" }, _operationId: "op-race") }""";

        var (first, second) = await RaceAsync(mutation, mutation);

        PostgresGraphQlHost.FirstError(first).GetProperty("extensions").GetProperty("code").GetString()
            .Should().Be("DATABASE_ERROR", first.RootElement.GetRawText());
        PostgresGraphQlHost.AssertNoErrors(second);
        var key = second.RootElement.GetProperty("data").GetProperty("notes").GetRawText();
        (await _host!.CountAsync("SELECT count(*) FROM notes")).Should().Be(1);
        (await _host.CountAsync($"SELECT count(*) FROM notes WHERE id = {key} AND title = 'flaky'")).Should().Be(1);
        (await _host.RowsAsync("SELECT operation_id, result FROM operation_ids")).Should().ContainSingle()
            .Which.Should().Equal(new Dictionary<string, string?> { ["operation_id"] = "op-race", ["result"] = key });
    }

    /// <summary>
    /// Sends <paramref name="firstQuery"/> and, once it is blocked inside its transaction,
    /// <paramref name="secondQuery"/>; releases both once the second is blocked too.
    /// </summary>
    private async Task<(JsonDocument First, JsonDocument Second)> RaceAsync(string firstQuery, string secondQuery)
    {
        // The schema loads on the first request; load it now, so the only backends that can
        // wait on the gate's lock are the two writes.
        var (_, warm) = await _host!.SendAsync("{ notes { data { id } } }");
        PostgresGraphQlHost.AssertNoErrors(warm);

        await using var gate = new NpgsqlConnection(_host.ConnectionString);
        await gate.OpenAsync();
        await using var hold = await gate.BeginTransactionAsync();
        await using (var lockNotes = new NpgsqlCommand("LOCK TABLE notes IN SHARE MODE", gate, hold))
            await lockNotes.ExecuteNonQueryAsync();

        // pg_stat_activity is snapshotted once per transaction, so it is read from its own
        // autocommit connection, never inside the gate's transaction.
        await using var observer = new NpgsqlConnection(_host.ConnectionString);
        await observer.OpenAsync();

        var first = _host.SendAsync(firstQuery, ("X-Test-User", "u-1"));
        await WaitForBlockedBackendsAsync(observer, 1, first);
        var second = _host.SendAsync(secondQuery, ("X-Test-User", "u-1"));
        await WaitForBlockedBackendsAsync(observer, 2, first, second);

        await hold.CommitAsync();
        var replies = await Task.WhenAll(first, second);
        return (replies[0].Body, replies[1].Body);
    }

    /// <summary>
    /// Waits until <paramref name="count"/> backends in this database, other than
    /// <paramref name="observer"/>, are waiting on a lock. A request that finishes first means it was never blocked: the race did not happen,
    /// so the fact fails rather than passing on a sequential run.
    /// </summary>
    private static async Task WaitForBlockedBackendsAsync(NpgsqlConnection observer, int count, params Task[] requests)
    {
        var deadline = DateTime.UtcNow + WaitBudget;
        while (true)
        {
            if (requests.FirstOrDefault(r => r.IsCompleted) is { } finished)
            {
                await finished;
                throw new Xunit.Sdk.XunitException("A request finished before the race was set up.");
            }
            await using var probe = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() " +
                "AND pid <> pg_backend_pid() AND wait_event_type = 'Lock'", observer);
            if (Convert.ToInt64(await probe.ExecuteScalarAsync()) >= count)
                return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"Fewer than {count} requests were blocked within {WaitBudget}. Backends:\n{await DescribeBackendsAsync(observer)}");
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }
    }

    private static async Task<string> DescribeBackendsAsync(NpgsqlConnection observer)
    {
        await using var probe = new NpgsqlCommand(
            "SELECT pid, state, wait_event_type, wait_event, left(query, 160) FROM pg_stat_activity " +
            "WHERE datname = current_database() AND pid <> pg_backend_pid()", observer);
        await using var reader = await probe.ExecuteReaderAsync();
        var lines = new List<string>();
        while (await reader.ReadAsync())
            lines.Add(string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "-" : reader.GetValue(i).ToString())));
        return string.Join("\n", lines);
    }
}
