using BifrostQL.Model;
using FluentAssertions;
using NSubstitute;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel.TestFixtures;
using Xunit;

namespace BifrostQL.Core.QueryModel
{
    public sealed class TableFilterTest
    {
        private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

        [Fact]
        public void LeafFilter_WithDbColumnName_RendersEvenWhenGraphQlNameDiffers()
        {
            // Security transformers (tenant, soft-delete) build filters keyed by
            // the raw DB column name, which differs from the GraphQL field name for
            // sanitized/prefixed columns. The leaf render must resolve either name
            // space instead of throwing KeyNotFoundException.
            var model = DbModelTestFixture.Create()
                .WithTable("Orders", t => t
                    .WithColumn("id", "int", isPrimaryKey: true)
                    .WithColumn("tenant_id", "int", graphQlName: "tenantId"))
                .Build();

            var filter = TableFilterFactory.Equals("Orders", "tenant_id", 7);
            var parameters = new SqlParameterCollection();

            var sut = filter.ToSqlParameterized(model, Dialect, parameters, "Orders");

            sut.Sql.Should().Be("[Orders].[tenant_id] = @p0");
            sut.Parameters.Should().ContainSingle().Which.Value.Should().Be(7);
        }

        [Fact]
        public void RelationshipFilter_MapsGraphQlColumnNameToDbName_InSubquery()
        {
            // A relationship (single-link) sub-filter references a column on the
            // PARENT table by its GraphQL name. The relationship sub-query previously
            // emitted that GraphQL name verbatim (invalid identifier for a renamed
            // column) and looked its type up in the DB-name-keyed ColumnLookup
            // (missing it). Map GraphQL -> DB name, exactly like the leaf path.
            var model = DbModelTestFixture.Create()
                .WithTable("Orders", t => t
                    .WithColumn("id", "int", isPrimaryKey: true)
                    .WithColumn("customer_id", "int"))
                .WithTable("Customers", t => t
                    .WithColumn("id", "int", isPrimaryKey: true)
                    .WithColumn("email_address", "nvarchar", graphQlName: "emailAddress"))
                .WithSingleLink("Orders", "customer_id", "Customers", "id", "customer")
                .Build();

            var filter = TableFilter.FromObject(new Dictionary<string, object?>
            {
                { "customer", new Dictionary<string, object?>
                    {
                        { "emailAddress", new Dictionary<string, object?> { { "_eq", "a@b.c" } } }
                    }
                }
            }, "Orders");
            var parameters = new SqlParameterCollection();

            var sut = filter.ToSqlParameterized(model, Dialect, parameters, "Orders");

            // The DB column name is emitted inside the relationship sub-query, never
            // the GraphQL name.
            sut.Sql.Should().Contain("[email_address]");
            sut.Sql.Should().NotContain("[emailAddress]");
            sut.Sql.Should().Contain("INNER JOIN");
            sut.Parameters.Should().ContainSingle().Which.Value.Should().Be("a@b.c");
        }

        [Fact]
        public void FilterNoOperationThrows()
        {
            var run = () =>
            {
                var filter = TableFilter.FromObject(new Dictionary<string, object?> {
                    { "id", 321 }
                }, "tableName");
            };

            run.Should().Throw<ArgumentException>().WithMessage("Invalid filter object (Parameter 'value')");
        }

        [Fact]
        public void BasicFilterSuccess()
        {
            var filter = TableFilter.FromObject(new Dictionary<string, object?> {
                { "id", new Dictionary<string, object?> {
                    { "_eq", "321" }
                } }
            }, "tableName");
            var dbModel = Substitute.For<IDbModel>();
            dbModel.GetTableFromDbName("tableName").Returns(new DbTable()
            {
                GraphQlLookup = new Dictionary<string, ColumnDto>() { { "id", new ColumnDto() { ColumnName = "id" } } }
            });

            var parameters = new SqlParameterCollection();
            var sut = filter.ToSqlParameterized(dbModel, Dialect, parameters, "table");
            sut.Sql.Should().Be("[table].[id] = @p0");
            sut.Parameters.Should().HaveCount(1);
            sut.Parameters[0].Value.Should().Be("321");
        }

