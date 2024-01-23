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

            var sut = filter.ToSql(dbModel, "table");
            sut.Should().Be(("", "[table].[id] = '321'"));
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
            var sut = filter.ToSql(dbModel, "table");
            sut.Should().Be(("", "[table].[id] = '321'"));
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
                GraphQlLookup = new Dictionary<string, ColumnDto>() { { "id", new ColumnDto() { ColumnName = "id" } }, { column2, new ColumnDto() { ColumnName = column2+"_ha" } } }
            });
            var sut = filter.ToSql(dbModel, "table");
            sut.Should().Be(("", $"(([table].[id] = '321') {joinType.ToUpper()} ([table].[{column2}_ha] > '321'))"));
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
            var sut = filter.ToSql(dbModel, "table");
            sut.Should().Be((
                $" INNER JOIN (SELECT DISTINCT [id] AS [joinid], [id] AS [value] FROM [Sessions]) [j0] ON [j0].[joinid] = [table].[sessionId_db]",
                $"(([j0].[value] = '321') {joinType.ToUpper()} ([table].[{column2}_db] > '321'))"));
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
            var sut = filter.ToSql(dbModel, "table");
            sut.Should().Be((
                $" INNER JOIN (SELECT DISTINCT [id] AS [joinid], [id] AS [value] FROM [Sessions]) [j0] ON [j0].[joinid] = [table].[sessionId_db]",
                $"(([j0].[value] = '321') {joinType.ToUpper()} ([table].[{column2}_db] > '321'))"));
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
            var sut = filter.ToSql(dbModel, "table");
            sut.ToString().Should().Be(( 
                $" INNER JOIN (SELECT DISTINCT [id] AS [joinid], [id] AS [value] FROM [Sessions]) [j0] ON [j0].[joinid] = [table].[sessionId_db] INNER JOIN (SELECT DISTINCT [id] AS [joinid], [id] AS [value] FROM [Sessions]) [j1] ON [j1].[joinid] = [table].[sessionId_db]",
                $"(((([j0].[value] = '321') OR ([j1].[value] = '322'))) {joinType.ToUpper()} ([table].[{column2}_db] > '321'))").ToString());
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

            var sut = filter.ToSql(dbModel, alias);
            sut.Should().Be(($" INNER JOIN (SELECT DISTINCT [id] AS [joinid] FROM [Sessions] WHERE [Sessions].[id] = '321') [j] ON [j].[joinid] = [{result}].[sessionId_db]", ""));
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

            var sut = filter.ToSql(dbModel, "table");
            sut.Should().Be((" INNER JOIN (SELECT DISTINCT [id] AS [joinid] FROM [Sessions] INNER JOIN (SELECT DISTINCT [id] AS [joinid] FROM [workshops] WHERE [workshops].[id] = '321') [j] ON [j].[joinid] = [Sessions].[workshopId]) [j] ON [j].[joinid] = [table].[sessionId_db]", ""));
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

