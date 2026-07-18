using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.SqlServer;
using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit;

namespace BifrostQL.Mcp.Test;

public sealed class DeclarativeQueryToolCompilerTests
{
    [Fact]
    public void Bind_ReusesCompiledIdentifiersAndBindsDifferentIdsWithoutChangingSqlShape()
    {
        var model = Model();
        var compiled = DeclarativeQueryToolCompiler.Compile(Definition(), model, new RecordingExecutor());

        var first = Render(compiled.Bind(Args("1")), model);
        var injection = Render(compiled.Bind(Args("'; DROP TABLE customers; --")), model);

        first.Sql.Should().Be(injection.Sql);
        first.Sql.Should().NotContain("DROP TABLE");
        first.Parameters.Should().ContainSingle().Which.Value.Should().Be("1");
        injection.Parameters.Should().ContainSingle().Which.Value.Should().Be("'; DROP TABLE customers; --");
        ParseSingleStatement(first.Sql);
    }

    [Fact]
    public async Task ExecuteAsync_UsesIntentExecutorAndCarriesUserContext()
    {
        var executor = new RecordingExecutor();
        var compiled = DeclarativeQueryToolCompiler.Compile(Definition(), Model(), executor, "/graphql");
        var context = new Dictionary<string, object?> { ["tenant_id"] = "tenant-a" };

        await compiled.ExecuteAsync(Args("7"), context);

        executor.Intent.Should().NotBeNull();
        executor.Intent!.UserContext.Should().BeSameAs(context);
        executor.Intent.Endpoint.Should().Be("/graphql");
        executor.Intent.Query.Filter.Should().NotBeNull();
    }

    [Fact]
    public void Compile_RejectsUnresolvedIdentifiersBeforeAnyRequestIsBound()
    {
        var definition = Definition() with { Root = Definition().Root with { Fields = ["name; DROP TABLE customers"] } };

        var act = () => DeclarativeQueryToolCompiler.Compile(definition, Model(), new RecordingExecutor());

        act.Should().Throw<InvalidOperationException>().WithMessage("*root.fields*unknown column*");
    }

