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

        // ---- fixtures --------------------------------------------------------

        private static (FakeScanExecutor Executor, IServiceProvider Services) Arrange()
        {
            var executor = new FakeScanExecutor(BuildModel());
            var services = new ServiceCollection()
                .AddSingleton<IQueryIntentExecutor>(executor)
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

            var model = Substitute.For<IDbModel>();
            model.Tables.Returns(new[] { widgets });
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

        private static Task<RespValue> Handle(IServiceProvider services, params string[] arguments) =>
            new RespScanCommandHandler().HandleAsync(
                new RespCommandContext(arguments, Session, services, null), CancellationToken.None);

    }
}
