using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.MySql;
using BifrostQL.Server;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MySqlConnector;

namespace BifrostQL.Integration.Test;

/// <summary>
/// Requests carrying the same operation id run as real, overlapping InnoDB transactions. The
/// first claims the id and then stops before its write; the others' claims wait on its
/// uncommitted record. Once released, every request must answer with the one recorded effect.
///
/// <para>The overlap is forced, not hoped for: the test holds <c>LOCK TABLES notes READ</c>, so
/// the first request stops after its claim, waiting on the table lock inside its transaction;
/// the next request is sent only once every earlier one is seen waiting, and the lock is
/// released only once all are. Each fact repeats its race <see cref="Attempts"/> times with a
/// fresh id.</para>
///
/// <para>When the first claim rolls back, InnoDB grants each waiting duplicate insert a shared
/// lock on the released key, and two waiters that both then need the exclusive lock deadlock:
/// error 1213. That victim must answer like the other retries, not as a database error.</para>
/// </summary>
public sealed class OperationIdMySqlConcurrencyTests : IAsyncLifetime
{
    private const string SkipReason = "BIFROST_TEST_MYSQL environment variable not set";
    private const int Attempts = 10;
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(30);

    private static readonly string[] Ddl =
    {
        "CREATE TABLE notes (id INT AUTO_INCREMENT PRIMARY KEY, title VARCHAR(100) NOT NULL)",
        """
        CREATE TABLE operation_ids (
            tenant VARCHAR(200) NOT NULL,
            caller VARCHAR(200) NOT NULL,
            operation_id VARCHAR(200) NOT NULL,
            table_name VARCHAR(200) NOT NULL,
            action VARCHAR(20) NOT NULL,
            target_key TEXT NOT NULL,
            input_hash VARCHAR(100) NOT NULL,
            result TEXT NULL,
            created_at DATETIME(6) NOT NULL,
            PRIMARY KEY (tenant, caller, operation_id))
        """,
        // Refuses the first insert of each 'flaky-*' title only. MyISAM is not transactional, so
        // the rolled-back attempt still counts.
        "CREATE TABLE flaky_attempts (title VARCHAR(100) NOT NULL) ENGINE=MyISAM",
        """
        CREATE TRIGGER notes_refuse_first_flaky BEFORE INSERT ON notes FOR EACH ROW
        BEGIN
            IF NEW.title LIKE 'flaky-%' THEN
                INSERT INTO flaky_attempts (title) VALUES (NEW.title);
                IF (SELECT COUNT(*) FROM flaky_attempts WHERE title = NEW.title) = 1 THEN
                    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'first flaky insert refused';
                END IF;
            END IF;
        END
        """,
    };

    private static readonly string[] Metadata =
    {
        ":root { operation-id-store: operation_ids }",
        "*.operation_ids { visibility: hidden }",
        "*.notes { operation-id: optional }",
    };

    private string? _master;
    private string _dbName = null!;
    private string _connectionString = null!;
    private IHost? _host;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _master = Environment.GetEnvironmentVariable("BIFROST_TEST_MYSQL");
        if (_master is null) return;
        _dbName = $"bifrost_opid_race_{Guid.NewGuid():N}";
        await ExecAsync(_master, $"CREATE DATABASE `{_dbName}`");
        _connectionString = new MySqlConnectionStringBuilder(_master) { Database = _dbName }.ConnectionString;
        foreach (var statement in Ddl)
            await ExecAsync(_connectionString, statement);

