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
            var sut = filter.ToSql(dbModel, "table");
            sut.Should().Be(("", "[table].[id] = '321'"));
        }

        [Theory]
        [InlineData("and", "id")]
        [InlineData("or", "id")]
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
            var sut = filter.ToSql(dbModel, "table");
            sut.Should().Be(("", $"(([table].[id] = '321') {joinType.ToUpper()} ([table].[{column2}] > '321'))"));
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
            Dictionary<string, TableDto> tables = GetTableModel();
            dbModel.GetTableFromTableName("tableName").Returns(tables["tableName1"]);
            var sut = filter.ToSql(dbModel, "table");
            sut.Should().Be((
                $" INNER JOIN (SELECT DISTINCT [id] AS [joinid], [id] AS [value] FROM [Sessions]) [j0] ON [j0].[joinid] = [table].[sessionId]",
                $"(([j0].[value] = '321') {joinType.ToUpper()} ([table].[{column2}] > '321'))"));
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
            Dictionary<string, TableDto> tables = GetTableModel();
            dbModel.GetTableFromTableName("tableName").Returns(tables["tableName1"]);
            var sut = filter.ToSql(dbModel, "table");
            sut.Should().Be((
                $" INNER JOIN (SELECT DISTINCT [id] AS [joinid], [id] AS [value] FROM [Sessions]) [j0] ON [j0].[joinid] = [table].[sessionId]",
                $"(([j0].[value] = '321') {joinType.ToUpper()} ([table].[{column2}] > '321'))"));
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
            Dictionary<string, TableDto> tables = GetTableModel();
            dbModel.GetTableFromTableName("tableName").Returns(tables["tableName1"]);
            var sut = filter.ToSql(dbModel, "table");
            sut.ToString().Should().Be(( 
                $" INNER JOIN (SELECT DISTINCT [id] AS [joinid], [id] AS [value] FROM [Sessions]) [j0] ON [j0].[joinid] = [table].[sessionId] INNER JOIN (SELECT DISTINCT [id] AS [joinid], [id] AS [value] FROM [Sessions]) [j1] ON [j1].[joinid] = [table].[sessionId]",
                $"(((([j0].[value] = '321') OR ([j1].[value] = '322'))) {joinType.ToUpper()} ([table].[{column2}] > '321'))").ToString());
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
            Dictionary<string, TableDto> tables = GetTableModel();
            dbModel.GetTableFromTableName("tableName").Returns(tables["tableName1"]);

            var sut = filter.ToSql(dbModel, alias);
            sut.Should().Be(($" INNER JOIN (SELECT DISTINCT [id] AS [joinid] FROM [Sessions] WHERE [Sessions].[id] = '321') [j] ON [j].[joinid] = [{result}].[sessionId]", ""));
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
            Dictionary<string, TableDto> tables = GetTableModel();
            dbModel.GetTableFromTableName("tableName").Returns(tables["tableName1"]);

            var sut = filter.ToSql(dbModel, "table");
            sut.Should().Be((" INNER JOIN (SELECT DISTINCT [id] AS [joinid] FROM [Sessions] INNER JOIN (SELECT DISTINCT [id] AS [joinid] FROM [workshops] WHERE [workshops].[id] = '321') [j] ON [j].[joinid] = [Sessions].[workshopId]) [j] ON [j].[joinid] = [table].[sessionId]", ""));
        }

        private static Dictionary<string, TableDto> GetTableModel()
        {
            var tables = new Dictionary<string, TableDto> {
                { "tableName1", new TableDto
                    {
                        DbName = "tableName1",
                        ColumnLookup = new Dictionary<string, ColumnDto> {
                            { "id", new ColumnDto {
                                ColumnName = "id",
                                GraphQlName = "id",
                                IsPrimaryKey = true,
                                DataType = "int",
                            } },
                            { "sessionId", new ColumnDto {
                                ColumnName = "sessionId",
                                GraphQlName = "sessionId",
                                IsPrimaryKey = false,
                                DataType = "int",
                            } } },
                    }
                },
                { "sessions", new TableDto
                    {
                        TableSchema = "dbo",
                        DbName = "Sessions",
                        GraphQlName = "sessions",
                        ColumnLookup = new Dictionary<string, ColumnDto> {
                        { "id", new ColumnDto {
                            ColumnName = "id",
                            GraphQlName = "id",
                            IsPrimaryKey = true,
                            DataType = "int",
                        } },
                        { "workshopId", new ColumnDto {
                            ColumnName = "workshopId",
                            GraphQlName = "workshopId",
                            IsPrimaryKey = true,
                            DataType = "int",
                        } } },
                        SingleLinks = new Dictionary<string, TableLinkDto>()
                    }
                },
                { "workshops", new TableDto
                    {
                        DbName = "workshops",
                        ColumnLookup = new Dictionary<string, ColumnDto> { { "id", new ColumnDto {
                            ColumnName = "id",
                            GraphQlName = "id",
                            IsPrimaryKey = true,
                            DataType = "int",
                        } } },
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