    [Fact]
    public void Bind_CompilesCollectionAndAggregateIncludesFromModelRelationship()
    {
        var model = Model(withOrders: true);
        var definition = Definition() with
        {
            Include =
            [
                new DeclarativeToolInclude
                {
                    Relation = "orders", As = "openOrders", Fields = ["id", "total"],
                    Filter = JsonSerializer.SerializeToElement(new { status = new { _eq = "open" } }),
                    Sort = "-total", Limit = 5,
                },
                new DeclarativeToolInclude
                {
                    Relation = "orders", As = "orderTotals",
                    Aggregate = new DeclarativeToolAggregate { Count = true, Sum = "total" },
                },
            ],
        };

        var query = DeclarativeQueryToolCompiler.Compile(definition, model, new RecordingExecutor()).Bind(Args("1"));

        query.Joins.Should().ContainSingle();
        var join = query.Joins.Single();
        join.FromColumn.Should().Be("id");
        join.ConnectedColumn.Should().Be("customer_id");
        join.ConnectedTable.Sort.Should().Equal("total_desc");
        join.ConnectedTable.Limit.Should().Be(5);
        query.AggregateColumns.Should().HaveCount(2);

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, SqlServerDialect.Instance, sqls, parameters);
        parameters.Parameters.Should().Contain(parameter => Equals(parameter.Value, "open"));
        foreach (var statement in sqls.Values)
            ParseSingleStatement(statement.Sql);
    }

    [Fact]
    public void Bind_DefaultSummaryOmitsFullDetailIncludesAndFullRestoresThem()
    {
        var definition = Definition() with
        {
            Include =
            [
                new DeclarativeToolInclude
                {
                    Relation = "orders", As = "orders", Fields = ["id"], DetailGate = "full",
                },
            ],
        };
        var compiled = DeclarativeQueryToolCompiler.Compile(definition, Model(withOrders: true), new RecordingExecutor());

        compiled.Bind(Args("1")).Links.Should().BeEmpty();
        var full = new Dictionary<string, JsonElement>(Args("1"))
        {
            ["detail"] = JsonSerializer.SerializeToElement("full"),
        };
        compiled.Bind(full).Links.Should().ContainSingle();
    }

    [Fact]
    public void Surface_DerivesSchemasAndReadOnlyAnnotationFromDeclaration()
    {
        var definition = Definition() with
        {
            Description = "Return a customer with optional order details.",
            Params = new Dictionary<string, DeclarativeToolParameter>
            {
                ["customerId"] = new() { Type = "id", Table = "dbo.customers", Description = "Customer identifier." },
            },
            Include = [new DeclarativeToolInclude { Relation = "orders", As = "orders", Fields = ["id", "total"], DetailGate = "full" }],
        };

        var tool = DeclarativeToolSurface.BuildTool(definition, Model(withOrders: true));

        tool.Annotations!.ReadOnlyHint.Should().BeTrue();
        tool.InputSchema.GetProperty("properties").GetProperty("customerId").GetProperty("description").GetString()
            .Should().Be("Customer identifier.");
        tool.InputSchema.GetProperty("properties").GetProperty("detail").GetProperty("default").GetString()
            .Should().Be("summary");
        var output = tool.OutputSchema!.Value.GetProperty("properties").GetProperty("data")
            .GetProperty("anyOf")[0];
        output.GetProperty("properties").GetProperty("orders").GetProperty("items")
            .GetProperty("properties").GetProperty("total").GetProperty("type").GetString().Should().Be("number");
        output.GetProperty("required").EnumerateArray().Select(value => value.GetString())
            .Should().NotContain("orders", "a full-detail property is optional in the summary response");
    }

    [Fact]
    public async Task Surface_SerializedSummaryIsSmallerAndConformsToGeneratedRequiredShape()
    {
        var definition = Definition() with
        {
            Description = "Return a customer with optional order details.",
            Include = [new DeclarativeToolInclude { Relation = "orders", As = "orders", Fields = ["id", "total"], DetailGate = "full" }],
        };
        var model = Model(withOrders: true);
        var executor = new SurfaceExecutor();

        var summary = await DeclarativeToolSurface.ExecuteAsync(definition, model, executor, null, Args("1"), new Dictionary<string, object?>(), default);
        var fullArgs = new Dictionary<string, JsonElement>(Args("1"))
        {
            ["detail"] = JsonSerializer.SerializeToElement("full"),
        };
        var full = await DeclarativeToolSurface.ExecuteAsync(definition, model, executor, null, fullArgs, new Dictionary<string, object?>(), default);
        var schema = DeclarativeToolSurface.BuildTool(definition, model).OutputSchema!.Value;

        summary["found"]!.GetValue<bool>().Should().BeTrue();
        summary["data"]!.AsObject().ContainsKey("orders").Should().BeFalse();
        full["data"]!["orders"]!.AsArray().Should().ContainSingle();
        summary.ToJsonString().Length.Should().BeLessThan(full.ToJsonString().Length);
        foreach (var required in schema.GetProperty("required").EnumerateArray())
            summary.ContainsKey(required.GetString()!).Should().BeTrue();
    }

    [Fact]
    public async Task Surface_NotFoundConformsToStableEnvelope()
    {
        var definition = Definition() with { Description = "Return one customer when it exists." };
        var model = Model();
        var payload = await DeclarativeToolSurface.ExecuteAsync(
            definition, model, new RecordingExecutor(), null, Args("404"), new Dictionary<string, object?>(), default);
        var schema = DeclarativeToolSurface.BuildTool(definition, model).OutputSchema!.Value;

        payload["found"]!.GetValue<bool>().Should().BeFalse();
        payload["data"].Should().BeNull();
        schema.GetProperty("properties").GetProperty("data").GetProperty("anyOf")[1]
            .GetProperty("type").GetString().Should().Be("null");
    }

    [Fact]
    public void Surface_UsesDatabaseResultKeysWhenDeclarationUsesGraphQlNames()
    {
        var model = Model(distinctGraphQlName: true);
        var definition = Definition() with
        {
            Description = "Return one customer by GraphQL field name.",
            Root = Definition().Root with { Fields = ["id", "displayName"] },
        };

        var dataSchema = DeclarativeToolSurface.BuildTool(definition, model).OutputSchema!.Value
            .GetProperty("properties").GetProperty("data").GetProperty("anyOf")[0];

        dataSchema.GetProperty("properties").TryGetProperty("name", out _).Should().BeTrue();
        dataSchema.GetProperty("properties").TryGetProperty("displayName", out _).Should().BeFalse();
    }

    [Fact]
    public void Compile_RejectsCompositeRelationshipForVersionOneDocument()
    {
        var model = Model(withOrders: true, compositeLink: true);
        var definition = Definition() with
        {
            Include = [new DeclarativeToolInclude { Relation = "orders", As = "orders", Fields = ["id"] }],
        };

        var act = () => DeclarativeQueryToolCompiler.Compile(definition, model, new RecordingExecutor());

        act.Should().Throw<InvalidOperationException>().WithMessage("*orders*multi-column foreign key*version 1*");
    }

    [Fact]
    public void Bind_AggregateIncludeCombinesDeclaredAndTransformerFiltersInSql()
    {
        var model = Model(withOrders: true);
        var definition = Definition() with
        {
            Include =
            [
                new DeclarativeToolInclude
                {
                    Relation = "orders", As = "openOrderCount",
                    Filter = JsonSerializer.SerializeToElement(new { status = new { _eq = "open" } }),
                    Aggregate = new DeclarativeToolAggregate { Count = true },
                },
            ],
        };
        var query = DeclarativeQueryToolCompiler.Compile(definition, model, new RecordingExecutor()).Bind(Args("1"));
        var transformers = new FilterTransformersWrap
        {
            Transformers = [new FixedOrderScopeTransformer()],
        };

        new QueryTransformerService(transformers).ApplyTransformers(query, model, new Dictionary<string, object?>());

        query.AggregateColumns.Single().LinkFilters.Single()!.FilterType.Should().Be(FilterType.And,
            "a declared predicate must narrow rather than replace the security predicate");

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, SqlServerDialect.Instance, sqls, parameters);
        parameters.Parameters.Select(parameter => parameter.Value).Should().Contain(["open", "tenant-a"]);
        sqls.Values.Should().Contain(statement => statement.Sql.Contains("status") && statement.Sql.Contains("tenant_id"));
        foreach (var statement in sqls.Values)
            ParseSingleStatement(statement.Sql);
    }

    [Fact]
    public void Bind_AggregateIncludeFilterIsCheckedByColumnPolicyGuards()
    {
        var model = Model(withOrders: true);
        var definition = Definition() with
        {
            Include =
            [
                new DeclarativeToolInclude
                {
                    Relation = "orders", As = "openOrderCount",
                    Filter = JsonSerializer.SerializeToElement(new { status = new { _eq = "open" } }),
                    Aggregate = new DeclarativeToolAggregate { Count = true },
                },
            ],
        };
        var query = DeclarativeQueryToolCompiler.Compile(definition, model, new RecordingExecutor()).Bind(Args("1"));
        var service = new QueryTransformerService(new FilterTransformersWrap
        {
            Transformers = [new DenyStatusFilterGuard()],
        });

        var act = () => service.ApplyTransformers(query, model, new Dictionary<string, object?>());

        act.Should().Throw<InvalidOperationException>().WithMessage("aggregate filter column denied");
    }

    [Fact]
    public void Bind_DeclaredCollectionFieldsUseTheSharedColumnReadGuard()
    {
        var model = Model(withOrders: true);
        var definition = Definition() with
        {
            Include =
            [
                new DeclarativeToolInclude
                {
                    Relation = "orders", As = "orders", Fields = ["id", "status"],
                },
            ],
        };
        var query = DeclarativeQueryToolCompiler.Compile(definition, model, new RecordingExecutor()).Bind(Args("1"));
        var service = new QueryTransformerService(new FilterTransformersWrap
        {
            Transformers = [new DenyStatusReadGuard()],
        });

        var act = () => service.ApplyTransformers(query, model, new Dictionary<string, object?>());

        act.Should().Throw<InvalidOperationException>().WithMessage("declared include field denied");
    }

    [Fact]
    public void Bind_CompilesManyToManyCollectionAndAggregateThroughJunction()
    {
        var model = ManyToManyModel();
        var definition = Definition() with
        {
            Include =
            [
                new DeclarativeToolInclude
                {
                    Relation = "tags", As = "tags", Fields = ["id", "label"],
                    Filter = JsonSerializer.SerializeToElement(new { label = new { _eq = "priority" } }),
                },
                new DeclarativeToolInclude
                {
                    Relation = "tags", As = "tagCount",
                    Filter = JsonSerializer.SerializeToElement(new { label = new { _eq = "priority" } }),
                    Aggregate = new DeclarativeToolAggregate { Count = true },
                },
            ],
        };

        var query = DeclarativeQueryToolCompiler.Compile(definition, model, new RecordingExecutor()).Bind(Args("1"));

        query.Joins.Should().ContainSingle();
        query.Joins.Single().Bridge.Should().NotBeNull();
        query.AggregateColumns.Should().ContainSingle().Which.Links.Should().HaveCount(2);
        new QueryTransformerService(new FilterTransformersWrap()).ApplyTransformers(
            query, model, new Dictionary<string, object?>());
        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, SqlServerDialect.Instance, sqls, parameters);
        parameters.Parameters.Count(parameter => Equals(parameter.Value, "priority")).Should().Be(2);
        sqls.Values.Should().Contain(statement => statement.Sql.Contains("customer_tags"));
        foreach (var statement in sqls.Values)
            ParseSingleStatement(statement.Sql);
    }

    private static (string Sql, IReadOnlyList<SqlParameterInfo> Parameters) Render(GqlObjectQuery query, IDbModel model)
    {
        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, SqlServerDialect.Instance, sqls, parameters);
        return (sqls.Values.Single().Sql, parameters.Parameters);
    }

    private static void ParseSingleStatement(string sql)
    {
        var parser = new TSql160Parser(false);
        var fragment = parser.Parse(new StringReader(sql), out var errors);
        errors.Should().BeEmpty();
        var script = fragment.Should().BeOfType<TSqlScript>().Subject;
        script.Batches.Should().ContainSingle();
        script.Batches.Single().Statements.Should().ContainSingle();
    }

    private static IReadOnlyDictionary<string, JsonElement> Args(string id) =>
        new Dictionary<string, JsonElement> { ["customerId"] = JsonSerializer.SerializeToElement(id) };

    private static DeclarativeToolDefinition Definition() => new()
    {
        Name = "get_customer",
        Params = new Dictionary<string, DeclarativeToolParameter>
        {
            ["customerId"] = new() { Type = "id", Table = "dbo.customers" },
        },
        Root = new() { Table = "dbo.customers", ById = "customerId", Fields = ["id", "name"] },
    };

    private static IDbModel Model(bool withOrders = false, bool compositeLink = false, bool distinctGraphQlName = false)
    {
        var columns = new[]
        {
            Column("id", "nvarchar(50)", true, 1),
            Column("name", "nvarchar(50)", false, 2, distinctGraphQlName ? "displayName" : null),
        };
        var table = new DbTable
        {
            DbName = "customers", GraphQlName = "customers", NormalizedName = "customers",
            TableSchema = "dbo", TableType = "BASE TABLE",
            ColumnLookup = columns.ToDictionary(column => column.DbName, StringComparer.OrdinalIgnoreCase),
            GraphQlLookup = columns.ToDictionary(column => column.GraphQlName, StringComparer.OrdinalIgnoreCase),
        };
        if (!withOrders)
            return new DbModel { Tables = [table], Metadata = new Dictionary<string, object?>() };

        var orderColumns = new[]
        {
            OrderColumn("id", true, 1), OrderColumn("customer_id", false, 2),
            OrderColumn("status", false, 3), OrderColumn("total", false, 4),
            OrderColumn("tenant_id", false, 5),
        };
        var orders = new DbTable
        {
            DbName = "orders", GraphQlName = "orders", NormalizedName = "orders",
            TableSchema = "dbo", TableType = "BASE TABLE",
            ColumnLookup = orderColumns.ToDictionary(column => column.DbName, StringComparer.OrdinalIgnoreCase),
            GraphQlLookup = orderColumns.ToDictionary(column => column.GraphQlName, StringComparer.OrdinalIgnoreCase),
        };
        var link = new TableLinkDto
        {
            Name = "orders", ParentTable = table, ChildTable = orders,
            ParentId = columns[0], ChildId = orderColumns[1],
            ParentIds = compositeLink ? [columns[0], columns[1]] : [columns[0]],
            ChildIds = compositeLink ? [orderColumns[1], orderColumns[0]] : [orderColumns[1]],
        };
        table.MultiLinks["orders"] = link;
        return new DbModel { Tables = [table, orders], Metadata = new Dictionary<string, object?>() };
    }

    private static IDbModel ManyToManyModel()
    {
        var model = (DbModel)Model();
        var customers = model.GetTableFromDbName("customers");
        var tagColumns = new[] { GenericColumn("tags", "id", true, 1), GenericColumn("tags", "label", false, 2) };
        var tags = Table("tags", tagColumns);
        var junctionColumns = new[]
        {
            GenericColumn("customer_tags", "customer_id", false, 1),
            GenericColumn("customer_tags", "tag_id", false, 2),
        };
        var junction = Table("customer_tags", junctionColumns);
        customers.ManyToManyLinks["tags"] = new ManyToManyLink
        {
            Name = "tags", SourceTable = customers, JunctionTable = junction, TargetTable = tags,
            SourceColumn = customers.KeyColumns.Single(), JunctionSourceColumn = junctionColumns[0],
            JunctionTargetColumn = junctionColumns[1], TargetColumn = tagColumns[0],
        };
        return new DbModel
        {
            Tables = [customers, junction, tags],
            Metadata = new Dictionary<string, object?>(),
        };
    }

    private static DbTable Table(string name, ColumnDto[] columns) => new()
    {
        DbName = name, GraphQlName = name, NormalizedName = name,
        TableSchema = "dbo", TableType = "BASE TABLE",
        ColumnLookup = columns.ToDictionary(column => column.DbName, StringComparer.OrdinalIgnoreCase),
        GraphQlLookup = columns.ToDictionary(column => column.GraphQlName, StringComparer.OrdinalIgnoreCase),
    };

    private static ColumnDto GenericColumn(string table, string name, bool primaryKey, int ordinal) => new()
    {
        TableCatalog = "test", TableSchema = "dbo", TableName = table,
        ColumnName = name, GraphQlName = name, NormalizedName = name,
        ColumnRef = new ColumnRef("test", "dbo", table, name), DataType = "nvarchar(50)",
        IsPrimaryKey = primaryKey, OrdinalPosition = ordinal,
    };

    private static ColumnDto Column(string name, string type, bool primaryKey, int ordinal, string? graphQlName = null) => new()
    {
        TableCatalog = "test", TableSchema = "dbo", TableName = "customers",
        ColumnName = name, GraphQlName = graphQlName ?? name, NormalizedName = name,
        ColumnRef = new ColumnRef("test", "dbo", "customers", name), DataType = type,
        IsPrimaryKey = primaryKey, OrdinalPosition = ordinal,
    };

    private static ColumnDto OrderColumn(string name, bool primaryKey, int ordinal) => new()
    {
        TableCatalog = "test", TableSchema = "dbo", TableName = "orders",
        ColumnName = name, GraphQlName = name, NormalizedName = name,
        ColumnRef = new ColumnRef("test", "dbo", "orders", name),
        DataType = name == "total" ? "decimal" : "nvarchar(50)",
        IsPrimaryKey = primaryKey, OrdinalPosition = ordinal,
    };

    private sealed class RecordingExecutor : IQueryIntentExecutor
    {
        public QueryIntent? Intent { get; private set; }
        public Task<IDbModel> GetModelAsync(string? endpoint = null) => throw new NotSupportedException();
        public Task<QueryIntentResult> ExecuteAsync(QueryIntent intent, CancellationToken cancellationToken = default)
        {
            Intent = intent;
            return Task.FromResult(new QueryIntentResult { Rows = [], Sql = "SELECT 1" });
        }
    }

    private sealed class SurfaceExecutor : IQueryIntentExecutor
    {
        public Task<IDbModel> GetModelAsync(string? endpoint = null) => throw new NotSupportedException();

        public Task<QueryIntentResult> ExecuteAsync(QueryIntent intent, CancellationToken cancellationToken = default)
        {
            var row = new Dictionary<string, object?> { ["id"] = "1", ["name"] = "Ada" };
            if (intent.Query.Joins.Count > 0)
                row["orders"] = new[] { new Dictionary<string, object?> { ["id"] = "10", ["total"] = 42m } };
            return Task.FromResult(new QueryIntentResult { Rows = [row], Sql = "SELECT 1" });
        }
    }

    private sealed class FixedOrderScopeTransformer : IFilterTransformer
    {
        public int Priority => 0;
        public bool AppliesTo(IDbTable table, QueryTransformContext context) => table.DbName == "orders";
        public TableFilter? GetAdditionalFilter(IDbTable table, QueryTransformContext context) =>
            TableFilterFactory.Equals(table.DbName, "tenant_id", "tenant-a");
    }

    private sealed class DenyStatusFilterGuard : IFilterTransformer, IColumnFilterGuard
    {
        public int Priority => 0;
        public bool AppliesTo(IDbTable table, QueryTransformContext context) => false;
        public TableFilter? GetAdditionalFilter(IDbTable table, QueryTransformContext context) => null;

        public void AssertColumnsFilterable(
            IDbTable table,
            IEnumerable<string> filteredColumns,
            QueryTransformContext context)
        {
            if (table.DbName == "orders" && filteredColumns.Contains("status", StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("aggregate filter column denied");
        }
    }

    private sealed class DenyStatusReadGuard : IFilterTransformer, IColumnReadGuard
    {
        public int Priority => 0;
        public bool AppliesTo(IDbTable table, QueryTransformContext context) => false;
        public TableFilter? GetAdditionalFilter(IDbTable table, QueryTransformContext context) => null;

        public void AssertColumnsReadable(
            IDbTable table,
            IEnumerable<string> referencedColumns,
            QueryTransformContext context)
        {
            if (table.DbName == "orders" && referencedColumns.Contains("status", StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("declared include field denied");
        }
    }
}