        [Theory]
        [InlineData("and")]
        [InlineData("or")]
        public void SingleAndOrFilterSuccess(string joinType)
        {
            var filter = TableFilter.FromObject(new Dictionary<string, object?> {
                { joinType,
                new List<object?> { new Dictionary<string, object?> {
                    { "id", new Dictionary<string, object?> {
                        { "_eq", "321" }
                    } } }
                } }
            }, "tableName");
            var dbModel = Substitute.For<IDbModel>();
            dbModel.GetTableFromDbName("tableName").Returns(new DbTable()
            {
                GraphQlLookup = new Dictionary<string, ColumnDto>() { { "id", new ColumnDto() { ColumnName = "id" } } }
            });

            var parameters = new SqlParameterCollection();
            var sut = filter.ToSqlParameterized(dbModel, Dialect, parameters, "table");
            sut.Sql.Should().Be("[table].[id] = @p0");
            sut.Parameters.Should().HaveCount(1);
            sut.Parameters[0].Value.Should().Be("321");
        }

        [Theory]
        [InlineData("and", "id2")]
        [InlineData("or", "id2")]
        [InlineData("and", "sessionId")]
        [InlineData("or", "sessionId")]
        public void DoubleAndOrFilterSuccess(string joinType, string column2)
        {
            var filter = TableFilter.FromObject(new Dictionary<string, object?> {
                { joinType,
                    new List<object?> { new Dictionary<string, object?> {
                        { "id", new Dictionary<string, object?> {
                            { "_eq", "321" }
                        } } },
                        new Dictionary<string, object?> {
                        { column2, new Dictionary<string, object?> {
                            { "_gt", "321" }
                        } } },
                    } }
            }, "tableName");
            var dbModel = Substitute.For<IDbModel>();
            dbModel.GetTableFromDbName("tableName").Returns(new DbTable()
            {
                GraphQlLookup = new Dictionary<string, ColumnDto>() { { "id", new ColumnDto() { ColumnName = "id" } }, { column2, new ColumnDto() { ColumnName = column2 + "_ha" } } }
            });

            var parameters = new SqlParameterCollection();
            var sut = filter.ToSqlParameterized(dbModel, Dialect, parameters, "table");
            sut.Sql.Should().Be($"(([table].[id] = @p0) {joinType.ToUpper()} ([table].[{column2}_ha] > @p1))");
            sut.Parameters.Should().HaveCount(2);
            sut.Parameters[0].Value.Should().Be("321");
            sut.Parameters[1].Value.Should().Be("321");
        }

        [Theory]
        [InlineData("id")]
        [InlineData("sessionId")]
        public void And_RelationshipPlusScalar_ProducesValidSql(string column2)
        {
            // A relationship filter ANDed with a scalar predicate: the relationship
            // contributes an INNER JOIN, the scalar a WHERE. This is the supported
            // combine and must render valid SQL.
            var filter = TableFilter.FromObject(new Dictionary<string, object?> {
                { "and",
                    new List<object?> { new Dictionary<string, object?> {
                        { "sessions",new Dictionary<string, object?> {
                            { "id", new Dictionary<string, object?> {
                                { "_eq", "321" }
                            } } }
                        } },
                        new Dictionary<string, object?> {
                        { column2, new Dictionary<string, object?> {
                            { "_gt", "321" }
                        } } },
                    } }
            }, "tableName");
            var dbModel = Substitute.For<IDbModel>();
            Dictionary<string, DbTable> tables = GetTableModel();
            dbModel.GetTableFromDbName("tableName").Returns(tables["tableName1"]);

            var parameters = new SqlParameterCollection();
            var sut = filter.ToSqlParameterized(dbModel, Dialect, parameters, "table");

            sut.Sql.Should().Contain("INNER JOIN");
            sut.Sql.Should().Contain("[table].[sessionId_db]");
            sut.Parameters.Count.Should().BeGreaterThanOrEqualTo(1);
        }

