using BifrostQL.Server.Pgwire;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Pgwire
{
    /// <summary>
    /// Per-session prepared-statement / portal admission tests. Named statements and portals are
    /// retained for the life of a connection, so ONE peer can grow server memory without bound by
    /// Parsing/Binding fresh names. Both maps are capped; exceeding a cap is a clean
    /// <c>53400 configuration_limit_exceeded</c> on the offending message and the session SURVIVES
    /// with everything it already holds still usable — never a silent eviction of a name the client
    /// still holds, and never an unbounded map.
    /// </summary>
    public sealed class PgSessionStateLimitTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        private static IReadOnlyList<IReadOnlyDictionary<string, object?>> OneUser() => new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["id"] = 1, ["name"] = "alice", ["active"] = true },
        };

        [Fact]
        public async Task NamedPreparedStatements_BeyondTheCap_AreRefused_AndTheSessionSurvives()
        {
            var executor = PgWireTestHarness.UsersExecutor(OneUser(), out _);
            await using var harness = new PgWireTestHarness(executor, maxPreparedStatements: 3);
            var client = (await harness.OpenSessionAsync()).Client;

            // Fill the cap with three distinct NAMED statements.
            for (var i = 0; i < 3; i++)
            {
                await client.SendParseAsync($"s{i}", "SELECT id FROM users");
                await client.SendSyncAsync();
                var ok = await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout);
                ok.HasError.Should().BeFalse("statement s{0} is within the cap", i);
            }

            // The fourth distinct name is refused — the map does not grow past the cap.
            await client.SendParseAsync("s3", "SELECT id FROM users");
            await client.SendSyncAsync();
            var refused = await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout);
            refused.ErrorSqlState.Should().Be(PgWireProtocol.SqlStateConfigurationLimitExceeded);

            // Re-Parsing an EXISTING name replaces it, so it stays allowed at the cap...
            await client.SendParseAsync("s0", "SELECT id, name FROM users");
            await client.SendSyncAsync();
            (await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout)).HasError.Should().BeFalse();

            // ...and so does the UNNAMED statement, which always replaces and cannot accumulate.
            await client.SendParseAsync("", "SELECT id FROM users");
            await client.SendBindAsync("", "");
            await client.SendExecuteAsync("");
            await client.SendSyncAsync();
            var unnamed = await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout);
            unnamed.HasError.Should().BeFalse("the unnamed statement is exempt: it replaces rather than accumulating");
            unnamed.CommandTag.Should().Be("SELECT 1");

            // Freeing a slot with Close('S') makes room for a genuinely new name again.
            await client.SendCloseStatementAsync("s1");
            await client.SendParseAsync("s3", "SELECT id FROM users");
            await client.SendSyncAsync();
            (await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout)).HasError.Should().BeFalse();
        }

        [Fact]
        public async Task NamedPortals_BeyondTheCap_AreRefused_AndTheUnnamedPortalStillWorks()
        {
            var executor = PgWireTestHarness.UsersExecutor(OneUser(), out _);
            await using var harness = new PgWireTestHarness(executor, maxPortals: 2);
            var client = (await harness.OpenSessionAsync()).Client;

            await client.SendParseAsync("s", "SELECT id FROM users");
            await client.SendSyncAsync();
            (await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout)).ParseComplete.Should().BeTrue();

            // Portals close at Sync, so the cap binds within ONE Sync batch.
            await client.SendBindAsync("p0", "s");
            await client.SendBindAsync("p1", "s");
            await client.SendBindAsync("p2", "s");
            await client.SendSyncAsync();
            var refused = await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout);
            refused.MessageOrder.Count(t => t == PgWireProtocol.BindComplete).Should().Be(2);
            refused.ErrorSqlState.Should().Be(PgWireProtocol.SqlStateConfigurationLimitExceeded);

            // The session survives the refusal and the unnamed portal (exempt) still executes.
            await client.SendBindAsync("", "s");
            await client.SendExecuteAsync("");
            await client.SendSyncAsync();
            var unnamed = await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout);
            unnamed.HasError.Should().BeFalse();
            unnamed.CommandTag.Should().Be("SELECT 1");
        }

        // ---- byte budgets and portal lifetime ----------------------------------

        private static IReadOnlyList<IReadOnlyDictionary<string, object?>> WideUsers(int count, int nameLength)
            => Enumerable.Range(1, count)
                .Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["id"] = i, ["name"] = new string('n', nameLength), ["active"] = true,
                })
                .ToList();

        [Fact]
        public async Task A_portal_does_not_survive_Sync()
        {
            var executor = PgWireTestHarness.UsersExecutor(WideUsers(3, 4), out _);
            await using var harness = new PgWireTestHarness(executor);
            var client = (await harness.OpenSessionAsync()).Client;

            await client.SendParseAsync("s", "SELECT id FROM users");
            await client.SendBindAsync("p", "s");
            await client.SendExecuteAsync("p", maxRows: 1);
            // Control: inside the batch the suspended portal resumes.
            await client.SendExecuteAsync("p", maxRows: 1);
            await client.SendSyncAsync();
            var batch = await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout);
            batch.HasError.Should().BeFalse();
            batch.Rows.Should().HaveCount(2);

            // Sync ended the implicit transaction, which closes every portal (pg protocol).
            await client.SendExecuteAsync("p", maxRows: 1);
            await client.SendSyncAsync();
            var after = await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout);
            after.Rows.Should().BeEmpty("a closed portal must not keep serving its cached rows");
            after.ErrorSqlState.Should().Be(PgWireProtocol.SqlStateInvalidSqlStatementName);

            // The named statement is session state, not transaction state: it survives Sync.
            await client.SendBindAsync("p", "s");
            await client.SendExecuteAsync("p");
            await client.SendSyncAsync();
            (await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout)).CommandTag.Should().Be("SELECT 3");
        }

        [Fact]
        public async Task A_simple_query_closes_open_portals()
        {
            var executor = PgWireTestHarness.UsersExecutor(WideUsers(3, 4), out _);
            await using var harness = new PgWireTestHarness(executor);
            var client = (await harness.OpenSessionAsync()).Client;

            await client.SendParseAsync("s", "SELECT id FROM users");
            await client.SendBindAsync("p", "s");
            await client.SendExecuteAsync("p", maxRows: 1);
            await client.SendFlushAsync();
            await client.SendQueryAsync("SELECT id FROM users");
            await client.SendExecuteAsync("p", maxRows: 1);
            await client.SendSyncAsync();

            // Execute(p, 1) -> one row + PortalSuspended, then the simple query's own cycle.
            var first = await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout);
            first.PortalSuspended.Should().BeTrue();
            first.Rows.Should().HaveCount(1 + 3);
            var after = await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout);
            after.Rows.Should().BeEmpty("the simple query's implicit transaction closed the portal");
            after.ErrorSqlState.Should().Be(PgWireProtocol.SqlStateInvalidSqlStatementName);
        }

        [Fact]
        public async Task Suspended_portals_are_capped_by_retained_bytes_and_draining_frees_the_budget()
        {
            // 10 rows of ~1 KiB: a portal suspended after one row retains ~9 KiB. The budget fits
            // one such portal but not two, so the second refusal is the SUM binding, not one portal.
            var executor = PgWireTestHarness.UsersExecutor(WideUsers(10, 1000), out _);
            await using var harness = new PgWireTestHarness(executor, maxSessionPortalBytes: 15_000);
            var client = (await harness.OpenSessionAsync()).Client;

            await client.SendParseAsync("s", "SELECT id, name FROM users");
            await client.SendBindAsync("p0", "s");
            await client.SendExecuteAsync("p0", maxRows: 1);
            await client.SendBindAsync("p1", "s");
            await client.SendExecuteAsync("p1", maxRows: 1);
            await client.SendSyncAsync();
            var overBudget = await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout);

            overBudget.PortalSuspended.Should().BeTrue("the first portal fits the budget alone");
            overBudget.Rows.Should().HaveCount(1, "the refused suspension sends no row at all");
            overBudget.ErrorSqlState.Should().Be(PgWireProtocol.SqlStateConfigurationLimitExceeded);

            // Draining the first portal returns its bytes, so the second now fits.
            await client.SendBindAsync("p0", "s");
            await client.SendExecuteAsync("p0", maxRows: 1);
            await client.SendExecuteAsync("p0");
            await client.SendBindAsync("p1", "s");
            await client.SendExecuteAsync("p1", maxRows: 1);
            await client.SendSyncAsync();
            var drained = await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout);
            drained.HasError.Should().BeFalse();
            drained.Rows.Should().HaveCount(10 + 1);
        }

        [Fact]
        public async Task Named_statements_are_capped_by_total_SQL_bytes()
        {
            var executor = PgWireTestHarness.UsersExecutor(OneUser(), out _);
            var sql = "SELECT id FROM users WHERE id = 1 OR id = 2 OR id = 3"; // 52 bytes
            await using var harness = new PgWireTestHarness(executor, maxSessionStatementBytes: 100);
            var client = (await harness.OpenSessionAsync()).Client;

            await client.SendParseAsync("s0", sql);
            await client.SendSyncAsync();
            (await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout)).HasError.Should().BeFalse();

            // Two statements are well under the count cap but over the byte budget together.
            await client.SendParseAsync("s1", sql);
            await client.SendSyncAsync();
            (await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout))
                .ErrorSqlState.Should().Be(PgWireProtocol.SqlStateConfigurationLimitExceeded);

            // The unnamed statement is exempt even past the budget...
            await client.SendParseAsync("", sql + " OR id = 4 OR id = 5 OR id = 6 OR id = 7 OR id = 8");
            await client.SendSyncAsync();
            (await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout)).HasError.Should().BeFalse();

            // ...and Close('S') returns the bytes.
            await client.SendCloseStatementAsync("s0");
            await client.SendParseAsync("s1", sql);
            await client.SendSyncAsync();
            (await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout)).HasError.Should().BeFalse();
        }
    }
}
