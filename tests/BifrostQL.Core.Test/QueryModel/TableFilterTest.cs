using BifrostQL.Model;
using FluentAssertions;
using NSubstitute;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using Xunit;

namespace BifrostQL.Core.QueryModel
{
    public sealed class TableFilterTest
    {
        private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

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
        [InlineData("and", "id")]
        [InlineData("or", "id")]
        [InlineData("and", "sessionId")]
        [InlineData("or", "sessionId")]
        public void DoubleAndOrNestedFilterSuccess(string joinType, string column2)
        {
            var filter = TableFilter.FromObject(new Dictionary<string, object?> {
                { joinType,
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

            // Complex nested joins produce parameterized SQL
            sut.Sql.Should().Contain("INNER JOIN");
            sut.Sql.Should().Contain("[table].[sessionId_db]");
            // The nested filter value and direct filter should both be parameterized
            sut.Parameters.Count.Should().BeGreaterThanOrEqualTo(1);
        }

        [Theory]
        [InlineData("and", "id")]
        [InlineData("or", "id")]
        [InlineData("and", "sessionId")]
        [InlineData("or", "sessionId")]
        public void DoubleAndOrNestedOrFilterSuccess(string joinType, string column2)
        {
            var filter = TableFilter.FromObject(new Dictionary<string, object?> {
                { joinType,
                    new List<object?> { new Dictionary<string, object?> {
                        { "or",  new List<object?> { new Dictionary<string, object?> {
                            { "sessions",new Dictionary<string, object?> {
                                { "id",  new Dictionary<string, object?> {{ "_eq", "321" }}
                                } } }
                            } }
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

            // Complex nested joins produce parameterized SQL
            sut.Sql.Should().Contain("INNER JOIN");
            sut.Sql.Should().Contain("[table].[sessionId_db]");
            sut.Parameters.Count.Should().BeGreaterThanOrEqualTo(1);
        }

        [Theory]
        [InlineData("and", "id")]
        [InlineData("or", "id")]
        [InlineData("and", "sessionId")]
        [InlineData("or", "sessionId")]
        public void DoubleAndOrNestedOrDoubleFilterSuccess(string joinType, string column2)
        {
            var filter = TableFilter.FromObject(new Dictionary<string, object?> {
                { joinType,
                    new List<object?> { new Dictionary<string, object?> {
                        { "or",  new List<object?> { new Dictionary<string, object?> {
                                { "sessions",new Dictionary<string, object?> {
                                { "id",  new Dictionary<string, object?> {{ "_eq", "321" }}
                                } } }
                            }, new Dictionary<string, object?> {
                                { "sessions",new Dictionary<string, object?> {
                                { "id",  new Dictionary<string, object?> {{ "_eq", "322" }}
                                } } }
                            }, }
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

            // Complex nested joins produce parameterized SQL
            sut.Sql.Should().Contain("INNER JOIN");
            sut.Sql.Should().Contain("[table].[sessionId_db]");
            // Should have parameters for both "321" and "322" values
            sut.Parameters.Count.Should().BeGreaterThanOrEqualTo(2);
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
