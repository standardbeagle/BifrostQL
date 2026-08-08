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

            for (var i = 0; i < 2; i++)
            {
                await client.SendBindAsync($"p{i}", "s");
                await client.SendSyncAsync();
                (await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout)).HasError.Should().BeFalse();
            }

            await client.SendBindAsync("p2", "s");
            await client.SendSyncAsync();
            var refused = await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout);
            refused.ErrorSqlState.Should().Be(PgWireProtocol.SqlStateConfigurationLimitExceeded);

            // The session survives the refusal and the unnamed portal (exempt) still executes.
            await client.SendBindAsync("", "s");
            await client.SendExecuteAsync("");
            await client.SendSyncAsync();
            var unnamed = await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout);
            unnamed.HasError.Should().BeFalse();
            unnamed.CommandTag.Should().Be("SELECT 1");
        }
    }
}