        [Fact]
        public void And_TwoRelationshipFilters_UseDistinctJoinAliases()
        {
            // Two relationship sub-filters at one AND level must get distinct join
            // aliases (j0, j1); a shared "[j]" was a duplicate-alias syntax error.
            var filter = TableFilter.FromObject(new Dictionary<string, object?> {
                { "and",
                    new List<object?> {
                        new Dictionary<string, object?> {
                            { "sessions", new Dictionary<string, object?> {
                                { "id", new Dictionary<string, object?> {{ "_eq", "321" }} } } } },
                        new Dictionary<string, object?> {
                            { "sessions", new Dictionary<string, object?> {
                                { "id", new Dictionary<string, object?> {{ "_eq", "322" }} } } } },
                    } }
            }, "tableName");
            var dbModel = Substitute.For<IDbModel>();
            Dictionary<string, DbTable> tables = GetTableModel();
            dbModel.GetTableFromDbName("tableName").Returns(tables["tableName1"]);

            var parameters = new SqlParameterCollection();
            var sut = filter.ToSqlParameterized(dbModel, Dialect, parameters, "table");

            sut.Sql.Should().Contain("[j0]");
            sut.Sql.Should().Contain("[j1]");
            // A "SELECT * FROM [t] {joins}" wrapper must parse — the duplicate-alias
            // bug produced two "[j]" and failed the grammar.
            BifrostQL.Core.Test.TestSupport.SqlSyntax.AssertValid($"SELECT * FROM [table]{sut.Sql}", "two relationship filters use distinct aliases");
        }

        [Fact]
        public void Or_RelationshipWithOtherBranch_ThrowsNotSupported()
        {
            // OR cannot be expressed by concatenating INNER JOINs; rather than
            // silently returning AND'd rows, this must fail loudly.
            var filter = TableFilter.FromObject(new Dictionary<string, object?> {
                { "or",
                    new List<object?> {
                        new Dictionary<string, object?> {
                            { "sessions", new Dictionary<string, object?> {
                                { "id", new Dictionary<string, object?> {{ "_eq", "321" }} } } } },
                        new Dictionary<string, object?> {
                            { "id", new Dictionary<string, object?> {{ "_gt", "321" }} } },
                    } }
            }, "tableName");
            var dbModel = Substitute.For<IDbModel>();
            Dictionary<string, DbTable> tables = GetTableModel();
            dbModel.GetTableFromDbName("tableName").Returns(tables["tableName1"]);

            var parameters = new SqlParameterCollection();
            var act = () => filter.ToSqlParameterized(dbModel, Dialect, parameters, "table");

            act.Should().Throw<BifrostQL.Core.Resolvers.BifrostExecutionError>()
                .WithMessage("*OR over relationship*");
        }

        [Theory]
        [InlineData("alias", "alias")]
        [InlineData("alias2", "alias2")]
        [InlineData(null, "tableName1")]
        public void NestedFilterSuccess(string? alias, string result)
        {
            var filter = TableFilter.FromObject(new Dictionary<string, object?> {
                { "sessions",new Dictionary<string, object?> {
                { "id", new Dictionary<string, object?> {
                    { "_eq", 321 }
                } } } }
            }, "tableName");
            var dbModel = Substitute.For<IDbModel>();
            Dictionary<string, DbTable> tables = GetTableModel();
            dbModel.GetTableFromDbName("tableName").Returns(tables["tableName1"]);

            var parameters = new SqlParameterCollection();
            var sut = filter.ToSqlParameterized(dbModel, Dialect, parameters, alias);

            // Complex nested joins produce parameterized SQL with JOIN
            sut.Sql.Should().Contain("INNER JOIN");
            sut.Sql.Should().Contain($"[{result}].[sessionId_db]");
            sut.Sql.Should().Contain("@p0");
            sut.Parameters.Should().HaveCount(1);
            sut.Parameters[0].Value.Should().Be(321);
        }

