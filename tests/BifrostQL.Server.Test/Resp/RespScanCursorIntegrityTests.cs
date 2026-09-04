using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Resp;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test.Resp
{
    /// <summary>
    /// The SCAN cursor must be integrity-protected. It was plain Base64 of a JSON array, so any
    /// caller could mint one: hand a table a position of its choosing, or resume another principal's
    /// iteration. Containment of the ROWS never depended on it — the transformer pipeline ANDs
    /// tenant, soft-delete and policy predicates onto every page regardless — but an unauthenticated
    /// token still lets a client page one table and swap in another mid-sequence, and it is the one
    /// continuation surface in the adapter set without a MAC (LDAP's paged-results cookie, OData's
    /// $skiptoken and the gRPC page token all carry one).
    ///
    /// <para>Forgery, tampering, cross-context replay and expiry must all produce ONE outcome, and
    /// an unusable cursor must be refused EXPLICITLY — treating it as "start from the top" would
    /// turn a tampered cursor into a silent full re-scan.</para>
    /// </summary>
    public sealed class RespScanCursorIntegrityTests
    {
        [Fact]
        public async Task A_hand_minted_cursor_is_refused_and_executes_nothing()
        {
            var (executor, services) = Arrange();

            // The old cursor format: Base64 of a JSON array of primary-key segments. Anyone can
            // write one, which is the whole finding.
            var forged = Convert.ToBase64String(Encoding.UTF8.GetBytes("[\"3\"]"));

            var reply = await Handle(services, "SCAN", forged, "MATCH", "widgets:*");

            reply.Should().BeOfType<RespError>().Which.Message.Should().Contain("cursor");
            executor.Intents.Should().BeEmpty(
                "an unauthenticated cursor must be refused before anything is executed — and refused "
                + "explicitly, never silently restarted from the top");
        }

        [Fact]
        public async Task A_tampered_cursor_is_refused_and_executes_nothing()
        {
            var (executor, services) = Arrange();

            var page = await ScanOnceAsync(services, RespProtocol.ScanStartCursor, count: "2");
            page.Cursor.Should().NotBe(RespProtocol.ScanStartCursor);
            var tampered = Tamper(page.Cursor);
            executor.Intents.Clear();

            var reply = await Handle(services, "SCAN", tampered, "MATCH", "widgets:*");

            reply.Should().BeOfType<RespError>().Which.Message.Should().Contain("cursor");
            executor.Intents.Should().BeEmpty();
        }

        [Fact]
        public async Task A_cursor_replayed_against_another_table_is_refused()
        {
            var (executor, services) = Arrange();

            var page = await ScanOnceAsync(services, RespProtocol.ScanStartCursor, count: "2");
            executor.Intents.Clear();

            // Same caller, same cursor, different table — and deliberately a table with the SAME
            // primary-key shape, so the segment-arity check cannot refuse it first and stand in for
            // the guard under test. Only the binding folded into the MAC can catch this.
            var reply = await Handle(services, "SCAN", page.Cursor, "MATCH", "gadgets:*");

            reply.Should().BeOfType<RespError>().Which.Message.Should().Contain("cursor");
            executor.Intents.Should().BeEmpty();
        }

        [Fact]
        public async Task A_cursor_replayed_by_another_identity_is_refused()
        {
            var (_, services) = Arrange();
            var page = await ScanOnceAsync(services, RespProtocol.ScanStartCursor, count: "2");

            // A different principal, sharing the front door's key, presents the first caller's
            // cursor. The identity fingerprint is folded into the MAC, so it does not validate.
            var other = new RespSession(2);
            other.Authenticate(new Dictionary<string, object?> { ["tenantId"] = 2 });
            Session = other;

            var reply = await Handle(services, "SCAN", page.Cursor, "MATCH", "widgets:*");

            reply.Should().BeOfType<RespError>().Which.Message.Should().Contain("cursor");
        }

        [Fact]
        public async Task An_expired_cursor_is_refused_exactly_like_a_forged_one()
        {
            var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var (_, services) = Arrange(() => now, ttl: TimeSpan.FromMinutes(10));

            var page = await ScanOnceAsync(services, RespProtocol.ScanStartCursor, count: "2");

            // Authentic, but stale. Same refusal as a forgery: no oracle separating "expired" from
            // "never valid".
            now += TimeSpan.FromMinutes(11);

            var reply = await Handle(services, "SCAN", page.Cursor, "MATCH", "widgets:*");

            reply.Should().BeOfType<RespError>().Which.Message.Should().Contain("cursor");
        }

        [Fact]
        public async Task A_cursor_the_server_issued_still_resumes_the_iteration()
        {
            // The guard must not break paging: the whole visible set is still enumerated across
            // pages, with no duplicate and no omission.
            var (_, services) = Arrange();

            var keys = new List<string>();
            var cursor = RespProtocol.ScanStartCursor;
            do
            {
                var page = await ScanOnceAsync(services, cursor, count: "2");
                keys.AddRange(page.Keys);
                cursor = page.Cursor;
            } while (cursor != RespProtocol.ScanStartCursor);

            keys.Should().Equal("widgets:1", "widgets:2", "widgets:4");
        }

        /// <summary>
        /// Flips one character of the SIGNATURE, leaving a perfectly well-formed payload behind. The
        /// integrity check is then the only thing that can refuse this cursor — tampering the payload
        /// instead would also break its parse, and the test would pass without proving the MAC runs.
        /// </summary>
        private static string Tamper(string cursor)
        {
            var dot = cursor.IndexOf('.');
            var mac = cursor[(dot + 1)..].ToCharArray();
            mac[0] = mac[0] == 'A' ? 'B' : 'A';
            return cursor[..(dot + 1)] + new string(mac);
        }

        // ---- fixtures --------------------------------------------------------

        private static (FakeScanExecutor Executor, IServiceProvider Services) Arrange(
            Func<DateTimeOffset>? clock = null, TimeSpan? ttl = null)
        {
            var executor = new FakeScanExecutor(BuildModel());
            var services = new ServiceCollection()
                .AddSingleton<IQueryIntentExecutor>(executor)
                .AddSingleton(new RespScanCursorKey(
                    Encoding.UTF8.GetBytes("resp-scan-cursor-test-secret"),
                    ttl ?? TimeSpan.FromMinutes(10),
                    clock))
                .BuildServiceProvider();
            Session = new RespSession(1);
            Session.Authenticate(new Dictionary<string, object?> { ["tenantId"] = 1 });
            return (executor, services);
        }

        private static RespSession Session = new(0);

        private static IDbModel BuildModel()
        {
            var widgets = FakeTable("widgets",
                Col("id", "int", 1, pk: true),
                Col("name", "varchar", 2),
                Col("tenant_id", "int", 3));

            // Same PK shape as widgets on purpose — see the cross-table test.
            var gadgets = FakeTable("gadgets",
                Col("id", "int", 1, pk: true),
                Col("name", "varchar", 2),
                Col("tenant_id", "int", 3));

            var model = Substitute.For<IDbModel>();
            model.Tables.Returns(new[] { widgets, gadgets });
            return model;
        }

        private static ColumnDto Col(string name, string type, int ordinal, bool pk = false) =>
            new() { ColumnName = name, GraphQlName = name, DataType = type, OrdinalPosition = ordinal, IsPrimaryKey = pk };

        private static IDbTable FakeTable(string name, params ColumnDto[] columns)
        {
            var table = Substitute.For<IDbTable>();
            table.DbName.Returns(name);
            table.GraphQlName.Returns(name);
            table.TableSchema.Returns("dbo");
            table.Columns.Returns(columns);
            table.KeyColumns.Returns(columns.Where(c => c.IsPrimaryKey));
            return table;
        }

        private readonly record struct ScanPage(string Cursor, IReadOnlyList<string> Keys);

        private static async Task<ScanPage> ScanOnceAsync(IServiceProvider services, string cursor, string count)
        {
            var reply = await Handle(services, "SCAN", cursor, "MATCH", "widgets:*", "COUNT", count);
            var items = reply.Should().BeOfType<RespArray>().Subject.Items!;
            var next = Encoding.UTF8.GetString(((RespBulkString)items[0]).Value!);
            var keys = ((RespArray)items[1]).Items!
                .Select(k => Encoding.UTF8.GetString(((RespBulkString)k).Value!)).ToList();
            return new ScanPage(next, keys);
        }

        private static Task<RespValue> Handle(IServiceProvider services, params string[] arguments) =>
            new RespScanCommandHandler().HandleAsync(
                new RespCommandContext(arguments, Session, services, null), CancellationToken.None);

    }
}
