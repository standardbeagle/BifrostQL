using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Verifies the protocol-adapter query-intent entry point
/// (<see cref="IQueryIntentExecutor"/>): a programmatic
/// <see cref="GqlObjectQuery"/> — no GraphQL text, no SqlVisitor — must pass
/// through the same security transformer pipeline as a GraphQL request. Tenant
/// isolation lands in the generated SQL, cross-tenant rows never surface, and
/// policy column read guards reject denied columns on the intent path too.
/// </summary>
public sealed class QueryIntentExecutorTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_query_intent_test;Mode=Memory;Cache=Shared";
    private const string EndpointPath = "/graphql";
    private SqliteConnection _keepAlive = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS orders");
        await Exec("DROP TABLE IF EXISTS documents");
        await Exec(
            """
            CREATE TABLE orders (
                id INTEGER PRIMARY KEY,
                tenant_id INTEGER NOT NULL,
                name TEXT NOT NULL
            )
            """);
        await Exec(
            """
            INSERT INTO orders(id, tenant_id, name) VALUES
                (1, 1, 'tenant-one-a'),
                (2, 1, 'tenant-one-b'),
                (3, 2, 'tenant-two-a')
            """);
        await Exec(
            """
            CREATE TABLE documents (
                id INTEGER PRIMARY KEY,
                title TEXT NOT NULL,
                body TEXT NOT NULL
            )
            """);
        await Exec("INSERT INTO documents(id, title, body) VALUES (1, 'public title', 'secret body')");
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private static readonly string[] Rules =
    {
        "*.orders { tenant-filter: tenant_id }",
        "*.documents { policy-read-deny: body }",
    };

    private static QueryIntentExecutor BuildExecutor()
    {
        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader(EndpointPath, async () =>
        {
            var factory = new SqliteDbConnFactory(ConnString);
            var loader = new DbModelLoader(factory, new MetadataLoader(Rules));
            var model = await loader.LoadAsync();
            return new Inputs(new Dictionary<string, object?>
            {
                ["model"] = model,
                ["dbSchema"] = DbSchema.FromModel(model),
                ["connFactory"] = factory,
            });
        });

        var transformerService = new QueryTransformerService(new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[]
            {
                new TenantFilterTransformer(),
                new PolicyFilterTransformer(),
            },
        });

        return new QueryIntentExecutor(pathCache, transformerService);
    }

    private static GqlObjectQuery BuildOrdersQuery(IDbModel model)
    {
        var table = model.GetTableFromDbName("orders");
        return new GqlObjectQuery
        {
            DbTable = table,
            SchemaName = table.TableSchema,
            TableName = table.DbName,
            GraphQlName = table.GraphQlName,
            Path = table.GraphQlName,
            ScalarColumns =
            {
                new GqlObjectColumn("id"),
                new GqlObjectColumn("tenant_id"),
                new GqlObjectColumn("name"),
            },
        };
    }

    private static IDictionary<string, object?> TenantContext(int tenantId) =>
        new Dictionary<string, object?> { ["tenant_id"] = tenantId };

    [Fact]
    public async Task Intent_OnTenantFilteredTable_EmitsTenantWhereClauseInSql()
    {
        var executor = BuildExecutor();
        var model = await executor.GetModelAsync(EndpointPath);

        var result = await executor.ExecuteAsync(new QueryIntent
        {
            Query = BuildOrdersQuery(model),
            UserContext = TenantContext(1),
            Endpoint = EndpointPath,
        });

        // The caller supplied no filter, yet the tenant transformer's WHERE must
        // be present in the generated SQL — parameterized, never inlined.
        result.Sql.Should().MatchRegex(@"WHERE[\s\S]*tenant_id");
        result.Sql.Should().NotContain("tenant_id\" = 1", "tenant values must bind as parameters, not literals");
    }

    [Fact]
    public async Task Intent_TenantAPrincipal_NeverSeesTenantBRows()
    {
        var executor = BuildExecutor();
        var model = await executor.GetModelAsync(EndpointPath);

        var result = await executor.ExecuteAsync(new QueryIntent
        {
            Query = BuildOrdersQuery(model),
            UserContext = TenantContext(1),
            Endpoint = EndpointPath,
        });

        result.Rows.Should().HaveCount(2);
        result.Rows.Should().OnlyContain(r => (long)r["tenant_id"]! == 1L);
        result.Rows.Select(r => (string)r["name"]!)
            .Should().BeEquivalentTo("tenant-one-a", "tenant-one-b");
    }

    [Fact]
    public async Task Intent_OtherTenant_SeesOnlyItsOwnRow()
    {
        var executor = BuildExecutor();
        var model = await executor.GetModelAsync(EndpointPath);

        var result = await executor.ExecuteAsync(new QueryIntent
        {
            Query = BuildOrdersQuery(model),
            UserContext = TenantContext(2),
            Endpoint = EndpointPath,
        });

        result.Rows.Should().ContainSingle(r => (string)r["name"]! == "tenant-two-a");
    }

    [Fact]
    public async Task Intent_MissingTenantContext_FailsClosed()
    {
        var executor = BuildExecutor();
        var model = await executor.GetModelAsync(EndpointPath);

        var act = () => executor.ExecuteAsync(new QueryIntent
        {
            Query = BuildOrdersQuery(model),
            UserContext = new Dictionary<string, object?>(),
            Endpoint = EndpointPath,
        });

        await act.Should().ThrowAsync<BifrostExecutionError>()
            .WithMessage("*Tenant context required*");
    }

    [Fact]
    public async Task Intent_SelectingPolicyDeniedColumn_IsRejectedByColumnReadGuard()
    {
        var executor = BuildExecutor();
        var model = await executor.GetModelAsync(EndpointPath);
        var documents = model.GetTableFromDbName("documents");

        var act = () => executor.ExecuteAsync(new QueryIntent
        {
            Query = new GqlObjectQuery
            {
                DbTable = documents,
                SchemaName = documents.TableSchema,
                TableName = documents.DbName,
                GraphQlName = documents.GraphQlName,
                Path = documents.GraphQlName,
                ScalarColumns = { new GqlObjectColumn("id"), new GqlObjectColumn("body") },
            },
            UserContext = new Dictionary<string, object?>(),
            Endpoint = EndpointPath,
        });

        await act.Should().ThrowAsync<BifrostExecutionError>()
            .WithMessage("*not permitted by authorization policy*");
    }

    [Fact]
    public async Task Intent_FilteringOnPolicyDeniedColumn_IsRejectedByColumnReadGuard()
    {
        var executor = BuildExecutor();
        var model = await executor.GetModelAsync(EndpointPath);
        var documents = model.GetTableFromDbName("documents");

        // Filtering (not selecting) a denied column would otherwise leak the
        // value through a boolean oracle; the read guard must reject it too.
        var act = () => executor.ExecuteAsync(new QueryIntent
        {
            Query = new GqlObjectQuery
            {
                DbTable = documents,
                SchemaName = documents.TableSchema,
                TableName = documents.DbName,
                GraphQlName = documents.GraphQlName,
                Path = documents.GraphQlName,
                ScalarColumns = { new GqlObjectColumn("id") },
                Filter = TableFilter.FromObject(
                    new Dictionary<string, object?>
                    {
                        ["body"] = new Dictionary<string, object?> { ["_eq"] = "secret body" },
                    },
                    documents.DbName),
            },
            UserContext = new Dictionary<string, object?>(),
            Endpoint = EndpointPath,
        });

        await act.Should().ThrowAsync<BifrostExecutionError>()
            .WithMessage("*not permitted by authorization policy*");
    }

    [Fact]
    public async Task Intent_UnknownEndpoint_FailsFast()
    {
        var executor = BuildExecutor();
        var model = await executor.GetModelAsync(EndpointPath);

        var act = () => executor.ExecuteAsync(new QueryIntent
        {
            Query = BuildOrdersQuery(model),
            UserContext = TenantContext(1),
            Endpoint = "/nope",
        });

        await act.Should().ThrowAsync<BifrostExecutionError>()
            .WithMessage("*Unknown BifrostQL endpoint*");
    }

    [Fact]
    public async Task Intent_NullEndpoint_ResolvesTheSingleRegisteredEndpoint()
    {
        var executor = BuildExecutor();
        var model = await executor.GetModelAsync();

        var result = await executor.ExecuteAsync(new QueryIntent
        {
            Query = BuildOrdersQuery(model),
            UserContext = TenantContext(1),
        });

        result.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Intent_WithIncludeResult_ReturnsUnpagedTotalScopedToTenant()
    {
        var executor = BuildExecutor();
        var model = await executor.GetModelAsync(EndpointPath);

        var query = BuildOrdersQuery(model);
        query.IncludeResult = true;
        query.Limit = 1;
        query.Sort.Add("id_asc");

        var result = await executor.ExecuteAsync(new QueryIntent
        {
            Query = query,
            UserContext = TenantContext(1),
            Endpoint = EndpointPath,
        });

        result.Rows.Should().HaveCount(1);
        // Total counts only tenant 1's rows — the transformer filter scopes the
        // count statement as well, not just the page.
        result.TotalCount.Should().Be(2);
    }

    // ---- grouped-aggregate intents ------------------------------------------

    private static GqlObjectQuery BuildOrdersCountAggregate(IDbModel model)
    {
        var table = model.GetTableFromDbName("orders");
        return new GqlObjectQuery
        {
            DbTable = table,
            SchemaName = table.TableSchema,
            TableName = table.DbName,
            GraphQlName = table.GraphQlName,
            Path = table.GraphQlName,
            GroupedAggregate = new GroupedAggregate
            {
                GroupColumns = Array.Empty<AggregateGroupColumn>(),
                IncludeCount = true,
                ValueColumns = Array.Empty<AggregateValueColumn>(),
            },
        };
    }

    [Fact]
    public async Task AggregateIntent_TenantScopeConstrainsTheGroupedRows_CountsDifferPerTenant()
    {
        var executor = BuildExecutor();
        var model = await executor.GetModelAsync(EndpointPath);

        var tenant1 = await executor.ExecuteAsync(new QueryIntent
        {
            Query = BuildOrdersCountAggregate(model),
            UserContext = TenantContext(1),
            Endpoint = EndpointPath,
        });
        var tenant2 = await executor.ExecuteAsync(new QueryIntent
        {
            Query = BuildOrdersCountAggregate(model),
            UserContext = TenantContext(2),
            Endpoint = EndpointPath,
        });

        // The tenant filter must land BEFORE grouping: identical intents yield
        // different aggregates per tenant, never the global count of 3.
        Convert.ToInt64(tenant1.Rows.Single()[GroupedAggregate.CountAlias]).Should().Be(2);
        Convert.ToInt64(tenant2.Rows.Single()[GroupedAggregate.CountAlias]).Should().Be(1);
        tenant1.Sql.Should().MatchRegex(@"WHERE[\s\S]*tenant_id");
        tenant1.Sql.Should().NotContain("tenant_id\" = 1", "tenant values must bind as parameters, not literals");
    }

    [Fact]
    public async Task AggregateIntent_MissingTenantContext_FailsClosed()
    {
        var executor = BuildExecutor();
        var model = await executor.GetModelAsync(EndpointPath);

        var act = () => executor.ExecuteAsync(new QueryIntent
        {
            Query = BuildOrdersCountAggregate(model),
            UserContext = new Dictionary<string, object?>(),
            Endpoint = EndpointPath,
        });

        await act.Should().ThrowAsync<BifrostExecutionError>()
            .WithMessage("*Tenant context required*");
    }

    [Fact]
    public async Task AggregateIntent_GroupByWithSum_ReturnsFlatGroupRowsScopedToTenant()
    {
        var executor = BuildExecutor();
        var model = await executor.GetModelAsync(EndpointPath);
        var table = model.GetTableFromDbName("orders");
        var nameColumn = table.ColumnLookup["name"];

        var query = new GqlObjectQuery
        {
            DbTable = table,
            SchemaName = table.TableSchema,
            TableName = table.DbName,
            GraphQlName = table.GraphQlName,
            Path = table.GraphQlName,
            GroupedAggregate = new GroupedAggregate
            {
                GroupColumns = new[] { new AggregateGroupColumn(nameColumn, nameColumn.GraphQlName) },
                IncludeCount = true,
                ValueColumns = new[]
                {
                    new AggregateValueColumn(AggregateOperationType.Max, table.ColumnLookup["id"], "_max", "_max_id"),
                },
            },
        };

        var result = await executor.ExecuteAsync(new QueryIntent
        {
            Query = query,
            UserContext = TenantContext(1),
            Endpoint = EndpointPath,
        });

        result.Rows.Should().HaveCount(2, "tenant 1 owns two distinctly named orders");
        result.Rows.Select(r => (string?)r[nameColumn.GraphQlName])
            .Should().BeEquivalentTo("tenant-one-a", "tenant-one-b");
        result.Rows.Should().OnlyContain(r => Convert.ToInt64(r[GroupedAggregate.CountAlias]) == 1L);
        result.Rows.Select(r => Convert.ToInt64(r["_max_id"])).Should().BeEquivalentTo(new[] { 1L, 2L });
    }

    [Fact]
    public async Task AggregateIntent_WithDeclaredLink_FailsFastInsteadOfSilentlyDroppingTheJoin()
    {
        var executor = BuildExecutor();
        var model = await executor.GetModelAsync(EndpointPath);

        var query = BuildOrdersCountAggregate(model);
        query.Links.Add(BuildOrdersQuery(model));

        var act = () => executor.ExecuteAsync(new QueryIntent
        {
            Query = query,
            UserContext = TenantContext(1),
            Endpoint = EndpointPath,
        });

        await act.Should().ThrowAsync<BifrostExecutionError>()
            .WithMessage("*do not support linked tables*");
    }
}