        [Fact]
        public void NestedNestedFilterSuccess()
        {
            var filter = TableFilter.FromObject(new Dictionary<string, object?> {
                { "sessions",new Dictionary<string, object?> {
                { "workshops",new Dictionary<string, object?> {
                { "id", new Dictionary<string, object?> {
                    { "_eq", 321 }
                } } } } } }
            }, "tableName");
            var dbModel = Substitute.For<IDbModel>();
            Dictionary<string, DbTable> tables = GetTableModel();
            dbModel.GetTableFromDbName("tableName").Returns(tables["tableName1"]);

            var parameters = new SqlParameterCollection();
            var sut = filter.ToSqlParameterized(dbModel, Dialect, parameters, "table");

            // Double nested produces parameterized SQL with nested JOINs
            sut.Sql.Should().Contain("INNER JOIN");
            sut.Sql.Should().Contain("[table].[sessionId_db]");
            sut.Sql.Should().Contain("@p0");
            sut.Parameters.Should().HaveCount(1);
            sut.Parameters[0].Value.Should().Be(321);
        }

        [Fact]
        public void SiblingKeys_FormImplicitAnd_ConstrainEveryColumn()
        {
            // `{ status: {_eq:...}, ownerId: {_eq:...} }` must constrain BOTH
            // columns. The former `filter.FirstOrDefault()` kept only the first key
            // and silently dropped every sibling, producing an over-broad WHERE
            // clause (a correctness/security hazard).
            var filter = TableFilter.FromObject(new Dictionary<string, object?>
            {
                { "status", new Dictionary<string, object?> { { "_eq", "open" } } },
                { "ownerId", new Dictionary<string, object?> { { "_eq", 7 } } },
            }, "tableName");

            var dbModel = Substitute.For<IDbModel>();
            dbModel.GetTableFromDbName("tableName").Returns(new DbTable
            {
                GraphQlLookup = new Dictionary<string, ColumnDto>
                {
                    { "status", new ColumnDto { ColumnName = "status" } },
                    { "ownerId", new ColumnDto { ColumnName = "owner_id" } },
                }
            });

            var parameters = new SqlParameterCollection();
            var sut = filter.ToSqlParameterized(dbModel, Dialect, parameters, "t");

            sut.Sql.Should().Be("(([t].[status] = @p0) AND ([t].[owner_id] = @p1))");
            sut.Parameters.Should().HaveCount(2);
            sut.Parameters[0].Value.Should().Be("open");
            sut.Parameters[1].Value.Should().Be(7);
        }

        [Fact]
        public void SiblingKeys_ThreeColumns_AllRendered()
        {
            var filter = TableFilter.FromObject(new Dictionary<string, object?>
            {
                { "a", new Dictionary<string, object?> { { "_eq", 1 } } },
                { "b", new Dictionary<string, object?> { { "_eq", 2 } } },
                { "c", new Dictionary<string, object?> { { "_eq", 3 } } },
            }, "tableName");

            var dbModel = Substitute.For<IDbModel>();
            dbModel.GetTableFromDbName("tableName").Returns(new DbTable
            {
                GraphQlLookup = new Dictionary<string, ColumnDto>
                {
                    { "a", new ColumnDto { ColumnName = "a" } },
                    { "b", new ColumnDto { ColumnName = "b" } },
                    { "c", new ColumnDto { ColumnName = "c" } },
                }
            });

            var parameters = new SqlParameterCollection();
            var sut = filter.ToSqlParameterized(dbModel, Dialect, parameters, "t");

            sut.Sql.Should().Contain("[t].[a] = @p0");
            sut.Sql.Should().Contain("[t].[b] = @p1");
            sut.Sql.Should().Contain("[t].[c] = @p2");
            sut.Parameters.Should().HaveCount(3);
        }

