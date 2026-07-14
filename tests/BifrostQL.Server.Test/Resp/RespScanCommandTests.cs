using System.Globalization;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Resp;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test.Resp
{
    /// <summary>
    /// Handler-level tests for the RESP slice-4 SCAN surface
    /// (<c>SCAN &lt;cursor&gt; MATCH &lt;table&gt;:* [COUNT n] [TYPE t]</c>). SCAN maps to keyset
    /// pagination over a table's primary key: ORDER BY the PK, WHERE pk &gt; last-cursor-position,
    /// LIMIT the (capped) page size — composed from the EXISTING <see cref="GqlObjectQuery"/>
    /// Sort/Filter/Limit surface, executed through <c>IQueryIntentExecutor</c> under the session
    /// identity so the tenant/policy transformer pipeline ANDs its predicate onto every page.
    ///
    /// <para>The load-bearing invariant proven here: the cursor controls ONLY the pagination START
    /// POSITION. Even a forged cursor that points into another tenant's PK range still gets the tenant
    /// filter ANDed by the pipeline, so it can never enumerate a row outside the caller's scope — at
    /// most it skips ahead within the caller's own visible set.</para>
    /// </summary>
    public sealed class RespScanCommandTests
    {
        // ---- single-PK enumeration -----------------------------------------

        [Fact]
        public async Task Scan_SinglePk_IteratesAllVisiblePksPageByPage_NoDupesNoOmissions()
        {
            var (executor, services) = Arrange(Tenant1);

            var keys = await ScanAllAsync(services, "widgets", count: "2");

            // Tenant 1 owns widgets 1,2,4 (3 and 5 belong to tenant 2). SCAN must yield exactly those,
            // paged, with no duplicate and no omission.
            keys.Should().BeEquivalentTo(new[] { "widgets:1", "widgets:2", "widgets:4" });
            keys.Should().OnlyHaveUniqueItems();
            // More than one page happened: the first page returned a non-terminal cursor.
            executor.Intents.Count.Should().BeGreaterThan(1);
        }

        [Fact]
        public async Task Scan_FirstPage_ReturnsNonZeroCursor_ThenTerminatesAtZero()
        {
            var (_, services) = Arrange(Tenant1);

            var page1 = await ScanOnceAsync(services, "0", "widgets", count: "2");
            page1.Cursor.Should().NotBe(RespProtocol.ScanStartCursor);
            page1.Keys.Should().Equal("widgets:1", "widgets:2");

            var page2 = await ScanOnceAsync(services, page1.Cursor, "widgets", count: "2");
            page2.Keys.Should().Equal("widgets:4");
            page2.Cursor.Should().Be(RespProtocol.ScanStartCursor, "the last page reports the terminal cursor");
        }

        // ---- composite-PK enumeration --------------------------------------

        [Fact]
        public async Task Scan_CompositePk_EmitsMultiSegmentKeys_AndCursorRoundTripsAComposite()
        {
            var (_, services) = Arrange(Tenant1);

            // PK order is (region, part_no); stable ascending order is eu:1, us:1, us:2.
            var page1 = await ScanOnceAsync(services, "0", "parts", count: "2");
            page1.Keys.Should().Equal("parts:eu:1", "parts:us:1");
            page1.Cursor.Should().NotBe(RespProtocol.ScanStartCursor);

            // Feeding the composite cursor continues immediately after (us,1) — proving the cursor
            // round-trips ALL key columns, not just the first.
            var page2 = await ScanOnceAsync(services, page1.Cursor, "parts", count: "2");
            page2.Keys.Should().Equal("parts:us:2");
            page2.Cursor.Should().Be(RespProtocol.ScanStartCursor);
        }

        // ---- tenant isolation ----------------------------------------------

        [Fact]
        public async Task Scan_TwoTenants_EachEnumeratesOnlyItsOwnPks()
        {
            var (_, servicesA) = Arrange(Tenant1);
            var (_, servicesB) = Arrange(Tenant2);

            var aKeys = await ScanAllAsync(servicesA, "widgets", count: "10");
            var bKeys = await ScanAllAsync(servicesB, "widgets", count: "10");

            aKeys.Should().BeEquivalentTo(new[] { "widgets:1", "widgets:2", "widgets:4" });
            bKeys.Should().BeEquivalentTo(new[] { "widgets:3", "widgets:5" });
            aKeys.Should().NotIntersectWith(bKeys, "one identity's SCAN must never surface another tenant's PKs");
        }

        // ---- cursor cannot widen visibility --------------------------------

        [Fact]
        public async Task Scan_ForgedCursorPointingIntoAnotherTenantsRange_StillOnlyYieldsCallersPks()
        {
            var (_, services) = Arrange(Tenant1);

            // Hand-craft a cursor positioned at widget id 3 — a row that belongs to tenant 2 and that
            // tenant 1 could never legitimately obtain a cursor for. Feeding it must NOT let tenant 1
            // step into tenant 2's rows: the tenant filter is ANDed by the pipeline regardless of the
            // cursor, so the only rows returned are tenant 1's own ids strictly greater than 3 → {4}.
            var forged = RespScanCursor.Encode(new[] { "3" });

            var page = await ScanOnceAsync(services, forged, "widgets", count: "10");

            page.Keys.Should().Equal("widgets:4");
            page.Keys.Should().NotContain(new[] { "widgets:3", "widgets:5" },
                "a forged cursor may at most skip ahead within the caller's own visible set, never widen it");
        }

        [Fact]
        public async Task Scan_CursorBelowEverything_StillTenantScoped()
        {
            var (_, services) = Arrange(Tenant1);

            var forged = RespScanCursor.Encode(new[] { "0" });
            var keys = new List<string>();
            var cursor = forged;
            do
            {
                var page = await ScanOnceAsync(services, cursor, "widgets", count: "10");
                keys.AddRange(page.Keys);
                cursor = page.Cursor;
            } while (cursor != RespProtocol.ScanStartCursor);

            keys.Should().BeEquivalentTo(new[] { "widgets:1", "widgets:2", "widgets:4" });
        }

        // ---- COUNT cap ------------------------------------------------------

        [Fact]
        public async Task Scan_Count_IsClampedToMaxPageSize()
        {
            var (executor, services) = Arrange(Tenant1);

            await ScanOnceAsync(services, "0", "widgets", count: "999999");

            // The engine fetches pageSize + 1 rows (a peek to detect more), so a capped page of
            // MaxScanPageSize shows up as Limit == MaxScanPageSize + 1.
            executor.Intents.Should().ContainSingle();
            executor.Intents[0].Query.Limit.Should().Be(RespProtocol.MaxScanPageSize + 1);
        }

        [Fact]
        public async Task Scan_Count_HintIsHonoredWhenBelowCap()
        {
            var (executor, services) = Arrange(Tenant1);
            await ScanOnceAsync(services, "0", "widgets", count: "2");
            executor.Intents[0].Query.Limit.Should().Be(3, "requested COUNT 2 + 1 peek row");
        }

        [Fact]
        public async Task Scan_NoCount_UsesDefaultPageSize()
        {
            var (executor, services) = Arrange(Tenant1);
            await ScanOnceAsync(services, "0", "widgets", count: null);
            executor.Intents[0].Query.Limit.Should().Be(RespProtocol.DefaultScanPageSize + 1);
        }

        [Fact]
        public async Task Scan_NonPositiveCount_IsSyntaxError()
        {
            var (executor, services) = Arrange(Tenant1);
            var reply = await Handle(services, "SCAN", "0", "MATCH", "widgets:*", "COUNT", "0");
            reply.Should().BeOfType<RespError>().Which.Message.Should().StartWith("ERR ");
            executor.Intents.Should().BeEmpty();
        }

        // ---- MATCH / table validation --------------------------------------

        [Fact]
        public async Task Scan_ProjectsOnlyPrimaryKeyColumns()
        {
            var (executor, services) = Arrange(Tenant1);
            await ScanOnceAsync(services, "0", "widgets", count: "2");

            var projected = executor.Intents[0].Query.ScalarColumns.Select(c => c.DbDbName);
            projected.Should().Equal(new[] { "id" }, "the enumeration selects only the PK column, never other row data");
        }

        [Fact]
        public async Task Scan_UnknownTable_IsCleanError_NeverExecutes()
        {
            var (executor, services) = Arrange(Tenant1);
            var reply = await Handle(services, "SCAN", "0", "MATCH", "ghosts:*");

            reply.Should().BeOfType<RespError>().Which.Message.Should().Contain("unknown table 'ghosts'");
            executor.Intents.Should().BeEmpty();
        }

        [Fact]
        public async Task Scan_PartialGlobPattern_IsCleanError_NeverExecutes()
        {
            var (executor, services) = Arrange(Tenant1);
            var reply = await Handle(services, "SCAN", "0", "MATCH", "widgets:1*");

            reply.Should().BeOfType<RespError>().Which.Message.Should().StartWith("ERR ");
            executor.Intents.Should().BeEmpty("only <table>:* is supported; a partial glob must not enumerate");
        }

        [Fact]
        public async Task Scan_MissingMatch_IsCleanError()
        {
            var (_, services) = Arrange(Tenant1);
            var reply = await Handle(services, "SCAN", "0");
            reply.Should().BeOfType<RespError>().Which.Message.Should().StartWith("ERR ");
        }

        [Fact]
        public async Task Scan_TypeArgument_IsAcceptedAndIgnored()
        {
            var (_, services) = Arrange(Tenant1);
            var reply = await Handle(services, "SCAN", "0", "MATCH", "widgets:*", "COUNT", "10", "TYPE", "string");

            var (_, keys) = ParseScanReply(reply);
            keys.Should().BeEquivalentTo(new[] { "widgets:1", "widgets:2", "widgets:4" });
        }

        [Fact]
        public async Task Scan_MalformedCursor_IsCleanError_NeverExecutes()
        {
            var (executor, services) = Arrange(Tenant1);
            var reply = await Handle(services, "SCAN", "not-a-real-cursor", "MATCH", "widgets:*");

            reply.Should().BeOfType<RespError>().Which.Message.Should().StartWith("ERR ");
            executor.Intents.Should().BeEmpty();
        }

        [Fact]
        public async Task Scan_NoArguments_IsWrongArgCountError()
        {
            var (_, services) = Arrange(Tenant1);
            var reply = await Handle(services, "SCAN");
            reply.Should().BeOfType<RespError>().Which.Message.Should().Be(RespProtocol.WrongArgCount("SCAN"));
        }

        // ---- fail-closed over the wire (slice-1 fixture) -------------------

        [Fact]
        public async Task Scan_BeforeAuth_IsRefusedWithNoAuth()
        {
            var store = new FakeRespCredentialStore();
            var services = new ServiceCollection()
                .AddSingleton<IQueryIntentExecutor>(new FakeScanExecutor(BuildModel()))
                .BuildServiceProvider();
            var options = new RespWireOptions { RequireAuthentication = true };

            await using var fixture = await RespFixture.StartAsync(store, services, options, new RespScanCommandHandler());
            await fixture.Client.SendCommandAsync("SCAN", "0", "MATCH", "widgets:*");
            var reply = await fixture.Client.ReadReplyAsync();

            reply.Should().BeOfType<RespError>().Which.Message.Should().StartWith("NOAUTH");
        }

        // ---- helpers --------------------------------------------------------

        private static readonly IDictionary<string, object?> Tenant1 =
            new Dictionary<string, object?> { ["tenantId"] = 1 };

        private static readonly IDictionary<string, object?> Tenant2 =
            new Dictionary<string, object?> { ["tenantId"] = 2 };

        private static (FakeScanExecutor executor, IServiceProvider services) Arrange(IDictionary<string, object?> tenant)
        {
            var executor = new FakeScanExecutor(BuildModel());
            var services = new ServiceCollection()
                .AddSingleton<IQueryIntentExecutor>(executor)
                .BuildServiceProvider();
            var session = new RespSession(1);
            session.Authenticate(tenant);
            _sessions[services] = session;
            return (executor, services);
        }

        private static readonly Dictionary<IServiceProvider, RespSession> _sessions = new();

        private static Task<RespValue> Handle(IServiceProvider services, params string[] arguments) =>
            new RespScanCommandHandler().HandleAsync(
                new RespCommandContext(arguments, _sessions[services], services, null), CancellationToken.None);

        private readonly record struct ScanPage(string Cursor, IReadOnlyList<string> Keys);

        private static async Task<ScanPage> ScanOnceAsync(IServiceProvider services, string cursor, string table, string? count)
        {
            var args = new List<string> { "SCAN", cursor, "MATCH", $"{table}:*" };
            if (count is not null) { args.Add("COUNT"); args.Add(count); }
            var reply = await Handle(services, args.ToArray());
            var (nextCursor, keys) = ParseScanReply(reply);
            return new ScanPage(nextCursor, keys);
        }

        private static async Task<List<string>> ScanAllAsync(IServiceProvider services, string table, string count)
        {
            var all = new List<string>();
            var cursor = RespProtocol.ScanStartCursor;
            do
            {
                var page = await ScanOnceAsync(services, cursor, table, count);
                all.AddRange(page.Keys);
                cursor = page.Cursor;
            } while (cursor != RespProtocol.ScanStartCursor);
            return all;
        }

        private static (string Cursor, IReadOnlyList<string> Keys) ParseScanReply(RespValue reply)
        {
            var top = reply.Should().BeOfType<RespArray>().Which.Items!;
            top.Should().HaveCount(2, "a SCAN reply is [cursor, keys]");
            var cursor = System.Text.Encoding.UTF8.GetString(
                top[0].Should().BeOfType<RespBulkString>().Which.Value!);
            var keys = top[1].Should().BeOfType<RespArray>().Which.Items!
                .Select(k => System.Text.Encoding.UTF8.GetString(((RespBulkString)k).Value!))
                .ToList();
            return (cursor, keys);
        }

        private static IDbModel BuildModel()
        {
            var widgets = FakeTable("widgets",
                Col("id", "int", 1, pk: true),
                Col("name", "varchar", 2),
                Col("tenant_id", "int", 3));

            var parts = FakeTable("parts",
                Col("region", "varchar", 1, pk: true),
                Col("part_no", "int", 2, pk: true),
                Col("label", "varchar", 3));

            var model = Substitute.For<IDbModel>();
            model.Tables.Returns(new[] { widgets, parts });
            return model;
        }

        private static ColumnDto Col(string name, string type, int ordinal, bool pk = false) =>
            new() { ColumnName = name, GraphQlName = name, DataType = type, OrdinalPosition = ordinal, IsPrimaryKey = pk };

        private static IDbTable FakeTable(string name, params ColumnDto[] columns)
        {
            var t = Substitute.For<IDbTable>();
            t.DbName.Returns(name);
            t.GraphQlName.Returns(name);
            t.TableSchema.Returns("dbo");
            t.Columns.Returns(columns);
            t.KeyColumns.Returns(columns.Where(c => c.IsPrimaryKey));
            return t;
        }
    }

    /// <summary>
    /// A fake read executor that models the REAL execution path SCAN relies on: it evaluates the
    /// query's keyset <see cref="TableFilter"/> (an OR of lexicographic AND terms of <c>_eq</c>/<c>_gt</c>),
    /// ANDs a tenant predicate (exactly as the security transformer would — a row with a <c>tenant_id</c>
    /// is visible only to a matching <c>tenantId</c> in the user context, regardless of the query filter),
    /// honors the query's <see cref="GqlObjectQuery.Sort"/> and <see cref="GqlObjectQuery.Limit"/>, and
    /// projects only the query's selected columns. Records every intent so the page-size cap, projection,
    /// and identity propagation are assertable.
    /// </summary>
    internal sealed class FakeScanExecutor : IQueryIntentExecutor
    {
        private readonly IDbModel _model;
        private readonly Dictionary<string, List<Dictionary<string, object?>>> _store;

        public List<QueryIntent> Intents { get; } = new();

        public FakeScanExecutor(IDbModel model)
        {
            _model = model;
            _store = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.Ordinal)
            {
                ["widgets"] = new()
                {
                    Row(("id", 1), ("name", "w1"), ("tenant_id", 1)),
                    Row(("id", 2), ("name", "w2"), ("tenant_id", 1)),
                    Row(("id", 3), ("name", "w3"), ("tenant_id", 2)),
                    Row(("id", 4), ("name", "w4"), ("tenant_id", 1)),
                    Row(("id", 5), ("name", "w5"), ("tenant_id", 2)),
                },
                ["parts"] = new()
                {
                    Row(("region", "us"), ("part_no", 1), ("label", "a")),
                    Row(("region", "us"), ("part_no", 2), ("label", "b")),
                    Row(("region", "eu"), ("part_no", 1), ("label", "c")),
                },
            };
        }

        public Task<IDbModel> GetModelAsync(string? endpoint = null) => Task.FromResult(_model);

        public Task<QueryIntentResult> ExecuteAsync(QueryIntent intent, CancellationToken cancellationToken = default)
        {
            Intents.Add(intent);
            var table = intent.Query.DbTable.DbName;

            IEnumerable<Dictionary<string, object?>> rows = _store.TryGetValue(table, out var stored)
                ? stored
                : new List<Dictionary<string, object?>>();

            if (intent.Query.Filter is { } filter)
                rows = rows.Where(r => Eval(filter, r));

            // The tenant transformer ANDs its predicate onto whatever the query already filters on —
            // never removed by the query's own filter (the cursor-can't-widen invariant).
            if (intent.UserContext.TryGetValue("tenantId", out var tenant))
                rows = rows.Where(r => !r.ContainsKey("tenant_id") || Compare(r["tenant_id"], tenant) == 0);

            rows = ApplySort(rows, intent.Query.Sort);

            if (intent.Query.Limit is { } limit && limit >= 0)
                rows = rows.Take(limit);

            var projected = rows
                .Select(r => (IReadOnlyDictionary<string, object?>)Project(r, intent.Query.ScalarColumns))
                .ToList();
            return Task.FromResult(new QueryIntentResult { Rows = projected, Sql = string.Empty });
        }

        private static Dictionary<string, object?> Project(Dictionary<string, object?> row, IEnumerable<GqlObjectColumn> columns)
        {
            var selected = columns.Select(c => c.DbDbName).ToList();
            return selected.Count == 0
                ? new Dictionary<string, object?>(row, StringComparer.Ordinal)
                : selected.ToDictionary(c => c, c => row.GetValueOrDefault(c), StringComparer.Ordinal);
        }

        private static IEnumerable<Dictionary<string, object?>> ApplySort(
            IEnumerable<Dictionary<string, object?>> rows, IReadOnlyList<string> sort)
        {
            if (sort.Count == 0) return rows;
            IOrderedEnumerable<Dictionary<string, object?>>? ordered = null;
            foreach (var token in sort)
            {
                var column = token.EndsWith("_desc") ? token[..^5] : token[..^4];
                ordered = ordered is null
                    ? rows.OrderBy(r => r.GetValueOrDefault(column), ScanValueComparer.Instance)
                    : ordered.ThenBy(r => r.GetValueOrDefault(column), ScanValueComparer.Instance);
            }
            return ordered ?? rows;
        }

        private static bool Eval(TableFilter node, Dictionary<string, object?> row)
        {
            switch (node.FilterType)
            {
                case FilterType.And:
                    return node.And.All(child => Eval(child, row));
                case FilterType.Or:
                    return node.Or.Any(child => Eval(child, row));
                case FilterType.Join when node.ColumnName is not null && node.Next is not null:
                    var cell = row.GetValueOrDefault(node.ColumnName);
                    var cmp = Compare(cell, node.Next.Value);
                    return node.Next.RelationName switch
                    {
                        FilterOperators.Eq => cmp == 0,
                        FilterOperators.Gt => cmp > 0,
                        FilterOperators.Gte => cmp >= 0,
                        FilterOperators.Lt => cmp < 0,
                        FilterOperators.Lte => cmp <= 0,
                        _ => throw new NotSupportedException($"fake does not model operator {node.Next.RelationName}"),
                    };
                default:
                    return true;
            }
        }

        private static int Compare(object? a, object? b) => ScanValueComparer.Instance.Compare(a, b);

        private static Dictionary<string, object?> Row(params (string Column, object? Value)[] cells) =>
            cells.ToDictionary(c => c.Column, c => c.Value, StringComparer.Ordinal);

        /// <summary>Numeric-aware ordinal comparer: two values that both parse as decimals compare
        /// numerically (so a bound parameter of any scale matches the stored PK), else invariant string.</summary>
        private sealed class ScanValueComparer : IComparer<object?>
        {
            public static readonly ScanValueComparer Instance = new();

            public int Compare(object? x, object? y)
            {
                var sx = Convert.ToString(x, CultureInfo.InvariantCulture) ?? string.Empty;
                var sy = Convert.ToString(y, CultureInfo.InvariantCulture) ?? string.Empty;
                if (decimal.TryParse(sx, NumberStyles.Number, CultureInfo.InvariantCulture, out var dx)
                    && decimal.TryParse(sy, NumberStyles.Number, CultureInfo.InvariantCulture, out var dy))
                    return dx.CompareTo(dy);
                return string.CompareOrdinal(sx, sy);
            }
        }
    }
}
