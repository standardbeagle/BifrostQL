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

        [Theory]
        [InlineData("SELECT id FROM users WHERE id = -5", -5L)]
        [InlineData("SELECT id FROM users WHERE id = +7", 7L)]
        public void NegativeAndSignedNumericLiteral_InWhere_BindsSignedValue(string sql, long expected)
        {
            // Signed numeric literals are squarely in the Phase-1 subset; the parser must
            // bind the signed value (never string-concatenate it) rather than reject '-'.
            var stmt = PgSqlSubsetParser.Parse(sql);
            var predicate = stmt.Where.Should().BeOfType<PgPredicate>().Subject;
            predicate.Value.Should().Be(expected);
        }

        [Fact]
        public void NegativeDecimalLiteral_PreservesDecimalKind()
        {
            var stmt = PgSqlSubsetParser.Parse("SELECT id FROM users WHERE total = -5.0");
            var predicate = stmt.Where.Should().BeOfType<PgPredicate>().Subject;
            predicate.Value.Should().Be(-5.0m);
        }

        [Fact]
        public void Between_NegativeBounds_AreParsed()
        {
            var stmt = PgSqlSubsetParser.Parse("SELECT id FROM users WHERE id BETWEEN -10 AND -1");
            var predicate = stmt.Where.Should().BeOfType<PgPredicate>().Subject;
            predicate.Op.Should().Be(PgCompareOp.Between);
            predicate.Values.Should().Equal(-10L, -1L);
        }

        [Theory]
        [InlineData("SELECT id FROM users LIMIT -5")]
        [InlineData("SELECT id FROM users OFFSET -1")]
        public async Task NegativeLimitOrOffset_IsSyntaxError(string sql)
        {
            // A negative LIMIT/OFFSET is not a supported literal position — it must be
            // rejected honestly as a syntax_error, never negated into a valid bound.
            var ex = await Rejected(sql, UsersOnlyModel());
            ex.Should().BeOfType<PgQueryTranslationException>()
                .Which.SqlState.Should().Be(PgWireProtocol.SqlStateSyntaxError);
        }

        [Fact]
        public async Task OversizedNumericLiteral_IsSyntaxError_NotInternalError_AndDoesNotLeak()
        {
            const string oversized = "99999999999999999999999999999";
            var ex = await Rejected($"SELECT id FROM users WHERE id = {oversized}", UsersOnlyModel());

            var translation = ex.Should().BeOfType<PgQueryTranslationException>().Subject;
            // Out-of-range literal is client input error: clean 42601, NOT the internal_error
            // an escaped OverflowException would have mapped to.
            translation.SqlState.Should().Be(PgWireProtocol.SqlStateSyntaxError);
            translation.SqlState.Should().NotBe(PgWireProtocol.SqlStateInternalError);
            // The raw oversized value (and any OverflowException text) must not leak (invariant 3).
            translation.Message.Should().NotContain(oversized);
            translation.Message.Should().NotContain("Overflow");
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

        // ---- join-edge visibility: a hidden link must never be described --------
        //
        // Table/column resolution deliberately stays on the RAW model — a policy-denied
        // object still builds an intent so the pipeline's authoritative "permission
        // denied." reaches the caller (invariant 10; pinned by the conformance kit).
        // Relationship METADATA is different: the ON-mismatch rejection names the
        // link's own FK/PK columns — identifiers the caller never supplied — and
        // PgQueryError forwards the text verbatim. A link whose key columns the caller
        // may not read is therefore treated as ABSENT (SchemaReadVisibility edge rule).

        [Theory]
        [InlineData("SELECT o.id FROM orders o JOIN users u ON o.id = u.id")]      // wrong ON
        [InlineData("SELECT o.id FROM orders o JOIN users u ON o.user_id = u.id")] // right ON
        public async Task Join_OverLinkWithReadDeniedKeyColumn_RejectsAsNoRelationship_WithoutNamingIt(string sql)
        {
            // orders.user_id is read-denied, so the orders→users link is not a visible
            // edge. Pre-fix the wrong-ON rejection printed the relationship's true
            // FK/PK column names ("user_id = id") straight from the raw model.
            var model = OrdersAndUsersModel(ordersReadDeny: "user_id");

            var ex = await Rejected(sql, model);

            ex.Should().BeOfType<PgQueryTranslationException>();
            ex!.Message.Should().Contain("no schema relationship connects",
                "a link with hidden key columns is treated as absent, like SchemaReadVisibility.IsLinkVisible");
            ex.Message.Should().NotContain("user_id = id",
                "the hidden link's key columns must not be disclosed by the rejection");
        }

        [Fact]
        public async Task SchemaQualifiedFrom_BindsTheNamedSchema_NotTheFirstNameMatch()
        {
            var model = DuplicateNameModel();

            var sales = await Translate("SELECT id FROM sales.items", model);
            var dbo = await Translate("SELECT id FROM dbo.items", model);

            sales.Intent.Query.SchemaName.Should().Be("sales",
                "the parser captures the schema qualifier and the translator must honor it");
            dbo.Intent.Query.SchemaName.Should().Be("dbo");
        }

        [Fact]
        public async Task BareNameSpanningTwoSchemas_IsRejected_NeverFirstPickBound()
        {
            var ex = await Rejected("SELECT id FROM items", DuplicateNameModel());

            ex.Should().BeOfType<PgQueryTranslationException>()
                .Which.Message.Should().Contain("ambiguous",
                    "a silent first pick would run the statement under the wrong table's policy/tenant scope");
        }

        [Theory]
        [InlineData("SELECT o.id FROM orders o JOIN users u ON o.user_id = u.id")] // right ON
        [InlineData("SELECT o.id FROM orders o JOIN users u ON o.id = u.id")]      // wrong ON
        public async Task Join_FromWhollyReadDeniedTable_StillResolves_SoThePipelineDenies(string sql)
        {
            // Table-level read deny on orders: the join must resolve on the RAW model and
            // build the intent, so the caller gets the pipeline's authoritative
            // "permission denied." — the same signal a plain SELECT on orders gets
            // (invariant 10). Rejecting as "no schema relationship" would give the same
            // condition a second wire signal, and validating the ON clause would make
            // right-vs-wrong ON an oracle over the denied table's key columns.
            var model = OrdersAndUsersModel(ordersPolicyActions: "create");

            var plan = await Translate(sql, model);

            plan.Intent.Query.Links.Should().HaveCount(1,
                "the denied table's join resolves so the pipeline can issue the denial");
        }

        [Fact]
        public async Task JoinOnMismatch_OverVisibleLink_StillNamesTheRelationshipColumns()
        {
            // With no policy in play the link is a visible edge, so the helpful
            // ON-mismatch text (naming the link's key columns) is preserved.
            var ex = await Rejected(
                "SELECT o.id FROM orders o JOIN users u ON o.id = u.id", OrdersAndUsersModel());

            ex.Should().BeOfType<PgQueryTranslationException>()
                .Which.Message.Should().Contain("user_id = id");
        }

        /// <summary>
        /// A nesting depth well past the cap but far below what the CLR stack tolerates:
        /// without a depth guard this parses SUCCESSFULLY, so the test fails on "no
        /// exception" rather than crashing the runner. It pins the cap itself.
        /// </summary>
        [Fact]
        public async Task DeeplyNestedWhere_ExceedingDepthCap_IsRejectedAsProtocolError()
        {
            const int levels = 200;
            var sql = "SELECT id FROM users WHERE "
                + new string('(', levels) + "id = 1" + new string(')', levels);

            var ex = await Rejected(sql, UsersOnlyModel());

            ex.Should().BeOfType<PgQueryTranslationException>(
                "an over-deep expression is malformed CLIENT input — it must raise the adapter's own "
                + "query-phase exception (already in the connection handler's caught family), not escape "
                + "to the host");
        }

        /// <summary>
        /// The actual attack shape from protocol-adapter-security invariant 6: ~20 KB of
        /// nested parentheses, well inside the 1 MiB message cap (a WIDTH cap, which the
        /// invariant states is insufficient). Unguarded this recurses ~60k physical frames
        /// and raises an UNCATCHABLE <c>StackOverflowException</c> that kills the whole host
        /// process — every other front door with it. Guarded, it is a clean protocol error.
        /// Pre-fix this test does not fail, it CRASHES the test host; that crash is the
        /// vulnerability.
        /// </summary>
        [Fact]
        public async Task NestedParenthesisBomb_DoesNotOverflowTheStack()
        {
            const int levels = 300_000;
            var sql = "SELECT id FROM users WHERE "
                + new string('(', levels) + "id = 1" + new string(')', levels);

            var ex = await Rejected(sql, UsersOnlyModel());

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

        /// <summary>Two tables named <c>items</c> in different schemas, no policy.</summary>
        private static IDbModel DuplicateNameModel()
        {
            IDbTable Items(string schema)
            {
                var t = Substitute.For<IDbTable>();
                t.DbName.Returns("items");
                t.GraphQlName.Returns("items");
                t.TableSchema.Returns(schema);
                t.Columns.Returns(new[] { Col("id", "int", 1, pk: true) });
                t.SingleLinks.Returns(new Dictionary<string, TableLinkDto>());
                t.MultiLinks.Returns(new Dictionary<string, TableLinkDto>());
                return t;
            }

            // Build both substitutes BEFORE configuring model.Tables — NSubstitute
            // cannot handle nested substitute configuration inside Returns(...).
            var dbo = Items("dbo");
            var sales = Items("sales");
            var tables = new[] { dbo, sales };
            var model = Substitute.For<IDbModel>();
            model.Tables.Returns(tables);
            return model;
        }

        private static (IDbModel model, IDbTable orders, IDbTable users) BuildOrdersUsers(
            string? ordersReadDeny = null, string? ordersPolicyActions = null)
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
            // A column deny needs an explicit read grant beside it: a policy that names
            // no allowed action denies table-read outright, which would hide the whole
            // table instead of exercising the hidden-link path. An explicit
            // ordersPolicyActions (e.g. "create") denies table read outright instead.
            orders.GetMetadataValue(MetadataKeys.Policy.Actions)
                .Returns(ordersPolicyActions ?? (ordersReadDeny is null ? null : "read"));
            orders.GetMetadataValue(MetadataKeys.Policy.ReadDeny).Returns(ordersReadDeny);

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

        private static IDbModel OrdersAndUsersModel(string? ordersReadDeny = null, string? ordersPolicyActions = null)
            => BuildOrdersUsers(ordersReadDeny, ordersPolicyActions).model;

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