        [Fact]
        public void RelationshipFilter_WithUnsupportedNestedShape_ThrowsInsteadOfEmptyJoin()
        {
            // A relationship whose nested body is an OR reaches the
            // BuildSqlParameterized fall-through, which once returned ("", empty)
            // and was spliced into `INNER JOIN () ...` — a syntax error surfacing as
            // an opaque 500. It must now fail loudly with the offending shape.
            var filter = TableFilter.FromObject(new Dictionary<string, object?>
            {
                { "sessions", new Dictionary<string, object?> {
                    { "workshops", new Dictionary<string, object?> {
                        { "or", new List<object?> {
                            new Dictionary<string, object?> { { "id", new Dictionary<string, object?> { { "_eq", 1 } } } },
                            new Dictionary<string, object?> { { "id", new Dictionary<string, object?> { { "_eq", 2 } } } },
                        } } } } } }
            }, "tableName");

            var dbModel = Substitute.For<IDbModel>();
            var tables = GetTableModel();
            dbModel.GetTableFromDbName("tableName").Returns(tables["tableName1"]);

            var parameters = new SqlParameterCollection();
            var act = () => filter.ToSqlParameterized(dbModel, Dialect, parameters, "table");

            act.Should().Throw<BifrostQL.Core.Resolvers.BifrostExecutionError>()
                .WithMessage("*unsupported shape*");
        }

        [Fact]
        public void RelationshipFilter_MultiplePredicates_AndsThemInOneSubquery()
        {
            // Regression (b4974d5): sibling keys on a relationship form an implicit AND
            // whose wrapper has a null Next. The dispatch keyed on `Next.Next == null`
            // misrouted that wrapper into the leaf path, where the relationship name was
            // looked up as a column and threw "unknown column 'sessions'". Both nested
            // predicates must now land, ANDed, inside the single relationship subquery.
            var filter = TableFilter.FromObject(new Dictionary<string, object?>
            {
                { "sessions", new Dictionary<string, object?>
                    {
                        { "id", new Dictionary<string, object?> { { "_eq", 1 } } },
                        { "workshopId", new Dictionary<string, object?> { { "_eq", 2 } } },
                    }
                }
            }, "tableName");
            var dbModel = Substitute.For<IDbModel>();
            var tables = GetTableModel();
            dbModel.GetTableFromDbName("tableName").Returns(tables["tableName1"]);

            var parameters = new SqlParameterCollection();
            var sut = filter.ToSqlParameterized(dbModel, Dialect, parameters, "table");

            sut.Sql.Should().Contain("INNER JOIN");
            // Both predicates are present against the (parent) Sessions table, ANDed.
            sut.Sql.Should().Contain("[Sessions].[id] = @p0");
            sut.Sql.Should().Contain("[Sessions].[workshopId] = @p1");
            sut.Sql.Should().Contain(" AND ");
            sut.Parameters.Should().HaveCount(2);
            sut.Parameters[0].Value.Should().Be(1);
            sut.Parameters[1].Value.Should().Be(2);
            BifrostQL.Core.Test.TestSupport.SqlSyntax.AssertValid(
                $"SELECT * FROM [table]{sut.Sql}", "multi-predicate relationship filter renders valid SQL");
        }

