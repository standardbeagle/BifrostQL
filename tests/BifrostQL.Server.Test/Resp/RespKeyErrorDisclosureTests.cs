using BifrostQL.Core.Model;
using BifrostQL.Server.Resp;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test.Resp
{
    /// <summary>
    /// A RESP key error must describe the CALLER'S key, not the model behind it. Key parsing runs
    /// against the raw <see cref="IDbModel"/> before any policy filter, so its diagnostics reach a
    /// caller who may have no read access to the table at all: naming the primary-key columns and
    /// their database types turns a rejected GET into schema introspection with no authorization
    /// behind it (protocol-adapter-security invariant 3 — never build client-facing wire text from
    /// the raw model).
    ///
    /// <para>Position and arity are enough to fix a malformed key. A caller does not need the
    /// column names to learn that it supplied one segment where two were expected.</para>
    /// </summary>
    public sealed class RespKeyErrorDisclosureTests
    {
        [Fact]
        public void An_arity_mismatch_reports_counts_only_never_the_key_column_names()
        {
            var parsed = RespReadEngine.ParseKey(Model(), "ledger_entries:7");

            parsed.Ok.Should().BeFalse();
            parsed.Error.Should().NotContainAny("ledger_id", "entry_seq", "posted_amount")
                .And.NotContain("bigint");
            // Still actionable: the caller learns the shape of the mistake it made.
            parsed.Error.Should().Contain("1").And.Contain("2");
        }

        [Fact]
        public void An_unparseable_segment_reports_its_position_never_the_column_or_its_type()
        {
            var parsed = RespReadEngine.ParseKey(Model(), "ledger_entries:7:not-a-number");

            parsed.Ok.Should().BeFalse();
            parsed.Error.Should().NotContainAny("entry_seq", "ledger_id", "bigint", "int");
            parsed.Error.Should().Contain("not-a-number", "the caller's own input may be echoed back");
        }

        [Fact]
        public void A_table_without_a_primary_key_is_not_named_back_to_the_caller()
        {
            // The caller addressed "audit_log", so echoing THAT is its own input; the resolved
            // model object's name is not, and the two differ whenever a GraphQL alias is in play.
            var parsed = RespReadEngine.ParseKey(Model(), "audit_log:1");

            parsed.Ok.Should().BeFalse();
            parsed.Error.Should().NotContain("AuditLogRaw");
        }

        private static IDbModel Model()
        {
            var ledger = FakeTable(
                "ledger_entries", "ledger_entries",
                Col("ledger_id", "int", 1, pk: true),
                Col("entry_seq", "bigint", 2, pk: true),
                Col("posted_amount", "decimal", 3));

            // DbName deliberately differs from the name a caller addresses it by.
            var audit = FakeTable("AuditLogRaw", "audit_log", Col("note", "varchar", 1));

            var model = Substitute.For<IDbModel>();
            model.Tables.Returns(new[] { ledger, audit });
            return model;
        }

        private static ColumnDto Col(string name, string type, int ordinal, bool pk = false) =>
            new()
            {
                ColumnName = name,
                GraphQlName = name,
                DataType = type,
                OrdinalPosition = ordinal,
                IsPrimaryKey = pk,
            };

        private static IDbTable FakeTable(string dbName, string graphQlName, params ColumnDto[] columns)
        {
            var table = Substitute.For<IDbTable>();
            table.DbName.Returns(dbName);
            table.GraphQlName.Returns(graphQlName);
            table.TableSchema.Returns("dbo");
            table.Columns.Returns(columns);
            table.KeyColumns.Returns(columns.Where(c => c.IsPrimaryKey).ToArray());
            return table;
        }
    }
}
