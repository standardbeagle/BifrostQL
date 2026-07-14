using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Pgwire;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test.Pgwire
{
    /// <summary>
    /// Translator-level tests for the pgwire SQL-subset parser: they pin the
    /// programmatic <see cref="GqlObjectQuery"/> the translator builds (columns,
    /// WHERE filter, ORDER BY, LIMIT/OFFSET, joins) and that every out-of-subset
    /// statement is rejected honestly — without a socket round-trip. Real row
    /// filtering, tenant scoping, and join flattening are proven end-to-end in the
    /// Core SQLite intent tests; here the executor only supplies the model.
    /// </summary>
    public sealed class PgSubsetQueryTranslatorTests
    {
        private static readonly PgSubsetQueryTranslator Translator = new();

        private static async Task<PgQueryPlan> Translate(string sql, IDbModel model)
        {
            var executor = Substitute.For<IQueryIntentExecutor>();
            executor.GetModelAsync(Arg.Any<string?>()).Returns(Task.FromResult(model));
            return await Translator.TranslateAsync(
                executor, sql, new Dictionary<string, object?>(), null, CancellationToken.None);
        }

        private static async Task<Exception> Rejected(string sql, IDbModel model)
        {
            var executor = Substitute.For<IQueryIntentExecutor>();
            executor.GetModelAsync(Arg.Any<string?>()).Returns(Task.FromResult(model));
            return await Record.ExceptionAsync(() => Translator.TranslateAsync(
                executor, sql, new Dictionary<string, object?>(), null, CancellationToken.None));
        }

        [Fact]
        public async Task Where_BuildsFilter_AndProjectsSelectedColumns()
        {
            var plan = await Translate("SELECT id, name FROM users WHERE id > 5 AND name = 'alice'", UsersOnlyModel());

            plan.Columns.Select(c => c.Name).Should().Equal("id", "name");
            plan.Intent.Query.Filter.Should().NotBeNull("a WHERE clause must produce a filter the security pipeline can extend");
            plan.Intent.Query.ScalarColumns.Select(c => c.GraphQlDbName).Should().Contain(new[] { "id", "name" });
        }

        [Fact]
        public async Task OrderByLimitOffset_ReflectedInIntent()
        {
            var plan = await Translate("SELECT id FROM users ORDER BY name DESC, id ASC LIMIT 10 OFFSET 5", UsersOnlyModel());

            plan.Intent.Query.Sort.Should().Equal("name_desc", "id_asc");
            plan.Intent.Query.Limit.Should().Be(10);
            plan.Intent.Query.Offset.Should().Be(5);
        }

        [Fact]
        public async Task SelectStar_ProjectsEveryColumnInOrdinalOrder()
        {
            var plan = await Translate("SELECT * FROM users", UsersOnlyModel());
            plan.Columns.Select(c => c.Name).Should().Equal("id", "name", "active");
        }

        [Fact]
        public async Task InBetweenNull_AreRecognized()
        {
            var plan = await Translate(
                "SELECT id FROM users WHERE id IN (1,2,3) OR (name IS NOT NULL AND id BETWEEN 5 AND 9)",
                UsersOnlyModel());
            plan.Intent.Query.Filter.Should().NotBeNull();
        }

        [Fact]
        public async Task UnknownTable_IsRejected()
        {
            var ex = await Rejected("SELECT id FROM ghosts", UsersOnlyModel());
            ex.Should().BeOfType<PgQueryTranslationException>();
            ex!.Message.Should().Contain("ghosts");
        }

        [Fact]
        public async Task UnknownColumn_IsRejected()
        {
            var ex = await Rejected("SELECT secret FROM users", UsersOnlyModel());
            ex.Should().BeOfType<PgQueryTranslationException>();
            ex!.Message.Should().Contain("secret");
        }

        [Theory]
        [InlineData("SELECT id FROM users WHERE id IN (SELECT id FROM users)")] // subquery
        [InlineData("SELECT count(id) FROM users")]                            // function call
        [InlineData("SELECT id FROM users GROUP BY id")]                       // GROUP BY
        [InlineData("SELECT id FROM users UNION SELECT id FROM users")]        // set op
        [InlineData("SELECT id FROM users LEFT JOIN users u ON id = u.id")]    // non-inner join
        public async Task OutOfSubset_RecognizedConstructs_AreFeatureNotSupported(string sql)
        {
            var ex = await Rejected(sql, UsersOnlyModel());
            ex.Should().BeOfType<PgQueryTranslationException>()
                .Which.SqlState.Should().Be(PgWireProtocol.SqlStateFeatureNotSupported);
        }

        [Theory]
        [InlineData("UPDATE users SET name = 'x'")]                            // write
        [InlineData("DELETE FROM users WHERE id = 1")]                         // write
        [InlineData("SELECT id FROM users; DROP TABLE users")]                 // second statement
        [InlineData("SELECT id FROM users -- comment")]                        // comment
        public async Task OutOfSubset_UnrecognizedStatements_AreSyntaxError(string sql)
        {
            var ex = await Rejected(sql, UsersOnlyModel());
            ex.Should().BeOfType<PgQueryTranslationException>()
                .Which.SqlState.Should().Be(PgWireProtocol.SqlStateSyntaxError);
        }

        [Fact]
        public async Task Injection_InLiteralAndTrailingStatement_IsRejected_NotExecuted()
        {
            // The classic injection payload closes the string then appends a DROP as a
            // second statement — the parser stops at the second statement and rejects.
            var ex = await Rejected("SELECT id FROM users WHERE name = 'x'; DROP TABLE users", UsersOnlyModel());
            ex.Should().BeOfType<PgQueryTranslationException>();
        }

        [Fact]
        public async Task Join_ResolvesSingleLink_BuildsLink_AndQualifiesJoinedColumns()
        {
            var plan = await Translate(
                "SELECT o.id, u.name FROM orders o JOIN users u ON o.user_id = u.id",
                OrdersAndUsersModel());

            plan.Intent.Query.Links.Should().HaveCount(1);
            plan.Intent.Query.Links[0].TableName.Should().Be("users");
            plan.Columns.Select(c => c.Name).Should().Contain("users.name");
            // The joined table's projection must carry the requested joined column.
            plan.Intent.Query.Links[0].ScalarColumns.Select(c => c.GraphQlDbName).Should().Contain("name");
        }

        [Fact]
        public async Task Join_ToUnrelatedTable_IsRejected()
        {
            var ex = await Rejected(
                "SELECT o.id FROM orders o JOIN widgets w ON o.id = w.id",
                OrdersUsersWidgetsModel());
            ex.Should().BeOfType<PgQueryTranslationException>()
                .Which.SqlState.Should().Be(PgWireProtocol.SqlStateFeatureNotSupported);
        }

        [Fact]
        public async Task Join_WithOnClauseNotMatchingRelationship_IsRejected()
        {
            var ex = await Rejected(
                "SELECT o.id FROM orders o JOIN users u ON o.id = u.id",
                OrdersAndUsersModel());
            ex.Should().BeOfType<PgQueryTranslationException>();
        }

        // ---- model builders --------------------------------------------------

        private static ColumnDto Col(string name, string type, int ordinal, bool pk = false, bool nullable = false) =>
            new() { ColumnName = name, GraphQlName = name, DataType = type, OrdinalPosition = ordinal, IsPrimaryKey = pk, IsNullable = nullable };

        private static IDbTable Users()
        {
            var t = Substitute.For<IDbTable>();
            t.DbName.Returns("users");
            t.GraphQlName.Returns("users");
            t.TableSchema.Returns("dbo");
            t.Columns.Returns(new[]
            {
                Col("id", "int", 1, pk: true),
                Col("name", "varchar", 2, nullable: true),
                Col("active", "bit", 3),
            });
            t.SingleLinks.Returns(new Dictionary<string, TableLinkDto>());
            t.MultiLinks.Returns(new Dictionary<string, TableLinkDto>());
            return t;
        }

        private static IDbModel UsersOnlyModel()
        {
            var users = Users();
            var model = Substitute.For<IDbModel>();
            model.Tables.Returns(new[] { users });
            return model;
        }

        private static (IDbModel model, IDbTable orders, IDbTable users) BuildOrdersUsers()
        {
            var users = Users();
            var orders = Substitute.For<IDbTable>();
            orders.DbName.Returns("orders");
            orders.GraphQlName.Returns("orders");
            orders.TableSchema.Returns("dbo");
            var orderCols = new[]
            {
                Col("id", "int", 1, pk: true),
                Col("user_id", "int", 2),
                Col("total", "decimal", 3),
            };
            orders.Columns.Returns(orderCols);
            orders.MultiLinks.Returns(new Dictionary<string, TableLinkDto>());

            var link = new TableLinkDto
            {
                Name = "users",
                ParentTable = users,
                ChildTable = orders,
                ParentId = users.Columns.First(c => c.ColumnName == "id"),
                ChildId = orderCols.First(c => c.ColumnName == "user_id"),
            };
            orders.SingleLinks.Returns(new Dictionary<string, TableLinkDto> { ["users"] = link });

            var model = Substitute.For<IDbModel>();
            model.Tables.Returns(new[] { orders, users });
            return (model, orders, users);
        }

        private static IDbModel OrdersAndUsersModel() => BuildOrdersUsers().model;

        private static IDbModel OrdersUsersWidgetsModel()
        {
            var (_, orders, users) = BuildOrdersUsers();
            var widgets = Substitute.For<IDbTable>();
            widgets.DbName.Returns("widgets");
            widgets.GraphQlName.Returns("widgets");
            widgets.TableSchema.Returns("dbo");
            widgets.Columns.Returns(new[] { Col("id", "int", 1, pk: true) });
            widgets.SingleLinks.Returns(new Dictionary<string, TableLinkDto>());
            widgets.MultiLinks.Returns(new Dictionary<string, TableLinkDto>());

            var model = Substitute.For<IDbModel>();
            model.Tables.Returns(new[] { orders, users, widgets });
            return model;
        }
    }
}