        [Fact]
        public void RelationshipFilter_ExplicitAndBlock_AndsPredicatesInOneSubquery()
        {
            // The explicit `and` form must behave like the implicit-AND sibling form.
            var filter = TableFilter.FromObject(new Dictionary<string, object?>
            {
                { "sessions", new Dictionary<string, object?>
                    {
                        { "and", new List<object?>
                            {
                                new Dictionary<string, object?> { { "id", new Dictionary<string, object?> { { "_eq", 1 } } } },
                                new Dictionary<string, object?> { { "workshopId", new Dictionary<string, object?> { { "_eq", 2 } } } },
                            }
                        },
                    }
                }
            }, "tableName");
            var dbModel = Substitute.For<IDbModel>();
            var tables = GetTableModel();
            dbModel.GetTableFromDbName("tableName").Returns(tables["tableName1"]);

            var parameters = new SqlParameterCollection();
            var sut = filter.ToSqlParameterized(dbModel, Dialect, parameters, "table");

            sut.Sql.Should().Contain("[Sessions].[id] = @p0");
            sut.Sql.Should().Contain("[Sessions].[workshopId] = @p1");
            sut.Parameters.Should().HaveCount(2);
            BifrostQL.Core.Test.TestSupport.SqlSyntax.AssertValid(
                $"SELECT * FROM [table]{sut.Sql}", "explicit-and relationship filter renders valid SQL");
        }

        [Fact]
        public void RelationshipFilter_SinglePredicate_NotDegradedByMultiPredicateSupport()
        {
            // A single-predicate relationship must still render its subquery + join.
            var filter = TableFilter.FromObject(new Dictionary<string, object?>
            {
                { "sessions", new Dictionary<string, object?> {
                    { "id", new Dictionary<string, object?> { { "_eq", 42 } } } } }
            }, "tableName");
            var dbModel = Substitute.For<IDbModel>();
            var tables = GetTableModel();
            dbModel.GetTableFromDbName("tableName").Returns(tables["tableName1"]);

            var parameters = new SqlParameterCollection();
            var sut = filter.ToSqlParameterized(dbModel, Dialect, parameters, "table");

            sut.Sql.Should().Contain("INNER JOIN");
            sut.Sql.Should().Contain("[Sessions].[id] = @p0");
            sut.Sql.Should().NotContain(" AND ");
            sut.Parameters.Should().ContainSingle().Which.Value.Should().Be(42);
        }

        #region TableFilterBuilder (public fluent builder)

        // A model whose GetTableFromDbName("tableName1") resolves the builder's TableName
        // (TableFilterBuilder stamps _table.DbName, which is "tableName1" here).
        private static IDbModel BuilderModel()
        {
            var dbModel = Substitute.For<IDbModel>();
            var tables = GetTableModel();
            dbModel.GetTableFromDbName("tableName1").Returns(tables["tableName1"]);
            return dbModel;
        }

        private static IDbTable BuilderTable() => GetTableModel()["tableName1"];

        [Fact]
        public void Builder_Comparison_RendersParameterized()
        {
            var filter = TableFilterBuilder.For(BuilderTable())
                .Compare("id", "_gt", "AAA")
                .Build();
            var parameters = new SqlParameterCollection();

            var sut = filter.ToSqlParameterized(BuilderModel(), Dialect, parameters, "table");

            sut.Sql.Should().Be("[table].[id_db] > @p0");
            sut.Sql.Should().NotContain("AAA", "the value must be bound as a parameter, never a literal");
            sut.Parameters.Should().ContainSingle().Which.Value.Should().Be("AAA");
        }

        [Fact]
        public void Builder_In_RendersParameterizedList()
        {
            var filter = TableFilterBuilder.For(BuilderTable())
                .In("id", new object?[] { "AAA", "BBB" })
                .Build();
            var parameters = new SqlParameterCollection();

            var sut = filter.ToSqlParameterized(BuilderModel(), Dialect, parameters, "table");

            sut.Sql.Should().Contain("[table].[id_db] IN (");
            sut.Sql.Should().Contain("@p0");
            sut.Sql.Should().Contain("@p1");
            sut.Sql.Should().NotContain("AAA");
            sut.Sql.Should().NotContain("BBB");
            sut.Parameters.Should().HaveCount(2);
        }

