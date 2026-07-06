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