        var settings = new Dictionary<string, string?>
        {
            ["Bifrost:DisableAuth"] = "true",
            ["Bifrost:Path"] = "/graphql",
        };
        for (var i = 0; i < Metadata.Length; i++)
            settings[$"Bifrost:Metadata:{i}"] = Metadata[i];
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        DbConnFactoryResolver.Register(BifrostDbProvider.MySql, cs => new MySqlDbConnFactory(cs));
        _host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddBifrostQL(o => o
                    .BindConfiguration(config.GetSection("Bifrost"))
                    .BindConnectionString(_connectionString)
                    .BindProvider("mysql"));
            });
            web.Configure(app =>
            {
                app.Use((context, next) =>
                {
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, "u-1") }, "test"));
                    return next(context);
                });
                app.UseBifrostQL();
            });
        }).StartAsync();
        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        if (_host is null) return;
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        MySqlConnection.ClearAllPools();
        await ExecAsync(_master!, $"DROP DATABASE IF EXISTS `{_dbName}`");
    }

    [SkippableFact]
    public async Task Concurrent_duplicate_claims_commit_one_effect_and_replay_it()
    {
        Skip.If(_host is null, SkipReason);

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var title = $"once-{attempt}";
            var mutation = $$"""mutation { notes(insert: { title: "{{title}}" }, _operationId: "op-once-{{attempt}}") }""";

            var replies = await RaceAsync(mutation, mutation);

            await AssertNoErrorsAsync(replies, attempt);
            replies[1].RootElement.GetRawText().Should().Be(replies[0].RootElement.GetRawText());
            (await CountAsync($"SELECT count(*) FROM notes WHERE title = '{title}'")).Should().Be(1);
            (await CountAsync($"SELECT count(*) FROM operation_ids WHERE operation_id = 'op-once-{attempt}'")).Should().Be(1);
        }
    }

    [SkippableFact]
    public async Task Duplicates_waiting_on_a_rolled_back_claim_commit_one_effect_and_replay_it()
    {
        Skip.If(_host is null, SkipReason);

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var title = $"flaky-{attempt}";
            var mutation = $$"""mutation { notes(insert: { title: "{{title}}" }, _operationId: "op-flaky-{{attempt}}") }""";

            var replies = await RaceAsync(mutation, mutation, mutation);

            // The first is refused by the trigger and rolls back its claim. Of the two waiting on
            // it, one claims the id and writes; the other answers from that record.
            DatabaseErrorCode(replies[0]).Should().Be("DATABASE_ERROR", replies[0].RootElement.GetRawText());
            await AssertNoErrorsAsync(replies[1..], attempt);
            replies[2].RootElement.GetRawText().Should().Be(replies[1].RootElement.GetRawText());
            (await CountAsync($"SELECT count(*) FROM notes WHERE title = '{title}'")).Should().Be(1);
            (await CountAsync($"SELECT count(*) FROM operation_ids WHERE operation_id = 'op-flaky-{attempt}'")).Should().Be(1);
        }
    }

    /// <summary>
    /// Sends each query in turn, each once every earlier one is blocked inside its transaction,
    /// and releases them all once the last is blocked too.
    /// </summary>
    private async Task<JsonDocument[]> RaceAsync(params string[] queries)
    {
        // The schema loads on the first request; load it now, so the only sessions that can wait
        // are the writes.
        var warm = await SendAsync("{ notes { data { id } } }");
        if (warm.RootElement.TryGetProperty("errors", out _))
            throw new Xunit.Sdk.XunitException("Warm-up failed: " + warm.RootElement.GetRawText());

        await using var gate = new MySqlConnection(_connectionString);
        await gate.OpenAsync();
        await using (var lockNotes = new MySqlCommand("LOCK TABLES notes READ", gate))
            await lockNotes.ExecuteNonQueryAsync();

        await using var observer = new MySqlConnection(_connectionString);
        await observer.OpenAsync();

        var sent = new List<Task<JsonDocument>>();
        try
        {
            foreach (var query in queries)
            {
                sent.Add(SendAsync(query));
                await WaitForBlockedSessionsAsync(observer, sent.Count, sent);
            }
        }
        finally
        {
            await using var unlock = new MySqlCommand("UNLOCK TABLES", gate);
            await unlock.ExecuteNonQueryAsync();
        }
        return await Task.WhenAll(sent);
    }

    /// <summary>
    /// Waits until <paramref name="count"/> sessions in this database wait on a lock: the first
    /// on the gate's table lock, the rest on its uncommitted claim. A request that finishes first
    /// was never blocked, so the race did not happen and the fact fails.
    /// </summary>
    private async Task WaitForBlockedSessionsAsync(MySqlConnection observer, int count, IReadOnlyList<Task<JsonDocument>> requests)
    {
        var deadline = DateTime.UtcNow + WaitBudget;
        while (true)
        {
            if (requests.FirstOrDefault(r => r.IsCompleted) is { } finished)
                throw new Xunit.Sdk.XunitException(
                    "A request finished before the race was set up: " + (await finished).RootElement.GetRawText());
            // The first waits on the gate's table lock; each later one waits on the first's
            // claim row, which the processlist shows as an operation_ids insert in state 'update'.
            await using var probe = new MySqlCommand(
                "SELECT count(*) FROM information_schema.processlist " +
                "WHERE db = DATABASE() AND id <> CONNECTION_ID() AND (state = 'Waiting for table metadata lock' " +
                "OR (state = 'update' AND info LIKE 'INSERT INTO %`operation_ids`%'))", observer);
            if (Convert.ToInt64(await probe.ExecuteScalarAsync()) >= count)
                return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"Fewer than {count} requests were blocked within {WaitBudget}. Sessions:\n{await DescribeSessionsAsync(observer)}");
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }
    }

    private static async Task<string> DescribeSessionsAsync(MySqlConnection observer)
    {
        await using var probe = new MySqlCommand(
            "SELECT id, command, state, left(info, 160) FROM information_schema.processlist " +
            "WHERE db = DATABASE() AND id <> CONNECTION_ID()", observer);
        await using var reader = await probe.ExecuteReaderAsync();
        var lines = new List<string>();
        while (await reader.ReadAsync())
            lines.Add(string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "-" : reader.GetValue(i).ToString())));
        return string.Join("\n", lines);
    }

    private async Task AssertNoErrorsAsync(IEnumerable<JsonDocument> replies, int attempt)
    {
        foreach (var reply in replies)
        {
            if (!reply.RootElement.TryGetProperty("errors", out _)) continue;
            throw new Xunit.Sdk.XunitException(
                $"Attempt {attempt + 1}/{Attempts}: expected no errors: {reply.RootElement.GetRawText()}\n" +
                await LatestDeadlockAsync());
        }
    }

    /// <summary>
    /// The InnoDB monitor's latest detected deadlock, the evidence behind a sanitized
    /// <c>DATABASE_ERROR</c>: the wire withholds the driver's 1213 text.
    /// </summary>
    private async Task<string> LatestDeadlockAsync()
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand("SHOW ENGINE INNODB STATUS", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var status = reader.GetString(2);
        var start = status.IndexOf("LATEST DETECTED DEADLOCK", StringComparison.Ordinal);
        if (start < 0) return "InnoDB reports no deadlock.";
        var end = status.IndexOf("\nTRANSACTIONS\n", start, StringComparison.Ordinal);
        return status[start..(end < 0 ? status.Length : end)];
    }

    private static string? DatabaseErrorCode(JsonDocument reply)
        => reply.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0
            ? errors[0].GetProperty("extensions").GetProperty("code").GetString()
            : null;

    private async Task<JsonDocument> SendAsync(string query)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql");
        request.Content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _client.SendAsync(request);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