        [Fact]
        public void Builder_IsNull_RendersIsNullWithoutParameter()
        {
            var filter = TableFilterBuilder.For(BuilderTable())
                .IsNull("id")
                .Build();
            var parameters = new SqlParameterCollection();

            var sut = filter.ToSqlParameterized(BuilderModel(), Dialect, parameters, "table");

            sut.Sql.Should().Be("[table].[id_db] IS NULL");
            sut.Parameters.Should().BeEmpty();
        }

        [Fact]
        public void Builder_Between_RendersRangeParameterized()
        {
            var filter = TableFilterBuilder.For(BuilderTable())
                .Between("sessionId", "AAA", "BBB")
                .Build();
            var parameters = new SqlParameterCollection();

            var sut = filter.ToSqlParameterized(BuilderModel(), Dialect, parameters, "table");

            sut.Sql.Should().Contain("[table].[sessionId_db] BETWEEN @p0 AND @p1");
            sut.Sql.Should().NotContain("AAA");
            sut.Sql.Should().NotContain("BBB");
            sut.Parameters.Should().HaveCount(2);
        }

        [Fact]
        public void Builder_Or_RendersParameterizedOr()
        {
            var filter = TableFilterBuilder.For(BuilderTable())
                .Or(
                    b => b.Equal("id", "AAA"),
                    b => b.Equal("sessionId", "BBB"))
                .Build();
            var parameters = new SqlParameterCollection();

            var sut = filter.ToSqlParameterized(BuilderModel(), Dialect, parameters, "table");

            sut.Sql.Should().Be("(([table].[id_db] = @p0) OR ([table].[sessionId_db] = @p1))");
            sut.Sql.Should().NotContain("AAA");
            sut.Sql.Should().NotContain("BBB");
            sut.Parameters.Should().HaveCount(2);
        }

        [Fact]
        public void Builder_Relationship_RendersParameterizedJoin()
        {
            var filter = TableFilterBuilder.For(BuilderTable())
                .Related("sessions", "id", "_eq", "AAA")
                .Build();
            var parameters = new SqlParameterCollection();

            var sut = filter.ToSqlParameterized(BuilderModel(), Dialect, parameters, "table");

            sut.Sql.Should().Contain("INNER JOIN");
            sut.Sql.Should().Contain("[Sessions].[id] = @p0");
            sut.Sql.Should().NotContain("AAA");
            sut.Parameters.Should().ContainSingle().Which.Value.Should().Be("AAA");
        }

        [Fact]
        public void Builder_MixedTree_RendersValidParameterizedSql()
        {
            var filter = TableFilterBuilder.For(BuilderTable())
                .Equal("id", "AAA")
                .Between("sessionId", "BBB", "CCC")
                .Related("sessions", "id", "_eq", "DDD")
                .Or(
                    b => b.Equal("id", "EEE"),
                    b => b.Equal("sessionId", "FFF"))
                .Build();
            var parameters = new SqlParameterCollection();

            var sut = filter.ToSqlParameterized(BuilderModel(), Dialect, parameters, "table");

            foreach (var literal in new[] { "AAA", "BBB", "CCC", "DDD", "EEE", "FFF" })
                sut.Sql.Should().NotContain(literal, "every value must be a bound parameter");
            sut.Sql.Should().Contain("@p0");
            sut.Parameters.Should().HaveCount(6); // eq(1) + between(2) + related(1) + or(2)
            BifrostQL.Core.Test.TestSupport.SqlSyntax.AssertValid(
                $"SELECT * FROM [table]{sut.Sql}", "builder mixed tree renders valid SQL");
        }

        [Fact]
        public void Builder_UnknownColumn_ThrowsAtBuildTime()
        {
            var act = () => TableFilterBuilder.For(BuilderTable()).Equal("does_not_exist", 1);

            act.Should().Throw<ArgumentException>()
                .WithMessage("*unknown column 'does_not_exist'*");
        }

