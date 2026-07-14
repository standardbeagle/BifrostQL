using System.Globalization;
using System.Text;
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
    /// Handler-level tests for the RESP slice-2 read surface (GET/MGET/EXISTS/TYPE). They exercise the
    /// real key parser, composite-PK mapping (schema order via <see cref="TableFilter.FromPrimaryKey"/>,
    /// never <c>[0]</c>), the row→JSON shape, MGET batching, and — through a filter-aware fake executor
    /// that also honors a tenant predicate — the "invisible row is indistinguishable from missing"
    /// property. The fail-closed-before-AUTH gate is proven over the wire with the slice-1 fixture.
    /// </summary>
    public sealed class RespReadCommandTests
    {
        // ---- GET ------------------------------------------------------------

        [Fact]
        public async Task Get_SinglePk_ReturnsRowAsJson()
        {
            var (executor, ctx) = Arrange(Tenant1);
            var reply = await new RespGetCommandHandler().HandleAsync(Ctx(ctx, "GET", "users:1"), CancellationToken.None);

            var json = BulkText(reply);
            json.Should().Contain("\"name\":\"alice\"").And.Contain("\"id\":1");
            // Read routed through the intent executor under the caller's identity (tenant filter applies).
            executor.Intents.Should().HaveCount(1);
            executor.Intents[0].UserContext.Should().Contain(new KeyValuePair<string, object?>("tenantId", 1));
            executor.Intents[0].Query.Filter.Should().NotBeNull();
        }

        [Fact]
        public async Task Get_MissingKey_ReturnsNull()
        {
            var (_, ctx) = Arrange(Tenant1);
            var reply = await new RespGetCommandHandler().HandleAsync(Ctx(ctx, "GET", "users:999"), CancellationToken.None);
            reply.Should().BeOfType<RespBulkString>().Which.Value.Should().BeNull();
        }

        [Fact]
        public async Task Get_RowInAnotherTenant_ReturnsNull_NotDistinguishableFromMissing()
        {
            // Row id=3 exists but belongs to tenant 2; caller is tenant 1. The tenant predicate (applied
            // inside the intent, exactly as the real transformer would) removes it, so GET sees no row.
            var (_, ctx) = Arrange(Tenant1);
            var reply = await new RespGetCommandHandler().HandleAsync(Ctx(ctx, "GET", "users:3"), CancellationToken.None);
            reply.Should().BeOfType<RespBulkString>().Which.Value.Should().BeNull(
                "a row the identity cannot see must be indistinguishable from a missing key");
        }

        [Fact]
        public async Task Get_CompositePk_ResolvesInSchemaOrder()
        {
            var (executor, ctx) = Arrange(Tenant1);
            var reply = await new RespGetCommandHandler().HandleAsync(Ctx(ctx, "GET", "order_items:10:2"), CancellationToken.None);

            BulkText(reply).Should().Contain("\"sku\":\"widget\"");
            executor.Intents.Should().HaveCount(1);
        }

        [Fact]
        public async Task Get_CompositePk_WrongArity_IsCleanErrorAndProvesNotFirstKeyOnly()
        {
            // Only one segment for a two-column PK: a naive primaryKeys[0] lookup would have "worked"
            // against the first column; the composite helper rejects the arity honestly instead.
            var (executor, ctx) = Arrange(Tenant1);
            var reply = await new RespGetCommandHandler().HandleAsync(Ctx(ctx, "GET", "order_items:10"), CancellationToken.None);

            reply.Should().BeOfType<RespError>().Which.Message.Should().StartWith("ERR ");
            executor.Intents.Should().BeEmpty("a malformed key must never execute against the database");
        }

        [Fact]
        public async Task Get_WrongArgumentCount_IsCleanError()
        {
            var (_, ctx) = Arrange(Tenant1);
            var reply = await new RespGetCommandHandler().HandleAsync(Ctx(ctx, "GET"), CancellationToken.None);
            reply.Should().BeOfType<RespError>().Which.Message.Should().Be(RespProtocol.WrongArgCount("GET"));
        }

        // ---- MGET -----------------------------------------------------------

        [Fact]
        public async Task MGet_MixedHitsAndMisses_ArePositionallyAligned_AndBatched()
        {
            var (executor, ctx) = Arrange(Tenant1);
            var reply = await new RespMGetCommandHandler().HandleAsync(Ctx(ctx, "MGET", "users:1", "users:999", "users:2"), CancellationToken.None);

            var items = reply.Should().BeOfType<RespArray>().Which.Items!;
            items.Should().HaveCount(3);
            BulkText(items[0]).Should().Contain("\"name\":\"alice\"");
            items[1].Should().BeOfType<RespBulkString>().Which.Value.Should().BeNull();
            BulkText(items[2]).Should().Contain("\"name\":\"bob\"");

            executor.Intents.Should().HaveCount(1, "same-table single-PK keys collapse into one _in intent");
        }

        [Fact]
        public async Task MGet_KeysAcrossTables_GroupsPerTable()
        {
            var (executor, ctx) = Arrange(Tenant1);
            var reply = await new RespMGetCommandHandler().HandleAsync(
                Ctx(ctx, "MGET", "users:1", "users:2", "order_items:10:2"), CancellationToken.None);

            reply.Should().BeOfType<RespArray>().Which.Items!.Should().HaveCount(3);
            // One batched _in intent for users, one exact intent for the composite order_items key.
            executor.Intents.Should().HaveCount(2);
        }

        // ---- EXISTS ---------------------------------------------------------

        [Fact]
        public async Task Exists_CountsOnlyVisibleRows()
        {
            var (_, ctx) = Arrange(Tenant1);
            // users:1 visible, users:3 belongs to tenant 2 (invisible), users:999 missing.
            var reply = await new RespExistsCommandHandler().HandleAsync(
                Ctx(ctx, "EXISTS", "users:1", "users:3", "users:999"), CancellationToken.None);
            reply.Should().BeOfType<RespInteger>().Which.Value.Should().Be(1);
        }

        // ---- TYPE -----------------------------------------------------------

        [Fact]
        public async Task Type_VisibleRow_IsString_MissingIsNone()
        {
            var (_, ctx) = Arrange(Tenant1);

            var hit = await new RespTypeCommandHandler().HandleAsync(Ctx(ctx, "TYPE", "users:1"), CancellationToken.None);
            hit.Should().BeOfType<RespSimpleString>().Which.Value.Should().Be(RespProtocol.TypeString);

            var miss = await new RespTypeCommandHandler().HandleAsync(Ctx(ctx, "TYPE", "users:999"), CancellationToken.None);
            miss.Should().BeOfType<RespSimpleString>().Which.Value.Should().Be(RespProtocol.TypeNone);

            var invisible = await new RespTypeCommandHandler().HandleAsync(Ctx(ctx, "TYPE", "users:3"), CancellationToken.None);
            invisible.Should().BeOfType<RespSimpleString>().Which.Value.Should().Be(RespProtocol.TypeNone);
        }

        // ---- validation edges ----------------------------------------------

        [Fact]
        public async Task Get_UnknownTable_IsCleanError_NeverExecutes()
        {
            var (executor, ctx) = Arrange(Tenant1);
            var reply = await new RespGetCommandHandler().HandleAsync(Ctx(ctx, "GET", "ghosts:1"), CancellationToken.None);

            reply.Should().BeOfType<RespError>().Which.Message.Should().Contain("unknown table 'ghosts'");
            executor.Intents.Should().BeEmpty();
        }

        [Fact]
        public async Task Get_UnparseablePkSegment_IsCleanError_NeverExecutes()
        {
            var (executor, ctx) = Arrange(Tenant1);
            var reply = await new RespGetCommandHandler().HandleAsync(Ctx(ctx, "GET", "users:not-a-number"), CancellationToken.None);

            reply.Should().BeOfType<RespError>().Which.Message.Should().StartWith("ERR ");
            executor.Intents.Should().BeEmpty();
        }

        // ---- fail-closed over the wire (slice-1 fixture) --------------------

        [Fact]
        public async Task Get_BeforeAuth_IsRefusedWithNoAuth()
        {
            var store = new FakeRespCredentialStore();
            var services = new ServiceCollection()
                .AddSingleton<IQueryIntentExecutor>(new FakeIntentExecutor(BuildModel()))
                .BuildServiceProvider();
            var options = new RespWireOptions { RequireAuthentication = true };

            await using var fixture = await RespFixture.StartAsync(store, services, options, new RespGetCommandHandler());
            await fixture.Client.SendCommandAsync("GET", "users:1");
            var reply = await fixture.Client.ReadReplyAsync();

            reply.Should().BeOfType<RespError>().Which.Message.Should().StartWith("NOAUTH");
        }

        // ---- fixtures -------------------------------------------------------

        private static readonly IDictionary<string, object?> Tenant1 =
            new Dictionary<string, object?> { ["tenantId"] = 1 };

        private static (FakeIntentExecutor executor, IServiceProvider services) Arrange(IDictionary<string, object?> tenant)
        {
            var executor = new FakeIntentExecutor(BuildModel());
            var services = new ServiceCollection()
                .AddSingleton<IQueryIntentExecutor>(executor)
                .BuildServiceProvider();
            var session = new RespSession(1);
            session.Authenticate(tenant);
            _sessions[services] = session;
            return (executor, services);
        }

        private static readonly Dictionary<IServiceProvider, RespSession> _sessions = new();

        private static RespCommandContext Ctx(IServiceProvider services, params string[] arguments) =>
            new(arguments, _sessions[services], services, null);

        private static string BulkText(RespValue value) =>
            Encoding.UTF8.GetString(value.Should().BeOfType<RespBulkString>().Which.Value!);

        private static IDbModel BuildModel()
        {
            var users = FakeTable("users",
                Col("id", "int", 1, pk: true),
                Col("name", "varchar", 2),
                Col("tenant_id", "int", 3));

            var orderItems = FakeTable("order_items",
                Col("order_id", "int", 1, pk: true),
                Col("line_no", "int", 2, pk: true),
                Col("sku", "varchar", 3));

            var model = Substitute.For<IDbModel>();
            model.Tables.Returns(new[] { users, orderItems });
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
    /// A filter-aware fake read executor. It applies the intent's <c>_eq</c>/<c>_in</c> primary-key
    /// constraints AND a tenant predicate (a row with a <c>tenant_id</c> is visible only to a matching
    /// <c>tenantId</c> in the user context) — modeling the real security transformer so a hidden row
    /// simply returns no row. Records every intent so batching and identity propagation are assertable.
    /// </summary>
    internal sealed class FakeIntentExecutor : IQueryIntentExecutor
    {
        private readonly IDbModel _model;
        private readonly Dictionary<string, List<Dictionary<string, object?>>> _store;

        public List<QueryIntent> Intents { get; } = new();

        public FakeIntentExecutor(IDbModel model)
        {
            _model = model;
            _store = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.Ordinal)
            {
                ["users"] = new()
                {
                    Row(("id", 1), ("name", "alice"), ("tenant_id", 1)),
                    Row(("id", 2), ("name", "bob"), ("tenant_id", 1)),
                    Row(("id", 3), ("name", "carol"), ("tenant_id", 2)),
                },
                ["order_items"] = new()
                {
                    Row(("order_id", 10), ("line_no", 1), ("sku", "gadget")),
                    Row(("order_id", 10), ("line_no", 2), ("sku", "widget")),
                },
            };
        }

        public Task<IDbModel> GetModelAsync(string? endpoint = null) => Task.FromResult(_model);

        public Task<QueryIntentResult> ExecuteAsync(QueryIntent intent, CancellationToken cancellationToken = default)
        {
            Intents.Add(intent);
            var table = intent.Query.DbTable.DbName;
            var constraints = ExtractConstraints(intent.Query.Filter);

            IEnumerable<Dictionary<string, object?>> rows = _store.TryGetValue(table, out var stored)
                ? stored
                : new List<Dictionary<string, object?>>();

            rows = rows.Where(r => constraints.All(kv => kv.Value.Contains(Token(r.GetValueOrDefault(kv.Key)))));

            if (intent.UserContext.TryGetValue("tenantId", out var tenant))
                rows = rows.Where(r => !r.ContainsKey("tenant_id") || Token(r["tenant_id"]) == Token(tenant));

            var result = rows.Select(r => (IReadOnlyDictionary<string, object?>)r).ToList();
            return Task.FromResult(new QueryIntentResult { Rows = result, Sql = string.Empty });
        }

        private static Dictionary<string, HashSet<string>> ExtractConstraints(TableFilter? filter)
        {
            var acc = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            if (filter is null) return acc;

            void Walk(TableFilter node)
            {
                switch (node.FilterType)
                {
                    case FilterType.And:
                        foreach (var child in node.And) Walk(child);
                        break;
                    case FilterType.Or:
                        foreach (var child in node.Or) Walk(child);
                        break;
                    case FilterType.Join when node.ColumnName is not null && node.Next is not null:
                        var op = node.Next.RelationName;
                        var values = new HashSet<string>(StringComparer.Ordinal);
                        if (op == FilterOperators.In && node.Next.Value is System.Collections.IEnumerable list and not string)
                            foreach (var v in list) values.Add(Token(v));
                        else
                            values.Add(Token(node.Next.Value));
                        acc[node.ColumnName] = values;
                        break;
                }
            }

            Walk(filter);
            return acc;
        }

        private static string Token(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

        private static Dictionary<string, object?> Row(params (string Column, object? Value)[] cells) =>
            cells.ToDictionary(c => c.Column, c => c.Value, StringComparer.Ordinal);
    }
}
