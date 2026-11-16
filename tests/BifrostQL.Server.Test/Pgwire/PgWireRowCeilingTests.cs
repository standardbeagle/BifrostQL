using System.Globalization;
using System.Security.Claims;
using BifrostQL.Core.Model;
using BifrostQL.Server.Pgwire;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Pgwire
{
    /// <summary>
    /// The pgwire read path against the model's <c>max-query-rows</c> ceiling, end to end over
    /// a real SQLite pipeline. SQL with no LIMIT means every row: the wire returns all of them
    /// when they fit under the ceiling and refuses with <c>54000 program_limit_exceeded</c>
    /// when they don't. It never answers a short result with a plausible <c>SELECT n</c> tag
    /// (the old behaviour: 100 rows for an unbounded SELECT, the ceiling for an over-ceiling
    /// LIMIT). Both the simple (<c>Q</c>) and extended (<c>Parse/Bind/Execute</c>) paths are
    /// driven, because each executes the read on its own.
    ///
    /// <para>The ceiling (200) sits above the old 100-row default so the "fits" facts can tell
    /// "every row" from "the default window": <c>big</c> has more rows than the ceiling,
    /// <c>small</c> fewer than the ceiling but more than 100.</para>
    /// </summary>
    public sealed class PgWireRowCeilingTests : IAsyncLifetime
    {
        private const int Ceiling = 200;
        private const int BigRows = 250;
        private const int SmallRows = 150;
        private const string CeilingMessage = "result exceeds the server row limit; add a LIMIT clause";
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        private static readonly string[] MetadataRules =
        {
            $":root {{ {MetadataKeys.Model.MaxQueryRows}: {Ceiling} }}",
        };

        private static readonly string[] SeedSql =
        {
            "DROP TABLE IF EXISTS big",
            "DROP TABLE IF EXISTS small",
            "CREATE TABLE big (id INTEGER PRIMARY KEY, label TEXT NOT NULL)",
            "CREATE TABLE small (id INTEGER PRIMARY KEY, label TEXT NOT NULL)",
            $"INSERT INTO big(id, label) WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {BigRows}) SELECT i, 'b' || i FROM n",
            $"INSERT INTO small(id, label) WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {SmallRows}) SELECT i, 's' || i FROM n",
        };

        private static readonly ClaimsPrincipal Caller = new(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "user-a") }, authenticationType: "pgwire"));

        private PgWireRealDbHarness _harness = null!;

        public async Task InitializeAsync()
            => _harness = await PgWireRealDbHarness.StartAsync(nameof(PgWireRowCeilingTests), MetadataRules, SeedSql);

        public async Task DisposeAsync() => await _harness.DisposeAsync();

        // ---- simple query ----------------------------------------------------

        [Fact]
        public async Task Simple_NoLimit_OverCeiling_RefusesWith54000_AndSendsNoRows()
        {
            var result = await _harness.QueryAsync(Caller, "SELECT * FROM big");

            result.ErrorSqlState.Should().Be("54000");
            result.ErrorMessage.Should().Be(CeilingMessage);
            result.Rows.Should().BeEmpty("the ceiling check runs before any DataRow is written");
            result.CommandTag.Should().BeNull();
            result.TransactionStatus.Should().Be('I', "a refused read leaves the session usable");
        }

        [Fact]
        public async Task Simple_NoLimit_UnderCeiling_ReturnsEveryRow()
        {
            var result = await _harness.QueryAsync(Caller, "SELECT id FROM small");

            result.HasError.Should().BeFalse();
            result.Rows.Should().HaveCount(SmallRows, "SQL without LIMIT means every row, not a 100-row default window");
            result.CommandTag.Should().Be($"SELECT {SmallRows}");
        }

        [Fact]
        public async Task Simple_LimitUnderCeiling_OnBiggerTable_ReturnsExactlyN()
        {
            var result = await _harness.QueryAsync(Caller, "SELECT id FROM big ORDER BY id LIMIT 50");

            result.HasError.Should().BeFalse("the statement's own LIMIT chose the page, not the ceiling");
            result.Rows.Should().HaveCount(50, "the sentinel row fetched past the page must be dropped");
            result.Rows[^1][0].Should().Be("50");
            result.CommandTag.Should().Be("SELECT 50");
        }

        [Fact]
        public async Task Simple_LimitAboveCeiling_OverCeilingRows_RefusesWith54000()
        {
            var result = await _harness.QueryAsync(Caller, "SELECT id FROM big LIMIT 5000");

            result.ErrorSqlState.Should().Be("54000", "a LIMIT above the ceiling must not be cut to the ceiling silently");
            result.ErrorMessage.Should().Be(CeilingMessage);
            result.Rows.Should().BeEmpty();
        }

        // ---- extended query --------------------------------------------------

        [Fact]
        public async Task Extended_NoLimit_OverCeiling_RefusesWith54000_AndSendsNoRows()
        {
            var result = await ExecuteExtendedAsync("SELECT * FROM big", maxRows: 0);

            result.ErrorSqlState.Should().Be("54000");
            result.ErrorMessage.Should().Be(CeilingMessage);
            result.MessageOrder.Should().NotContain(PgWireProtocol.DataRow);
            result.CommandTag.Should().BeNull();
        }

        [Fact]
        public async Task Extended_NoLimit_UnderCeiling_ReturnsEveryRow()
        {
            var result = await ExecuteExtendedAsync("SELECT id FROM small", maxRows: 0);

            result.HasError.Should().BeFalse();
            result.Rows.Should().HaveCount(SmallRows, "SQL without LIMIT means every row, not a 100-row default window");
            result.CommandTag.Should().Be($"SELECT {SmallRows}");
        }

        [Fact]
        public async Task Extended_LimitUnderCeiling_OnBiggerTable_ReturnsExactlyN()
        {
            var result = await ExecuteExtendedAsync("SELECT id FROM big ORDER BY id LIMIT 50", maxRows: 0);

            result.HasError.Should().BeFalse("the statement's own LIMIT chose the page, not the ceiling");
            result.Rows.Should().HaveCount(50, "the sentinel row fetched past the page must be dropped");
            result.CommandTag.Should().Be("SELECT 50");
        }

        [Fact]
        public async Task Extended_MaxRows_OverCeiling_RefusesOnFirstExecute_BeforeAnyRow()
        {
            // A row-limited Execute (max_rows) pages the result; the ceiling check still runs
            // on the whole materialized result first, so no partial page leaks out.
            var result = await ExecuteExtendedAsync("SELECT id FROM big", maxRows: 10);

            result.ErrorSqlState.Should().Be("54000");
            result.MessageOrder.Should().NotContain(PgWireProtocol.DataRow);
            result.PortalSuspended.Should().BeFalse();
        }

        [Fact]
        public async Task Extended_MaxRows_UnderCeiling_SuspendsThenResumesOverEveryRow()
        {
            await using var frontDoor = _harness.OpenFrontDoor();
            var client = (await frontDoor.OpenSessionAsync()).Client;

            await client.SendParseAsync("", "SELECT id FROM small ORDER BY id");
            await client.SendBindAsync("p", "");
            await client.SendExecuteAsync("p", maxRows: 100);
            await client.SendSyncAsync();
            var first = await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout);

            first.HasError.Should().BeFalse();
            first.Rows.Should().HaveCount(100);
            first.PortalSuspended.Should().BeTrue("rows remain past the first max_rows page");

            await client.SendExecuteAsync("p", maxRows: 100);
            await client.SendSyncAsync();
            var second = await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout);

            second.HasError.Should().BeFalse();
            second.Rows.Should().HaveCount(SmallRows - 100);
            second.Rows[^1][0].Should().Be(SmallRows.ToString(CultureInfo.InvariantCulture));
            second.CommandTag.Should().Be($"SELECT {SmallRows - 100}");
        }

        private async Task<ExtendedResult> ExecuteExtendedAsync(string sql, int maxRows)
        {
            await using var frontDoor = _harness.OpenFrontDoor();
            var client = (await frontDoor.OpenSessionAsync()).Client;

            await client.SendParseAsync("", sql);
            await client.SendBindAsync("", "");
            await client.SendExecuteAsync("", maxRows);
            await client.SendSyncAsync();
            return await client.ReadExtendedUntilReadyAsync().WaitAsync(Timeout);
        }
    }
}