        [Fact]
        public void Builder_UnknownOperator_ThrowsAtBuildTime()
        {
            var act = () => TableFilterBuilder.For(BuilderTable()).Compare("id", "_bogus", 1);

            act.Should().Throw<ArgumentException>()
                .WithMessage("*Unknown filter operator '_bogus'*");
        }

        [Fact]
        public void Builder_UnknownRelationship_ThrowsAtBuildTime()
        {
            var act = () => TableFilterBuilder.For(BuilderTable()).Related("nope", "id", "_eq", 1);

            act.Should().Throw<ArgumentException>()
                .WithMessage("*unknown single-link relationship 'nope'*");
        }

        [Fact]
        public void Builder_EmptyBuild_Throws()
        {
            var act = () => TableFilterBuilder.For(BuilderTable()).Build();

            act.Should().Throw<ArgumentException>()
                .WithMessage("*at least one predicate*");
        }

        #endregion

        private static Dictionary<string, DbTable> GetTableModel()
        {
            var table1Columns = new Dictionary<string, ColumnDto>
            {
                {
                    "id", new ColumnDto
                    {
                        ColumnName = "id_db",
                        GraphQlName = "id",
                        IsPrimaryKey = true,
                        DataType = "int",
                    }
                },
                {
                    "sessionId", new ColumnDto
                    {
                        ColumnName = "sessionId_db",
                        GraphQlName = "sessionId",
                        IsPrimaryKey = false,
                        DataType = "int",
                    }
                }
            };
            var sessionColumns = new Dictionary<string, ColumnDto>
            {
                {
                    "id", new ColumnDto
                    {
                        ColumnName = "id",
                        GraphQlName = "id",
                        IsPrimaryKey = true,
                        DataType = "int",
                    }
                },
                {
                    "workshopId", new ColumnDto
                    {
                        ColumnName = "workshopId",
                        GraphQlName = "workshopId",
                        IsPrimaryKey = true,
                        DataType = "int",
                    }
                }
            };
            var workshopColumns = new Dictionary<string, ColumnDto>
            {
                {
                    "id", new ColumnDto
                    {
                        ColumnName = "id",
                        GraphQlName = "id",
                        IsPrimaryKey = true,
                        DataType = "int",
                    }
                }
            };
            var tables = new Dictionary<string, DbTable> {
                { "tableName1", new DbTable
                    {
                        DbName = "tableName1",
                        ColumnLookup = table1Columns,
                        GraphQlLookup = table1Columns.Values.ToDictionary(x => x.GraphQlName, x => x),
                    }
                },
                { "sessions", new DbTable
                    {
                        TableSchema = "dbo",
                        DbName = "Sessions",
                        GraphQlName = "sessions",
                        ColumnLookup = sessionColumns,
                        GraphQlLookup = sessionColumns.Values.ToDictionary(x => x.GraphQlName, x => x),
                        SingleLinks = new Dictionary<string, TableLinkDto>()
                    }
                },
                { "workshops", new DbTable
                    {
                        DbName = "workshops",
                        ColumnLookup = workshopColumns,
                        GraphQlLookup = workshopColumns.Values.ToDictionary(x => x.GraphQlName, x => x),
                    }
                },
            };

            tables["tableName1"].SingleLinks.Add("sessions", new TableLinkDto
            {
                Name = "tableName->Sessions",
                ParentId = tables["sessions"].ColumnLookup["id"],
                ParentTable = tables["sessions"],
                ChildId = tables["tableName1"].ColumnLookup["sessionId"],
                ChildTable = tables["tableName1"],
            });

            tables["sessions"].SingleLinks.Add("workshops", new TableLinkDto
            {
                Name = "Sessions->Workshops",
                ChildId = tables["sessions"].ColumnLookup["workshopId"],
                ChildTable = tables["sessions"],
                ParentId = tables["workshops"].ColumnLookup["id"],
                ParentTable = tables["workshops"],
            });
            return tables;
        }
    }
}
