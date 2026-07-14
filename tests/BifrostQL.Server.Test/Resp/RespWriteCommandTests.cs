using System.Globalization;
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
    /// Handler-level tests for the RESP slice-5 WRITE surface (SET/HSET/DEL). They prove, against a
    /// tenant-aware fake mutation executor that models the pipeline's WHERE narrowing, that:
    /// writes are OFF BY DEFAULT (disabled → clean -ERR and NO intent executed); enabling them routes
    /// SET/HSET/DEL through <see cref="IMutationIntentExecutor"/> under the session identity with the
    /// right table/op/columns/PK; an identity cannot mutate another tenant's row (zero rows affected,
    /// row unchanged); identity is fail-closed before AUTH over the wire; and unknown table/column and
    /// malformed JSON are clean -ERRs that execute nothing. Composite PKs flow positionally, never [0].
    /// </summary>
    public sealed class RespWriteCommandTests
    {
        // ---- OFF BY DEFAULT (the non-negotiable gate) -----------------------

        [Fact]
        public async Task Set_WritesDisabled_IsCleanError_AndNeverExecutes()
        {
            var (executor, services, session) = Arrange(Tenant1, enableWrites: false);
            var reply = await new RespSetCommandHandler().HandleAsync(
                Ctx(services, session, "SET", "users:1", "{\"name\":\"changed\"}"), CancellationToken.None);

            reply.Should().BeOfType<RespError>().Which.Message.Should().Be(RespProtocol.WritesDisabledError);
            executor.Intents.Should().BeEmpty("a disabled write surface must build no mutation intent");
            executor.Row("users", 1)!["name"].Should().Be("alice", "nothing was written");
        }

        [Fact]
        public async Task HSet_WritesDisabled_IsCleanError_AndNeverExecutes()
        {
            var (executor, services, session) = Arrange(Tenant1, enableWrites: false);
            var reply = await new RespHSetCommandHandler().HandleAsync(
                Ctx(services, session, "HSET", "users:1", "name", "changed"), CancellationToken.None);

            reply.Should().BeOfType<RespError>().Which.Message.Should().Be(RespProtocol.WritesDisabledError);
            executor.Intents.Should().BeEmpty();
        }

        [Fact]
        public async Task Del_WritesDisabled_IsCleanError_AndNeverExecutes()
        {
            var (executor, services, session) = Arrange(Tenant1, enableWrites: false);
            var reply = await new RespDelCommandHandler().HandleAsync(
                Ctx(services, session, "DEL", "users:1"), CancellationToken.None);

            reply.Should().BeOfType<RespError>().Which.Message.Should().Be(RespProtocol.WritesDisabledError);
            executor.Intents.Should().BeEmpty();
            executor.Row("users", 1).Should().NotBeNull("the row must still exist");
        }

        // ---- SET (update through the pipeline) ------------------------------

        [Fact]
        public async Task Set_EnabledWrites_UpdatesRow_ThroughMutationIntent_UnderIdentity()
        {
            var (executor, services, session) = Arrange(Tenant1, enableWrites: true);
            var reply = await new RespSetCommandHandler().HandleAsync(
                Ctx(services, session, "SET", "users:1", "{\"name\":\"alice2\"}"), CancellationToken.None);

            reply.Should().BeOfType<RespSimpleString>().Which.Value.Should().Be(RespProtocol.Ok);

            executor.Intents.Should().HaveCount(1);
            var intent = executor.Intents[0];
            intent.Table.Should().Be("users");
            intent.Action.Should().Be(MutationIntentAction.Update);
            intent.Data.Should().Contain(new KeyValuePair<string, object?>("name", "alice2"));
            intent.Data.Should().NotContainKey("id", "the PK comes from the key, never the SET data");
            intent.PrimaryKey.Should().Equal(new object?[] { 1L }, "the PK is carried positionally from the key");
            // The audit ACTOR resolves from the identity: the mutation runs under the session context.
            intent.UserContext.Should().Contain(new KeyValuePair<string, object?>("tenantId", 1));

            executor.Row("users", 1)!["name"].Should().Be("alice2", "the pipeline applied the update");
        }

        [Fact]
        public async Task Set_MissingRow_IsNoOp_StillRepliesOk()
        {
            // SET is an UPDATE, not an insert: an absent PK is narrowed to zero rows and nothing is
            // created, but Redis SET's fire-and-forget status is still +OK.
            var (executor, services, session) = Arrange(Tenant1, enableWrites: true);
            var reply = await new RespSetCommandHandler().HandleAsync(
                Ctx(services, session, "SET", "users:999", "{\"name\":\"ghost\"}"), CancellationToken.None);

            reply.Should().BeOfType<RespSimpleString>().Which.Value.Should().Be(RespProtocol.Ok);
            executor.Row("users", 999).Should().BeNull("SET must not insert a missing row");
        }

        [Fact]
        public async Task Set_CompositePk_UpdatesInSchemaOrder()
        {
            var (executor, services, session) = Arrange(Tenant1, enableWrites: true);
            var reply = await new RespSetCommandHandler().HandleAsync(
                Ctx(services, session, "SET", "order_items:10:2", "{\"sku\":\"gizmo\"}"), CancellationToken.None);

            reply.Should().BeOfType<RespSimpleString>().Which.Value.Should().Be(RespProtocol.Ok);
            executor.Intents[0].PrimaryKey.Should().Equal(new object?[] { 10L, 2L });
            executor.CompositeRow("order_items", 10, 2)!["sku"].Should().Be("gizmo");
        }

        [Fact]
        public async Task Set_BodyPrimaryKeyMatchingKey_IsAllowed_AndNotWritten()
        {
            var (executor, services, session) = Arrange(Tenant1, enableWrites: true);
            var reply = await new RespSetCommandHandler().HandleAsync(
                Ctx(services, session, "SET", "users:1", "{\"id\":1,\"name\":\"alice3\"}"), CancellationToken.None);

            reply.Should().BeOfType<RespSimpleString>().Which.Value.Should().Be(RespProtocol.Ok);
            executor.Intents[0].Data.Should().NotContainKey("id", "a matching body PK is dropped, not set");
            executor.Intents[0].Data.Should().ContainKey("name");
        }

        [Fact]
        public async Task Set_BodyPrimaryKeyConflictingWithKey_IsCleanError_NeverExecutes()
        {
            var (executor, services, session) = Arrange(Tenant1, enableWrites: true);
            var reply = await new RespSetCommandHandler().HandleAsync(
                Ctx(services, session, "SET", "users:1", "{\"id\":2,\"name\":\"x\"}"), CancellationToken.None);

            reply.Should().BeOfType<RespError>().Which.Message.Should().StartWith("ERR ");
            executor.Intents.Should().BeEmpty("a SET must never move a row's identity");
        }

        [Fact]
        public async Task Set_NoWritableColumns_IsCleanError_NeverExecutes()
        {
            var (executor, services, session) = Arrange(Tenant1, enableWrites: true);
            var reply = await new RespSetCommandHandler().HandleAsync(
                Ctx(services, session, "SET", "users:1", "{}"), CancellationToken.None);

            reply.Should().BeOfType<RespError>().Which.Message.Should().StartWith("ERR ");
            executor.Intents.Should().BeEmpty();
        }

        [Fact]
        public async Task Set_MalformedJson_IsCleanError_NeverExecutes()
        {
            var (executor, services, session) = Arrange(Tenant1, enableWrites: true);
            var reply = await new RespSetCommandHandler().HandleAsync(
                Ctx(services, session, "SET", "users:1", "not-json"), CancellationToken.None);

            reply.Should().BeOfType<RespError>().Which.Message.Should().StartWith("ERR ");
            executor.Intents.Should().BeEmpty("a malformed body must never execute");
        }

        [Fact]
        public async Task Set_UnknownColumn_IsCleanError_NeverExecutes()
        {
            var (executor, services, session) = Arrange(Tenant1, enableWrites: true);
            var reply = await new RespSetCommandHandler().HandleAsync(
                Ctx(services, session, "SET", "users:1", "{\"nope\":\"x\"}"), CancellationToken.None);

            reply.Should().BeOfType<RespError>().Which.Message.Should().Contain("unknown column 'nope'");
            executor.Intents.Should().BeEmpty();
        }

        [Fact]
        public async Task Set_ValueWithSqlMetacharacters_IsBoundAsData_NotConcatenated()
        {
            // The value is carried as intent Data (a bound parameter downstream), never spliced into text.
            var (executor, services, session) = Arrange(Tenant1, enableWrites: true);
            await new RespSetCommandHandler().HandleAsync(
                Ctx(services, session, "SET", "users:1", "{\"name\":\"'; DROP TABLE users;--\"}"), CancellationToken.None);

            executor.Intents[0].Data["name"].Should().Be("'; DROP TABLE users;--");
        }

        // ---- HSET (named-column update) -------------------------------------

        [Fact]
        public async Task HSet_SetsNamedColumns_ReturnsFieldCount()
        {
            var (executor, services, session) = Arrange(Tenant1, enableWrites: true);
            var reply = await new RespHSetCommandHandler().HandleAsync(
                Ctx(services, session, "HSET", "users:1", "name", "bob"), CancellationToken.None);

            reply.Should().BeOfType<RespInteger>().Which.Value.Should().Be(1);
            var intent = executor.Intents.Should().ContainSingle().Which;
            intent.Action.Should().Be(MutationIntentAction.Update);
            intent.Data.Should().Contain(new KeyValuePair<string, object?>("name", "bob"));
            intent.PrimaryKey.Should().Equal(new object?[] { 1L });
            executor.Row("users", 1)!["name"].Should().Be("bob");
        }

        [Fact]
        public async Task HSet_PrimaryKeyColumn_IsRefused_NeverExecutes()
        {
            var (executor, services, session) = Arrange(Tenant1, enableWrites: true);
            var reply = await new RespHSetCommandHandler().HandleAsync(
                Ctx(services, session, "HSET", "users:1", "id", "9"), CancellationToken.None);

            reply.Should().BeOfType<RespError>().Which.Message.Should().Contain("primary-key column 'id'");
            executor.Intents.Should().BeEmpty();
        }

        [Fact]
        public async Task HSet_OddArgumentCount_IsCleanError()
        {
            var (_, services, session) = Arrange(Tenant1, enableWrites: true);
            var reply = await new RespHSetCommandHandler().HandleAsync(
                Ctx(services, session, "HSET", "users:1", "name"), CancellationToken.None);

            reply.Should().BeOfType<RespError>().Which.Message.Should().Be(RespProtocol.WrongArgCount("HSET"));
        }

        // ---- DEL (delete through the pipeline) ------------------------------

        [Fact]
        public async Task Del_DeletesRow_ThroughDeleteIntent_ReturnsCount()
        {
            var (executor, services, session) = Arrange(Tenant1, enableWrites: true);
            var reply = await new RespDelCommandHandler().HandleAsync(
                Ctx(services, session, "DEL", "users:1"), CancellationToken.None);

            reply.Should().BeOfType<RespInteger>().Which.Value.Should().Be(1);
            // The adapter routes a DELETE and lets the pipeline decide hard vs soft — it never bypasses.
            executor.Intents.Should().ContainSingle().Which.Action.Should().Be(MutationIntentAction.Delete);
            executor.Row("users", 1).Should().BeNull();
        }

        [Fact]
        public async Task Del_MultipleKeys_CountsOnlyRowsActuallyDeleted()
        {
            var (executor, services, session) = Arrange(Tenant1, enableWrites: true);
            // users:1 exists, users:999 missing → only one row deleted.
            var reply = await new RespDelCommandHandler().HandleAsync(
                Ctx(services, session, "DEL", "users:1", "users:999"), CancellationToken.None);

            reply.Should().BeOfType<RespInteger>().Which.Value.Should().Be(1);
        }

        [Fact]
        public async Task Del_OneBadKey_RejectsWholeCommand_NoPartialDelete()
        {
            var (executor, services, session) = Arrange(Tenant1, enableWrites: true);
            var reply = await new RespDelCommandHandler().HandleAsync(
                Ctx(services, session, "DEL", "users:1", "ghosts:1"), CancellationToken.None);

            reply.Should().BeOfType<RespError>().Which.Message.Should().Contain("unknown table 'ghosts'");
            executor.Intents.Should().BeEmpty("a malformed key must not delete any row");
            executor.Row("users", 1).Should().NotBeNull();
        }

        [Fact]
        public async Task Del_CompositePk_DeletesInSchemaOrder()
        {
            var (executor, services, session) = Arrange(Tenant1, enableWrites: true);
            var reply = await new RespDelCommandHandler().HandleAsync(
                Ctx(services, session, "DEL", "order_items:10:2"), CancellationToken.None);

            reply.Should().BeOfType<RespInteger>().Which.Value.Should().Be(1);
            executor.Intents[0].PrimaryKey.Should().Equal(new object?[] { 10L, 2L });
            executor.CompositeRow("order_items", 10, 2).Should().BeNull();
        }

        // ---- TENANT SCOPING: A cannot mutate B's row ------------------------

        [Fact]
        public async Task Set_OnAnotherTenantsRow_AffectsZeroRows_RowUnchanged()
        {
            // Tenant 1 targets users:3, which belongs to tenant 2. The pipeline's tenant predicate
            // narrows it out, so the update touches nothing — A cannot write B's row.
            var (executor, services, session) = Arrange(Tenant1, enableWrites: true);
            await new RespSetCommandHandler().HandleAsync(
                Ctx(services, session, "SET", "users:3", "{\"name\":\"hacked\"}"), CancellationToken.None);

            executor.Row("users", 3)!["name"].Should().Be("carol", "an out-of-scope row must be untouched");
        }

        [Fact]
        public async Task Del_OnAnotherTenantsRow_DeletesNothing_ReturnsZero()
        {
            var (executor, services, session) = Arrange(Tenant1, enableWrites: true);
            var reply = await new RespDelCommandHandler().HandleAsync(
                Ctx(services, session, "DEL", "users:3"), CancellationToken.None);

            reply.Should().BeOfType<RespInteger>().Which.Value.Should().Be(0, "an out-of-scope key deletes nothing");
            executor.Row("users", 3).Should().NotBeNull("B's row must survive A's DEL");
        }

        [Fact]
        public async Task HSet_OnAnotherTenantsRow_WritesNothing()
        {
            var (executor, services, session) = Arrange(Tenant1, enableWrites: true);
            await new RespHSetCommandHandler().HandleAsync(
                Ctx(services, session, "HSET", "users:3", "name", "hacked"), CancellationToken.None);

            executor.Row("users", 3)!["name"].Should().Be("carol");
        }

        // ---- identity fail-closed over the wire (slice-1 fixture) -----------

        [Fact]
        public async Task Set_BeforeAuth_IsRefusedWithNoAuth_NeverExecutes()
        {
            var store = new FakeRespCredentialStore();
            var mutation = new FakeMutationIntentExecutor(BuildModel());
            var services = new ServiceCollection()
                .AddSingleton<IQueryIntentExecutor>(new FakeIntentExecutor(BuildModel()))
                .AddSingleton<IMutationIntentExecutor>(mutation)
                .AddSingleton(new RespWireOptions { EnableWrites = true })
                .BuildServiceProvider();
            var options = new RespWireOptions { RequireAuthentication = true, EnableWrites = true };

            await using var fixture = await RespFixture.StartAsync(store, services, options, new RespSetCommandHandler());
            await fixture.Client.SendCommandAsync("SET", "users:1", "{\"name\":\"x\"}");
            var reply = await fixture.Client.ReadReplyAsync();

            reply.Should().BeOfType<RespError>().Which.Message.Should().StartWith("NOAUTH");
            mutation.Intents.Should().BeEmpty("an unauthenticated write must never reach the pipeline");
        }

        // ---- fixtures -------------------------------------------------------

        private static readonly IDictionary<string, object?> Tenant1 =
            new Dictionary<string, object?> { ["tenantId"] = 1 };

        private static (FakeMutationIntentExecutor executor, IServiceProvider services, RespSession session) Arrange(
            IDictionary<string, object?> tenant, bool enableWrites)
        {
            var model = BuildModel();
            var executor = new FakeMutationIntentExecutor(model);
            var services = new ServiceCollection()
                .AddSingleton<IQueryIntentExecutor>(new FakeIntentExecutor(model))
                .AddSingleton<IMutationIntentExecutor>(executor)
                .AddSingleton(new RespWireOptions { EnableWrites = enableWrites })
                .BuildServiceProvider();
            var session = new RespSession(1);
            session.Authenticate(new Dictionary<string, object?>(tenant));
            return (executor, services, session);
        }

        private static RespCommandContext Ctx(IServiceProvider services, RespSession session, params string[] arguments) =>
            new(arguments, session, services, null);

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
            model.GetTableFromDbName(Arg.Any<string>()).Returns(ci =>
                model.Tables.First(t => string.Equals(t.DbName, ci.Arg<string>(), StringComparison.OrdinalIgnoreCase)));
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
    /// A tenant-aware fake mutation executor. It models the mutation pipeline's WHERE narrowing: an
    /// UPDATE/DELETE addresses a row by its positional primary key AND, when the row carries a
    /// <c>tenant_id</c>, only when the intent's <c>tenantId</c> matches — so a write against another
    /// tenant's row simply affects zero rows and leaves the store untouched. Records every intent so
    /// routing, identity propagation and off-by-default behavior are assertable.
    /// </summary>
    internal sealed class FakeMutationIntentExecutor : IMutationIntentExecutor
    {
        private readonly IDbModel _model;
        private readonly Dictionary<string, List<Dictionary<string, object?>>> _store;

        public List<MutationIntent> Intents { get; } = new();

        public FakeMutationIntentExecutor(IDbModel model)
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
                    Row(("order_id", 10), ("line_no", 2), ("sku", "widget")),
                },
            };
        }

        public IReadOnlyDictionary<string, object?>? Row(string table, object pk) =>
            _store[table].FirstOrDefault(r => Token(r["id"]) == Token(pk));

        public IReadOnlyDictionary<string, object?>? CompositeRow(string table, object first, object second) =>
            _store[table].FirstOrDefault(r => Token(r["order_id"]) == Token(first) && Token(r["line_no"]) == Token(second));

        public Task<MutationIntentResult> ExecuteAsync(MutationIntent intent, CancellationToken cancellationToken = default)
        {
            Intents.Add(intent);
            var table = _model.Tables.First(t => string.Equals(t.DbName, intent.Table, StringComparison.OrdinalIgnoreCase));
            var keyColumns = table.KeyColumns.ToList();
            var rows = _store[intent.Table];

            var match = rows.FirstOrDefault(r =>
                keyColumns.Select((c, i) => Token(r[c.ColumnName]) == Token(intent.PrimaryKey![i])).All(x => x)
                && Visible(r, intent.UserContext));

            var affected = 0;
            if (match is not null)
            {
                if (intent.Action == MutationIntentAction.Update)
                    foreach (var kv in intent.Data)
                        match[kv.Key] = kv.Value;
                else if (intent.Action == MutationIntentAction.Delete)
                    rows.Remove(match);
                affected = 1;
            }
            return Task.FromResult(new MutationIntentResult { Value = affected });
        }

        public Task<MutationBatchIntentResult> ExecuteBatchAsync(
            MutationBatchIntent intent, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("the RESP write slice does not use batch intents");

        private static bool Visible(IReadOnlyDictionary<string, object?> row, IDictionary<string, object?> userContext) =>
            !row.ContainsKey("tenant_id")
            || !userContext.TryGetValue("tenantId", out var tenant)
            || Token(row["tenant_id"]) == Token(tenant);

        private static string Token(object? value) =>
            Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

        private static Dictionary<string, object?> Row(params (string Column, object? Value)[] cells) =>
            cells.ToDictionary(c => c.Column, c => c.Value, StringComparer.Ordinal);
    }
}
