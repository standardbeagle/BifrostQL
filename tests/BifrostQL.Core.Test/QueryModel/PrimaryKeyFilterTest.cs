using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Model;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Core.QueryModel
{
    public sealed class PrimaryKeyFilterTest
    {
        private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

        [Fact]
        public void FromPrimaryKey_SingleColumn_CreatesEqFilter()
        {
            var keyColumns = new[]
            {
                new ColumnDto { ColumnName = "Id", GraphQlName = "Id", IsPrimaryKey = true, DataType = "int" }
            };
            var values = new object?[] { "42" };

            var filter = TableFilter.FromPrimaryKey(values, keyColumns, "Users");

            var dbModel = Substitute.For<IDbModel>();
            dbModel.GetTableFromDbName("Users").Returns(new DbTable
            {
                GraphQlLookup = new Dictionary<string, ColumnDto>
                {
                    { "Id", new ColumnDto { ColumnName = "Id", GraphQlName = "Id" } }
                }
            });
            var parameters = new SqlParameterCollection();
            var sql = filter.ToSqlParameterized(dbModel, Dialect, parameters);

            sql.Sql.Should().Be("[Users].[Id] = @p0");
            parameters.Parameters.Should().HaveCount(1);
            parameters.Parameters[0].Value.Should().Be("42");
        }

        [Fact]
        public void FromPrimaryKey_CompositeKey_CreatesAndFilter()
        {
            var keyColumns = new[]
            {
                new ColumnDto { ColumnName = "TenantId", GraphQlName = "TenantId", IsPrimaryKey = true, DataType = "int" },
                new ColumnDto { ColumnName = "OrderId", GraphQlName = "OrderId", IsPrimaryKey = true, DataType = "int" }
            };
            var values = new object?[] { "100", "200" };

            var filter = TableFilter.FromPrimaryKey(values, keyColumns, "TenantOrders");

            var dbModel = Substitute.For<IDbModel>();
            dbModel.GetTableFromDbName("TenantOrders").Returns(new DbTable
            {
                GraphQlLookup = new Dictionary<string, ColumnDto>
                {
                    { "TenantId", new ColumnDto { ColumnName = "TenantId", GraphQlName = "TenantId" } },
                    { "OrderId", new ColumnDto { ColumnName = "OrderId", GraphQlName = "OrderId" } }
                }
            });
            var parameters = new SqlParameterCollection();
            var sql = filter.ToSqlParameterized(dbModel, Dialect, parameters);

            sql.Sql.Should().Contain("AND");
            sql.Sql.Should().Contain("[TenantOrders].[TenantId] = @p0");
            sql.Sql.Should().Contain("[TenantOrders].[OrderId] = @p1");
            parameters.Parameters.Should().HaveCount(2);
            parameters.Parameters[0].Value.Should().Be("100");
            parameters.Parameters[1].Value.Should().Be("200");
        }

        [Fact]
        public void FromPrimaryKey_NoKeyColumns_ThrowsExecutionError()
        {
            var keyColumns = Array.Empty<ColumnDto>();
            var values = new object?[] { "42" };

            var action = () => TableFilter.FromPrimaryKey(values, keyColumns, "NoKeyTable");

            action.Should().Throw<BifrostQL.Core.Resolvers.BifrostExecutionError>()
                .WithMessage("Table 'NoKeyTable' has no primary key columns.");
        }

        [Fact]
        public void FromPrimaryKey_TooFewValues_ThrowsExecutionError()
        {
            var keyColumns = new[]
            {
                new ColumnDto { ColumnName = "TenantId", GraphQlName = "TenantId", IsPrimaryKey = true, DataType = "int" },
                new ColumnDto { ColumnName = "OrderId", GraphQlName = "OrderId", IsPrimaryKey = true, DataType = "int" }
            };
            var values = new object?[] { "100" };

            var action = () => TableFilter.FromPrimaryKey(values, keyColumns, "TenantOrders");

            action.Should().Throw<BifrostQL.Core.Resolvers.BifrostExecutionError>()
                .WithMessage("*expects 2 value(s)*received 1*");
        }

        [Fact]
        public void FromPrimaryKey_TooManyValues_ThrowsExecutionError()
        {
            var keyColumns = new[]
            {
                new ColumnDto { ColumnName = "Id", GraphQlName = "Id", IsPrimaryKey = true, DataType = "int" }
            };
            var values = new object?[] { "1", "2" };

            var action = () => TableFilter.FromPrimaryKey(values, keyColumns, "Users");

            action.Should().Throw<BifrostQL.Core.Resolvers.BifrostExecutionError>()
                .WithMessage("*expects 1 value(s)*received 2*");
        }

        [Fact]
        public void FromPrimaryKey_ErrorMessageListsColumnNames()
        {
            var keyColumns = new[]
            {
                new ColumnDto { ColumnName = "Region", GraphQlName = "Region", IsPrimaryKey = true, DataType = "nvarchar" },
                new ColumnDto { ColumnName = "Code", GraphQlName = "Code", IsPrimaryKey = true, DataType = "nvarchar" }
            };
            var values = new object?[] { "US" };

            var action = () => TableFilter.FromPrimaryKey(values, keyColumns, "RegionCodes");

            action.Should().Throw<BifrostQL.Core.Resolvers.BifrostExecutionError>()
                .WithMessage("*Region, Code*");
        }

        [Fact]
        public void FromPrimaryKey_NullValue_CreatesIsNullFilter()
        {
            var keyColumns = new[]
            {
                new ColumnDto { ColumnName = "Id", GraphQlName = "Id", IsPrimaryKey = true, DataType = "int" }
            };
            var values = new object?[] { null };

            var filter = TableFilter.FromPrimaryKey(values, keyColumns, "Users");

            var dbModel = Substitute.For<IDbModel>();
            dbModel.GetTableFromDbName("Users").Returns(new DbTable
            {
                GraphQlLookup = new Dictionary<string, ColumnDto>
                {
                    { "Id", new ColumnDto { ColumnName = "Id", GraphQlName = "Id" } }
                }
            });
            var parameters = new SqlParameterCollection();
            var sql = filter.ToSqlParameterized(dbModel, Dialect, parameters);

            sql.Sql.Should().Be("[Users].[Id] IS NULL");
            parameters.Parameters.Should().HaveCount(0);
        }

        [Fact]
        public void BuildCombinedFilter_PrimaryKeyOnly_CreatesFilter()
        {
            var model = DbModelTestFixture.Create()
                .WithTable("Users", t => t
                    .WithPrimaryKey("Id")
                    .WithColumn("Name", "nvarchar"))
                .Build();

            var field = new QueryField
            {
                Name = "Users",
                Fields = new List<IQueryField>
                {
                    new QueryField { Name = "Id" },
                    new QueryField { Name = "Name" },
                },
                Arguments = new List<QueryArgument>
                {
                    new QueryArgument { Name = "_primaryKey", Value = new List<object?> { "42" } }
                }
            };

            var result = field.ToSqlData(model);

            result.Filter.Should().NotBeNull();
            var parameters = new SqlParameterCollection();
            var sql = result.Filter!.ToSqlParameterized(model, Dialect, parameters);
            sql.Sql.Should().Contain("[Users].[Id] = @p0");
            parameters.Parameters[0].Value.Should().Be("42");
        }

        [Fact]
        public void BuildCombinedFilter_FilterOnly_CreatesFilter()
        {
            var model = DbModelTestFixture.Create()
                .WithTable("Users", t => t
                    .WithPrimaryKey("Id")
                    .WithColumn("Name", "nvarchar"))
                .Build();

            var field = new QueryField
            {
                Name = "Users",
                Fields = new List<IQueryField>
                {
                    new QueryField { Name = "Id" },
                    new QueryField { Name = "Name" },
                },
                Arguments = new List<QueryArgument>
                {
                    new QueryArgument
                    {
                        Name = "filter",
                        Value = new Dictionary<string, object?>
                        {
                            { "Name", new Dictionary<string, object?> { { "_eq", "Alice" } } }
                        }
                    }
                }
            };

            var result = field.ToSqlData(model);

            result.Filter.Should().NotBeNull();
            var parameters = new SqlParameterCollection();
            var sql = result.Filter!.ToSqlParameterized(model, Dialect, parameters);
            sql.Sql.Should().Contain("[Users].[Name] = @p0");
            parameters.Parameters[0].Value.Should().Be("Alice");
        }

        [Fact]
        public void BuildCombinedFilter_BothFilterAndPrimaryKey_CombinesWithAnd()
        {
            var model = DbModelTestFixture.Create()
                .WithTable("Users", t => t
                    .WithPrimaryKey("Id")
                    .WithColumn("Name", "nvarchar"))
                .Build();

            var field = new QueryField
            {
                Name = "Users",
                Fields = new List<IQueryField>
                {
                    new QueryField { Name = "Id" },
                    new QueryField { Name = "Name" },
                },
                Arguments = new List<QueryArgument>
                {
                    new QueryArgument
                    {
                        Name = "filter",
                        Value = new Dictionary<string, object?>
                        {
                            { "Name", new Dictionary<string, object?> { { "_eq", "Alice" } } }
                        }
                    },
                    new QueryArgument
                    {
                        Name = "_primaryKey",
                        Value = new List<object?> { "42" }
                    }
                }
            };

            var result = field.ToSqlData(model);

            result.Filter.Should().NotBeNull();
            var parameters = new SqlParameterCollection();
            var sql = result.Filter!.ToSqlParameterized(model, Dialect, parameters);
            sql.Sql.Should().Contain("AND");
            sql.Sql.Should().Contain("[Users].[Name] = @p0");
            sql.Sql.Should().Contain("[Users].[Id] = @p1");
            parameters.Parameters.Should().HaveCount(2);
        }

        [Fact]
        public void BuildCombinedFilter_NeitherFilterNorPrimaryKey_ReturnsNull()
        {
            var model = DbModelTestFixture.Create()
                .WithTable("Users", t => t
                    .WithPrimaryKey("Id")
                    .WithColumn("Name", "nvarchar"))
                .Build();

            var field = new QueryField
            {
                Name = "Users",
                Fields = new List<IQueryField>
                {
                    new QueryField { Name = "Id" },
                    new QueryField { Name = "Name" },
                },
                Arguments = new List<QueryArgument>()
            };

            var result = field.ToSqlData(model);

            result.Filter.Should().BeNull();
        }

        [Fact]
        public void BuildCombinedFilter_CompositeKey_WorksCorrectly()
        {
            var model = DbModelTestFixture.Create()
                .WithTable("TenantOrders", t => t
                    .WithColumn("TenantId", "int", isPrimaryKey: true)
                    .WithColumn("OrderId", "int", isPrimaryKey: true)
                    .WithColumn("Total", "decimal"))
                .Build();

            var field = new QueryField
            {
                Name = "TenantOrders",
                Fields = new List<IQueryField>
                {
                    new QueryField { Name = "TenantId" },
                    new QueryField { Name = "OrderId" },
                    new QueryField { Name = "Total" },
                },
                Arguments = new List<QueryArgument>
                {
                    new QueryArgument { Name = "_primaryKey", Value = new List<object?> { "10", "20" } }
                }
            };

            var result = field.ToSqlData(model);

            result.Filter.Should().NotBeNull();
            var parameters = new SqlParameterCollection();
            var sql = result.Filter!.ToSqlParameterized(model, Dialect, parameters);
            sql.Sql.Should().Contain("AND");
            sql.Sql.Should().Contain("TenantId");
            sql.Sql.Should().Contain("OrderId");
            parameters.Parameters.Should().HaveCount(2);
        }

        [Fact]
        public void BuildCombinedFilter_PrimaryKeyNullValue_IgnoredWhenNull()
        {
            var model = DbModelTestFixture.Create()
                .WithTable("Users", t => t
                    .WithPrimaryKey("Id")
                    .WithColumn("Name", "nvarchar"))
                .Build();

            var field = new QueryField
            {
                Name = "Users",
                Fields = new List<IQueryField>
                {
                    new QueryField { Name = "Id" },
                    new QueryField { Name = "Name" },
                },
                Arguments = new List<QueryArgument>
                {
                    new QueryArgument { Name = "_primaryKey", Value = null }
                }
            };

            var result = field.ToSqlData(model);

            result.Filter.Should().BeNull();
        }

        [Fact]
        public void BuildCombinedFilter_PrimaryKeyMismatchCount_ThrowsError()
        {
            var model = DbModelTestFixture.Create()
                .WithTable("TenantOrders", t => t
                    .WithColumn("TenantId", "int", isPrimaryKey: true)
                    .WithColumn("OrderId", "int", isPrimaryKey: true)
                    .WithColumn("Total", "decimal"))
                .Build();

            var field = new QueryField
            {
                Name = "TenantOrders",
                Fields = new List<IQueryField>
                {
                    new QueryField { Name = "TenantId" },
                    new QueryField { Name = "OrderId" },
                    new QueryField { Name = "Total" },
                },
                Arguments = new List<QueryArgument>
                {
                    new QueryArgument { Name = "_primaryKey", Value = new List<object?> { "10" } }
                }
            };

            var action = () => field.ToSqlData(model);

            action.Should().Throw<BifrostQL.Core.Resolvers.BifrostExecutionError>()
                .WithMessage("*expects 2 value(s)*received 1*");
        }
    }
}
