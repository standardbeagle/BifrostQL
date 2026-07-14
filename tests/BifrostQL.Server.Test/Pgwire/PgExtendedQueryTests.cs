using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Pgwire;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Pgwire
{
    /// <summary>
    /// Extended query protocol (Parse/Bind/Describe/Execute/Sync/Close) tests for slice 5,
    /// driven end to end over a loopback socket through a real authenticated handshake. The
    /// read boundary (<see cref="IQueryIntentExecutor"/>) is mocked so the tests pin the wire
    /// behavior and — crucially — that a bound <c>$N</c> value reaches the GqlObjectQuery
    /// filter as DATA, while the real translator/parser/encoders run unmocked.
    /// </summary>
    public sealed class PgExtendedQueryTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        private static IReadOnlyList<IReadOnlyDictionary<string, object?>> TwoUsers() => new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["id"] = 1, ["name"] = "alice", ["active"] = true },
            new Dictionary<string, object?> { ["id"] = 2, ["name"] = "bob", ["active"] = false },
        };

        [Fact]
        public async Task ParseBindExecute_RoundTrips_AndBoundValueReachesFilterAsData()
        {
            var executor = PgWireTestHarness.UsersExecutor(TwoUsers(), out var captured);
            await using var harness = new PgWireTestHarness(executor);
            var client = (await harness.OpenSessionAsync()).Client;

            // Parse a parameterized statement, bind $1 = 5, describe the portal, execute, sync.
            await client.SendParseAsync("", "SELECT id, name FROM users WHERE id = $1", PgTypeMap.OidInt4);
            await client.SendBindAsync("", "", "5");
            await client.SendDescribePortalAsync("");
            await client.SendExecuteAsync("");
            await client.SendSyncAsync();

            var result = await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout);

            // The message sequence a driver expects.
            result.HasError.Should().BeFalse();
            result.ParseComplete.Should().BeTrue();
            result.BindComplete.Should().BeTrue();
            result.Fields.Select(f => f.Name).Should().Equal("id", "name");
            result.Fields.Select(f => f.TypeOid).Should().Equal(PgTypeMap.OidInt4, PgTypeMap.OidVarchar);
            result.CommandTag.Should().Be("SELECT 2");
            result.TransactionStatus.Should().Be('I');
            result.MessageOrder.Should().ContainInOrder(
                PgWireProtocol.ParseComplete, PgWireProtocol.BindComplete,
                PgWireProtocol.RowDescription, PgWireProtocol.CommandComplete, PgWireProtocol.ReadyForQuery);

            // The bound $1 must reach the intent's filter as a typed DATA value (long 5 for an
            // int4 parameter), never string-concatenated into SQL.
            captured.Intent!.Query.Filter.Should().NotBeNull();
            PgWireTestHarness.CollectFilterValues(captured.Intent.Query.Filter).Should().Contain(5L);
        }

        [Fact]
        public async Task MultipleBindExecute_OnOnePreparedStatement_UseDifferentParamValues()
        {
            var executor = PgWireTestHarness.UsersExecutor(TwoUsers(), out var captured);
            await using var harness = new PgWireTestHarness(executor);
            var client = (await harness.OpenSessionAsync()).Client;

            await client.SendParseAsync("stmt", "SELECT id FROM users WHERE id = $1", PgTypeMap.OidInt4);
            await client.SendSyncAsync();
            (await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout)).ParseComplete.Should().BeTrue();

            // First execution: $1 = 5.
            await client.SendBindAsync("", "stmt", "5");
            await client.SendExecuteAsync("");
            await client.SendSyncAsync();
            var first = await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout);
            first.HasError.Should().BeFalse();
            PgWireTestHarness.CollectFilterValues(captured.Intent!.Query.Filter).Should().Contain(5L);

            // Re-bind the SAME statement with $1 = 42 and execute again.
            await client.SendBindAsync("", "stmt", "42");
            await client.SendExecuteAsync("");
            await client.SendSyncAsync();
            var second = await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout);
            second.HasError.Should().BeFalse();
            PgWireTestHarness.CollectFilterValues(captured.Intent!.Query.Filter).Should().Contain(42L);

            captured.ExecuteCount.Should().Be(2);
        }

        [Fact]
        public async Task DescribeStatement_ReturnsParameterDescription_AndRowDescription()
        {
            var executor = PgWireTestHarness.UsersExecutor(TwoUsers(), out _);
            await using var harness = new PgWireTestHarness(executor);
            var client = (await harness.OpenSessionAsync()).Client;

            await client.SendParseAsync("s", "SELECT id, name FROM users WHERE id = $1", PgTypeMap.OidInt4);
            await client.SendDescribeStatementAsync("s");
            await client.SendSyncAsync();

            var result = await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout);

            result.HasError.Should().BeFalse();
            result.ParameterTypeOids.Should().Equal(PgTypeMap.OidInt4); // the one declared $1 type
            result.Fields.Select(f => f.Name).Should().Equal("id", "name");
            result.MessageOrder.Should().ContainInOrder(
                PgWireProtocol.ParameterDescription, PgWireProtocol.RowDescription, PgWireProtocol.ReadyForQuery);
        }

        [Fact]
        public async Task ErrorMidSequence_SkipsUntilSync_ThenReadyForQuery_AndSessionSurvives()
        {
            var executor = PgWireTestHarness.UsersExecutor(TwoUsers(), out _);
            await using var harness = new PgWireTestHarness(executor);
            var client = (await harness.OpenSessionAsync()).Client;

            // Parse+Bind succeed (translation is deferred); Describe translates and fails on the
            // unknown relation → ErrorResponse. The following Execute MUST be ignored until Sync.
            await client.SendParseAsync("", "SELECT * FROM nonexistent WHERE id = $1", PgTypeMap.OidInt4);
            await client.SendBindAsync("", "", "5");
            await client.SendDescribePortalAsync("");
            await client.SendExecuteAsync(""); // discarded during skip-until-Sync
            await client.SendSyncAsync();

            var result = await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout);

            result.ParseComplete.Should().BeTrue();
            result.BindComplete.Should().BeTrue();
            result.HasError.Should().BeTrue();
            result.ErrorSqlState.Should().Be(PgWireProtocol.SqlStateSyntaxError);
            result.Rows.Should().BeEmpty();          // the skipped Execute produced nothing
            result.CommandTag.Should().BeNull();
            result.TransactionStatus.Should().Be('I');

            // The session survives: a subsequent simple query round-trips.
            await client.SendQueryAsync("SELECT id FROM users");
            var ok = await client.ReadQueryResultAsync().WaitAsync(Timeout);
            ok.HasError.Should().BeFalse();
            ok.Rows.Should().HaveCount(2);
        }

        [Fact]
        public async Task PreparedCatalogQuery_RoutesThroughCatalogResponder_OverExtendedPath()
        {
            var executor = PgWireTestHarness.UsersExecutor(TwoUsers(), out _);
            await using var harness = new PgWireTestHarness(executor);
            var client = (await harness.OpenSessionAsync()).Client;

            // A no-parameter introspection query prepared via the extended protocol must be
            // answered by the SAME catalog responder the simple path uses.
            await client.SendParseAsync("", "SELECT table_name FROM information_schema.tables");
            await client.SendBindAsync("", "");
            await client.SendDescribePortalAsync("");
            await client.SendExecuteAsync("");
            await client.SendSyncAsync();

            var result = await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout);

            result.HasError.Should().BeFalse();
            result.Fields.Select(f => f.Name).Should().Contain("table_name");
            result.Rows.SelectMany(r => r).Should().Contain("users");
        }

        [Fact]
        public async Task BoundParameter_WithSqlInjectionPayload_IsTreatedAsLiteralData()
        {
            var executor = PgWireTestHarness.UsersExecutor(TwoUsers(), out var captured);
            await using var harness = new PgWireTestHarness(executor);
            var client = (await harness.OpenSessionAsync()).Client;

            const string payload = "'; DROP TABLE users; --";
            await client.SendParseAsync("", "SELECT id, name FROM users WHERE name = $1", PgTypeMap.OidText);
            await client.SendBindAsync("", "", payload);
            await client.SendExecuteAsync("");
            await client.SendSyncAsync();

            var result = await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout);

            // The payload never parses as SQL: the exchange completes normally and the exact
            // string reaches the filter as a bound DATA value.
            result.HasError.Should().BeFalse();
            result.CommandTag.Should().Be("SELECT 2");
            captured.Intent!.Query.Filter.Should().NotBeNull();
            PgWireTestHarness.CollectFilterValues(captured.Intent.Query.Filter).Should().Contain(payload);
        }

        [Fact]
        public async Task BinaryResultFormat_IsRejectedHonestly_WithFeatureNotSupported()
        {
            var executor = PgWireTestHarness.UsersExecutor(TwoUsers(), out _);
            await using var harness = new PgWireTestHarness(executor);
            var client = (await harness.OpenSessionAsync()).Client;

            // Slice 5 is text-format only; a binary result request is refused cleanly, not
            // silently misinterpreted.
            await client.SendParseAsync("", "SELECT id FROM users");
            await client.SendBindBinaryResultAsync("", "");
            await client.SendSyncAsync();

            var result = await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout);

            result.HasError.Should().BeTrue();
            result.ErrorSqlState.Should().Be(PgWireProtocol.SqlStateFeatureNotSupported);
            result.TransactionStatus.Should().Be('I');
        }

        [Fact]
        public async Task BindOutOfRangeNumericParam_YieldsCleanError_SessionSurvives_NoLeak()
        {
            var executor = PgWireTestHarness.UsersExecutor(TwoUsers(), out _);
            await using var harness = new PgWireTestHarness(executor);
            var client = (await harness.OpenSessionAsync()).Client;

            // $1 declared int8 but the bound text value overflows long.Parse. This must NOT
            // escape the bind loop as an unhandled OverflowException (which would drop the
            // connection with no ErrorResponse); it must yield a clean bind error + skip-to-Sync.
            const string overflow = "99999999999999999999999999999";
            await client.SendParseAsync("", "SELECT id FROM users WHERE id = $1", PgTypeMap.OidInt8);
            await client.SendBindAsync("", "", overflow);
            await client.SendExecuteAsync(""); // discarded during skip-until-Sync
            await client.SendSyncAsync();

            var result = await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout);

            // Clean bind ErrorResponse — not a dropped connection / unhandled throw.
            result.HasError.Should().BeTrue();
            result.ErrorSqlState.Should().Be(PgWireProtocol.SqlStateProtocolViolation);
            result.BindComplete.Should().BeFalse();
            result.TransactionStatus.Should().Be('I');

            // No leak: neither the raw value nor exception text ("Overflow") reaches the wire.
            result.ErrorMessage.Should().NotContain(overflow);
            result.ErrorMessage.Should().NotContain("Overflow");

            // The session survives: a follow-up valid Parse/Bind/Execute on the SAME connection
            // round-trips normally.
            await client.SendParseAsync("", "SELECT id FROM users WHERE id = $1", PgTypeMap.OidInt8);
            await client.SendBindAsync("", "", "5");
            await client.SendExecuteAsync("");
            await client.SendSyncAsync();

            var second = await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout);
            second.HasError.Should().BeFalse();
            second.BindComplete.Should().BeTrue();
            second.CommandTag.Should().Be("SELECT 2");
        }

        [Fact]
        public async Task BindMalformedNumericParam_HitsSameCleanBindErrorPath()
        {
            var executor = PgWireTestHarness.UsersExecutor(TwoUsers(), out _);
            await using var harness = new PgWireTestHarness(executor);
            var client = (await harness.OpenSessionAsync()).Client;

            // A non-numeric text value for a declared int8 OID raises FormatException from
            // long.Parse and must take the same clean bind-error path, not crash the session.
            const string garbage = "not-a-number";
            await client.SendParseAsync("", "SELECT id FROM users WHERE id = $1", PgTypeMap.OidInt8);
            await client.SendBindAsync("", "", garbage);
            await client.SendExecuteAsync("");
            await client.SendSyncAsync();

            var result = await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout);

            result.HasError.Should().BeTrue();
            result.ErrorSqlState.Should().Be(PgWireProtocol.SqlStateProtocolViolation);
            result.BindComplete.Should().BeFalse();
            result.TransactionStatus.Should().Be('I');
            result.ErrorMessage.Should().NotContain(garbage);
        }
    }
}
