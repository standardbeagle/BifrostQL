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
    /// Handler-level tests for the RESP slice-3 hash surface (HGETALL/HGET). They prove the two
    /// commands REUSE the slice-2 single-row read path (one <c>IQueryIntentExecutor</c> intent under the
    /// caller's identity, composite PK via <see cref="TableFilter.FromPrimaryKey"/>), the RESP3-Map vs
    /// RESP2-flat-array wire shapes, the missing/tenant-hidden = empty-or-Null indistinguishability, and —
    /// the security-critical property with no review gate to catch it — that the hash is built strictly
    /// from the columns the pipeline RETURNED: a masked/omitted column never surfaces in HGETALL and HGET
    /// on it returns Null (indistinguishable from an unknown field), never a distinct existence-leaking error.
    /// </summary>
    public sealed class RespHashCommandTests
    {
        // ---- HGETALL: wire shapes ------------------------------------------

        [Fact]
        public async Task HGetAll_Resp3_ReturnsVisibleColumnsAsMap()
        {
            var (executor, ctx) = Arrange(Tenant1, protocol: RespProtocol.Resp3);
            var reply = await new RespHGetAllCommandHandler().HandleAsync(Ctx(ctx, "HGETALL", "users:1"), CancellationToken.None);

            var entries = reply.Should().BeOfType<RespMap>().Which.Entries;
            HashOf(entries).Should().Equal(
                ("id", "1"), ("name", "alice"), ("tenant_id", "1")); // schema ordinal order

            // Reuse evidence: one intent, under the caller's identity, with a PK filter.
            executor.Intents.Should().HaveCount(1);
            executor.Intents[0].UserContext.Should().Contain(new KeyValuePair<string, object?>("tenantId", 1));
            executor.Intents[0].Query.Filter.Should().NotBeNull();
        }

        [Fact]
        public async Task HGetAll_Resp2_ReturnsVisibleColumnsAsFlatArray()
        {
            var (_, ctx) = Arrange(Tenant1, protocol: RespProtocol.Resp2);
            var reply = await new RespHGetAllCommandHandler().HandleAsync(Ctx(ctx, "HGETALL", "users:1"), CancellationToken.None);

            var items = reply.Should().BeOfType<RespArray>().Which.Items!;
            FlatHashOf(items).Should().Equal(
                ("id", "1"), ("name", "alice"), ("tenant_id", "1"));
        }

        [Fact]
        public async Task HGetAll_CompositePk_ResolvesInSchemaOrder_ReusesFromPrimaryKey()
        {
            var (executor, ctx) = Arrange(Tenant1, protocol: RespProtocol.Resp2);
            var reply = await new RespHGetAllCommandHandler().HandleAsync(Ctx(ctx, "HGETALL", "order_items:10:2"), CancellationToken.None);

            FlatHashOf(reply.Should().BeOfType<RespArray>().Which.Items!)
                .Should().Equal(("order_id", "10"), ("line_no", "2"), ("sku", "widget"));
            executor.Intents.Should().HaveCount(1);
        }

        // ---- HGETALL: missing / hidden = empty (no leak) -------------------

        [Fact]
        public async Task HGetAll_MissingKey_IsEmptyHash()
        {
            var (_, ctx) = Arrange(Tenant1, protocol: RespProtocol.Resp2);
            var reply = await new RespHGetAllCommandHandler().HandleAsync(Ctx(ctx, "HGETALL", "users:999"), CancellationToken.None);
            reply.Should().BeOfType<RespArray>().Which.Items.Should().BeEmpty();
        }

        [Fact]
        public async Task HGetAll_TenantHiddenRow_IsEmptyHash_NotDistinguishableFromMissing()
        {
            // users:3 exists but belongs to tenant 2; caller is tenant 1. Empty in BOTH protocols.
            var (_, resp2) = Arrange(Tenant1, protocol: RespProtocol.Resp2);
            var (_, resp3) = Arrange(Tenant1, protocol: RespProtocol.Resp3);

            var r2 = await new RespHGetAllCommandHandler().HandleAsync(Ctx(resp2, "HGETALL", "users:3"), CancellationToken.None);
            var r3 = await new RespHGetAllCommandHandler().HandleAsync(Ctx(resp3, "HGETALL", "users:3"), CancellationToken.None);

            r2.Should().BeOfType<RespArray>().Which.Items.Should().BeEmpty(
                "a hidden row must be indistinguishable from a missing key");
            r3.Should().BeOfType<RespMap>().Which.Entries.Should().BeEmpty();
        }

        // ---- COLUMN VISIBILITY (security-critical) -------------------------

        [Fact]
        public async Task HGetAll_OmitsPipelineDroppedColumn_AndKeepsMaskedValueMasked()
        {
            // The accounts model has 4 columns; the pipeline returns a row that OMITS secret_note entirely
            // (policy-dropped) and MASKS ssn. HGETALL must reflect exactly the returned columns.
            var (_, ctx) = Arrange(Tenant1, protocol: RespProtocol.Resp2);
            var reply = await new RespHGetAllCommandHandler().HandleAsync(Ctx(ctx, "HGETALL", "accounts:1"), CancellationToken.None);

            var fields = FlatHashOf(reply.Should().BeOfType<RespArray>().Which.Items!);
            fields.Should().Equal(("id", "1"), ("holder", "acme"), ("ssn", "***")); // masked value returned as-is
            fields.Select(f => f.Item1).Should().NotContain("secret_note",
                "a pipeline-dropped column must never be re-added from the model");
        }

        [Fact]
        public async Task HGet_DeniedColumn_ReturnsNull_NotADistinctError()
        {
            var (_, ctx) = Arrange(Tenant1, protocol: RespProtocol.Resp2);

            // secret_note exists in the model but the pipeline omitted it → Null, exactly like...
            var denied = await new RespHGetCommandHandler().HandleAsync(Ctx(ctx, "HGET", "accounts:1", "secret_note"), CancellationToken.None);
            // ...an entirely unknown field.
            var unknown = await new RespHGetCommandHandler().HandleAsync(Ctx(ctx, "HGET", "accounts:1", "no_such_field"), CancellationToken.None);

            denied.Should().BeOfType<RespBulkString>().Which.Value.Should().BeNull();
            unknown.Should().BeOfType<RespBulkString>().Which.Value.Should().BeNull();
        }

        [Fact]
        public async Task HGet_MaskedColumn_ReturnsMaskedValue_NotUnmasked()
        {
            var (_, ctx) = Arrange(Tenant1, protocol: RespProtocol.Resp2);
            var reply = await new RespHGetCommandHandler().HandleAsync(Ctx(ctx, "HGET", "accounts:1", "ssn"), CancellationToken.None);
            BulkText(reply).Should().Be("***");
        }

        // ---- HGET: single column / missing / hidden -----------------------

        [Fact]
        public async Task HGet_VisibleColumn_ReturnsValue()
        {
            var (executor, ctx) = Arrange(Tenant1, protocol: RespProtocol.Resp2);
            var reply = await new RespHGetCommandHandler().HandleAsync(Ctx(ctx, "HGET", "users:1", "name"), CancellationToken.None);
            BulkText(reply).Should().Be("alice");
            executor.Intents.Should().HaveCount(1); // reuses the same read path
        }

        [Fact]
        public async Task HGet_CompositePk_ReturnsColumn()
        {
            var (_, ctx) = Arrange(Tenant1, protocol: RespProtocol.Resp2);
            var reply = await new RespHGetCommandHandler().HandleAsync(Ctx(ctx, "HGET", "order_items:10:2", "sku"), CancellationToken.None);
            BulkText(reply).Should().Be("widget");
        }

        [Fact]
        public async Task HGet_MissingRow_ReturnsNull_Resp2BulkNull_Resp3Null()
        {
            var (_, resp2) = Arrange(Tenant1, protocol: RespProtocol.Resp2);
            var (_, resp3) = Arrange(Tenant1, protocol: RespProtocol.Resp3);

            var r2 = await new RespHGetCommandHandler().HandleAsync(Ctx(resp2, "HGET", "users:999", "name"), CancellationToken.None);
            var r3 = await new RespHGetCommandHandler().HandleAsync(Ctx(resp3, "HGET", "users:999", "name"), CancellationToken.None);

            r2.Should().BeOfType<RespBulkString>().Which.Value.Should().BeNull();
            r3.Should().BeOfType<RespNull>();
        }

        [Fact]
        public async Task HGet_TenantHiddenRow_ReturnsNull()
        {
            var (_, ctx) = Arrange(Tenant1, protocol: RespProtocol.Resp2);
            var reply = await new RespHGetCommandHandler().HandleAsync(Ctx(ctx, "HGET", "users:3", "name"), CancellationToken.None);
            reply.Should().BeOfType<RespBulkString>().Which.Value.Should().BeNull(
                "a hidden row's column must be indistinguishable from a missing key");
        }

        // ---- arity edges ---------------------------------------------------

        [Fact]
        public async Task HGetAll_WrongArgumentCount_IsCleanError()
        {
            var (_, ctx) = Arrange(Tenant1, protocol: RespProtocol.Resp2);
            var reply = await new RespHGetAllCommandHandler().HandleAsync(Ctx(ctx, "HGETALL"), CancellationToken.None);
            reply.Should().BeOfType<RespError>().Which.Message.Should().Be(RespProtocol.WrongArgCount("HGETALL"));
        }

        [Fact]
        public async Task HGet_WrongArgumentCount_IsCleanError()
        {
            var (_, ctx) = Arrange(Tenant1, protocol: RespProtocol.Resp2);
            var reply = await new RespHGetCommandHandler().HandleAsync(Ctx(ctx, "HGET", "users:1"), CancellationToken.None);
            reply.Should().BeOfType<RespError>().Which.Message.Should().Be(RespProtocol.WrongArgCount("HGET"));
        }

        [Fact]
        public async Task HGet_MalformedKey_IsCleanError_NeverExecutes()
        {
            // The trailing <field> must not be mis-parsed as a second key segment: a one-segment key for a
            // two-column PK is still an honest arity error, and nothing executes.
            var (executor, ctx) = Arrange(Tenant1, protocol: RespProtocol.Resp2);
            var reply = await new RespHGetCommandHandler().HandleAsync(Ctx(ctx, "HGET", "order_items:10", "sku"), CancellationToken.None);

            reply.Should().BeOfType<RespError>().Which.Message.Should().StartWith("ERR ");
            executor.Intents.Should().BeEmpty();
        }

        // ---- fail-closed over the wire (slice-1 fixture) ------------------

        [Fact]
        public async Task HGetAll_BeforeAuth_IsRefusedWithNoAuth()
        {
            var store = new FakeRespCredentialStore();
            var services = new ServiceCollection()
                .AddSingleton<IQueryIntentExecutor>(new HashIntentExecutor(BuildModel()))
                .BuildServiceProvider();
            var options = new RespWireOptions { RequireAuthentication = true };

            await using var fixture = await RespFixture.StartAsync(store, services, options, new RespHGetAllCommandHandler());
            await fixture.Client.SendCommandAsync("HGETALL", "users:1");
            var reply = await fixture.Client.ReadReplyAsync();

            reply.Should().BeOfType<RespError>().Which.Message.Should().StartWith("NOAUTH");
        }

        // ---- fixtures ------------------------------------------------------

        private static readonly IDictionary<string, object?> Tenant1 =
            new Dictionary<string, object?> { ["tenantId"] = 1 };

        private static (HashIntentExecutor executor, IServiceProvider services) Arrange(
            IDictionary<string, object?> tenant, int protocol)
        {
            var executor = new HashIntentExecutor(BuildModel());
            var services = new ServiceCollection()
                .AddSingleton<IQueryIntentExecutor>(executor)
                .BuildServiceProvider();
            var session = new RespSession(1) { Protocol = protocol };
            session.Authenticate(tenant);
            _sessions[services] = session;
            return (executor, services);
        }

        private static readonly Dictionary<IServiceProvider, RespSession> _sessions = new();

        private static RespCommandContext Ctx(IServiceProvider services, params string[] arguments) =>
            new(arguments, _sessions[services], services, null);

        private static string BulkText(RespValue value) =>
            Encoding.UTF8.GetString(value.Should().BeOfType<RespBulkString>().Which.Value!);

        /// <summary>Flattens a RESP3 map to (field, valueText) tuples in order.</summary>
        private static List<(string, string)> HashOf(IReadOnlyList<KeyValuePair<RespValue, RespValue>> entries) =>
            entries.Select(e => (BulkText(e.Key), BulkText(e.Value))).ToList();

        /// <summary>Flattens a RESP2 alternating field,value array to (field, valueText) tuples.</summary>
        private static List<(string, string)> FlatHashOf(IReadOnlyList<RespValue> items)
        {
            items.Count.Should().Match(n => n % 2 == 0, "HGETALL is an even-length field,value stream");
            var pairs = new List<(string, string)>();
            for (var i = 0; i < items.Count; i += 2)
                pairs.Add((BulkText(items[i]), BulkText(items[i + 1])));
            return pairs;
        }

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

            // The model declares 4 columns; the pipeline (modeled by HashIntentExecutor's seed) returns a
            // row that OMITS secret_note and MASKS ssn, so the visible-column set is a strict subset.
            var accounts = FakeTable("accounts",
                Col("id", "int", 1, pk: true),
                Col("holder", "varchar", 2),
                Col("ssn", "varchar", 3),
                Col("secret_note", "varchar", 4));

            var model = Substitute.For<IDbModel>();
            model.Tables.Returns(new[] { users, orderItems, accounts });
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
    /// A filter-aware fake read executor for the hash tests. It honors the intent's primary-key
    /// <c>_eq</c>/<c>_in</c> constraints and a tenant predicate (a row with <c>tenant_id</c> is visible
    /// only to a matching <c>tenantId</c>), and returns each seeded row VERBATIM — so the accounts row,
    /// seeded with secret_note omitted and ssn masked, faithfully models a transformer pipeline that
    /// dropped/masked those columns. Records every intent so reuse-of-the-read-path is assertable.
    /// </summary>
    internal sealed class HashIntentExecutor : IQueryIntentExecutor
    {
        private readonly IDbModel _model;
        private readonly Dictionary<string, List<Dictionary<string, object?>>> _store;

        public List<QueryIntent> Intents { get; } = new();

        public HashIntentExecutor(IDbModel model)
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
                // secret_note is absent (pipeline-dropped); ssn carries a masked value, not the real one.
                ["accounts"] = new()
                {
                    Row(("id", 1), ("holder", "acme"), ("ssn", "***")),
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

        private static string Token(object? value) =>
            Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

        private static Dictionary<string, object?> Row(params (string Column, object? Value)[] cells) =>
            cells.ToDictionary(c => c.Column, c => c.Value, StringComparer.Ordinal);
    }
}
