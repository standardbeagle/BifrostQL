using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// End-to-end coverage for the opt-in filtered set-update (<c>updateWhere: { set, where }</c>)
/// against a real SQLite database: matching rows update in one statement and the reply is the
/// affected COUNT; the transformers' row scope (soft-delete, policy) ANDs into the caller's
/// where; and every fail-closed gate rejects cleanly BEFORE any SQL — max-affected breaches
/// roll back, denied filter columns error rather than being stripped, and hook/token tables
/// are refused outright.
/// </summary>
public sealed class FilteredUpdateExecutionTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_filtered_update_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        await Exec("DROP TABLE IF EXISTS orders");
        await Exec("""
            CREATE TABLE orders (
                id INTEGER PRIMARY KEY,
                tenant_id INTEGER NOT NULL,
                status TEXT NOT NULL,
                total REAL NOT NULL,
                deleted_at TEXT NULL
            )
            """);
        // id 0 pins the PK-value-0 fixture case; id 3 belongs to another tenant.
        await Exec("""
            INSERT INTO orders(id, tenant_id, status, total, deleted_at) VALUES
                (0, 1, 'new', 5.0, NULL),
                (1, 1, 'new', 10.0, NULL),
                (2, 1, 'old', 20.0, NULL),
                (3, 2, 'new', 30.0, NULL),
                (4, 1, 'new', 40.0, '2026-01-01')
            """);
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string where)
    {
        await using var cmd = new SqliteCommand($"SELECT COUNT(*) FROM orders WHERE {where}", _keepAlive);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task<IDbModel> LoadModelAsync(Action<IDbTable>? configure = null)
    {
        var model = await new DbModelLoader(
            new SqliteDbConnFactory(ConnString), new MetadataLoader(Array.Empty<string>())).LoadAsync();
        var table = model.GetTableFromDbName("orders");
        table.Metadata[MetadataKeys.FilteredUpdate.Enabled] = FilteredUpdateConfig.EnabledValue;
        configure?.Invoke(table);
        return model;
    }

    private static async Task<ExecutionResult> ExecuteAsync(
        IDbModel model, string mutation,
        IMutationTransformer[]? transformers = null,
        IFilterTransformer[]? filterTransformers = null,
        IDictionary<string, object?>? userContext = null)
    {
        var schema = DbSchema.FromModel(model);
        var factory = new SqliteDbConnFactory(ConnString);
        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = transformers ?? Array.Empty<IMutationTransformer>(),
        });
        services.AddSingleton<IFilterTransformers>(new FilterTransformersWrap
        {
            Transformers = filterTransformers ?? Array.Empty<IFilterTransformer>(),
        });
        await using var provider = services.BuildServiceProvider();

        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = mutation;
            options.RequestServices = provider;
            options.UserContext = userContext is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(userContext);
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = model,
                ["tableReaderFactory"] = new SqlExecutionManager(model, schema, BifrostQL.Core.Modules.NullQueryTransformerService.Instance),
            });
        });
    }

    // ---- the happy set-update ----

    [Fact]
    public async Task UpdateWhere_UpdatesMatchingRows_ReturnsAffectedCount()
    {
        var model = await LoadModelAsync();
        var result = await ExecuteAsync(model,
            "mutation { orders(updateWhere: { set: { status: \"paid\" }, where: { status: { _eq: \"new\" }, tenant_id: { _eq: 1 } } }) }");

        result.Errors.Should().BeNullOrEmpty(
            $"errors: {string.Join("; ", result.Errors?.Select(e => e.Message) ?? Array.Empty<string>())}");
        // Rows 0 and 1 and 4 have status 'new' + tenant 1 — including the PK-value-0 row.
        (await CountAsync("status = 'paid'")).Should().Be(3);
        (await CountAsync("id = 0 AND status = 'paid'")).Should().Be(1, "PK value 0 is a legitimate matching row");
        (await CountAsync("id = 2 AND status = 'old'")).Should().Be(1, "non-matching rows are untouched");
        (await CountAsync("id = 3 AND status = 'new'")).Should().Be(1, "the other tenant's row only matched because no scope transformer ran here");
    }

    [Fact]
    public async Task UpdateWhere_SoftDeleteGuard_AndsIntoTheCallerFilter()
    {
        var model = await LoadModelAsync(t => t.Metadata[MetadataKeys.SoftDelete.Column] = "deleted_at");
        var result = await ExecuteAsync(model,
            "mutation { orders(updateWhere: { set: { status: \"paid\" }, where: { status: { _eq: \"new\" } } }) }",
            transformers: new IMutationTransformer[] { new SoftDeleteMutationTransformer() });

        result.Errors.Should().BeNullOrEmpty(
            $"errors: {string.Join("; ", result.Errors?.Select(e => e.Message) ?? Array.Empty<string>())}");
        (await CountAsync("id = 4 AND status = 'new'")).Should().Be(1,
            "the soft-deleted row is excluded by the transformer's IS NULL guard, ANDed — never replaced — into the caller's where");
        (await CountAsync("status = 'paid'")).Should().Be(3);
    }

    [Fact]
    public async Task UpdateWhere_PolicyRowScope_NarrowsToCallerTenant()
    {
        var model = await LoadModelAsync(t =>
        {
            t.Metadata[MetadataKeys.Policy.Actions] = "read,create,update,delete";
            t.Metadata[MetadataKeys.Policy.RowScope] = "tenant_id = {tenant_id}";
        });
        var result = await ExecuteAsync(model,
            "mutation { orders(updateWhere: { set: { status: \"mine\" }, where: { status: { _eq: \"new\" } } }) }",
            transformers: new IMutationTransformer[] { new PolicyMutationTransformer() },
            userContext: new Dictionary<string, object?> { ["user_id"] = "u1", ["roles"] = new[] { "user" }, ["tenant_id"] = 1 });

        result.Errors.Should().BeNullOrEmpty(
            $"errors: {string.Join("; ", result.Errors?.Select(e => e.Message) ?? Array.Empty<string>())}");
        (await CountAsync("id = 3 AND status = 'new'")).Should().Be(1,
            "the other tenant's matching row is silently narrowed out — 0 of it affected, no existence oracle");
        (await CountAsync("status = 'mine' AND tenant_id = 1")).Should().Be(3);
    }

    // ---- fail-closed gates ----

    [Fact]
    public async Task UpdateWhere_MaxAffectedBreach_ThrowsAndRollsBack()
    {
        var model = await LoadModelAsync(t => t.Metadata[MetadataKeys.FilteredUpdate.MaxAffected] = "1");
        var result = await ExecuteAsync(model,
            "mutation { orders(updateWhere: { set: { status: \"paid\" }, where: { status: { _eq: \"new\" } } }) }");

        result.Errors.Should().NotBeNullOrEmpty();
        result.Errors![0].Message.Should().Contain("filtered-update-max-affected");
        (await CountAsync("status = 'paid'")).Should().Be(0, "the breached update must roll back whole");
    }

    [Fact]
    public async Task UpdateWhere_EmptyWhere_IsRefused()
    {
        var model = await LoadModelAsync();
        var result = await ExecuteAsync(model,
            "mutation { orders(updateWhere: { set: { status: \"paid\" }, where: { } }) }");

        result.Errors.Should().NotBeNullOrEmpty();
        // The filter parser itself refuses an empty object ("Filter on orders has no
        // properties") before the pipeline's own non-empty-where gate — either way a
        // whole-table update is inexpressible by accident.
        result.Errors![0].Message.Should().MatchEquivalentOf("*no properties*");
        (await CountAsync("status = 'paid'")).Should().Be(0);
    }

    [Fact]
    public async Task UpdateWhere_NotOptedIn_ArgumentDoesNotExist()
    {
        // A non-opted-in table has no updateWhere argument at all: GraphQL validation
        // rejects the document before any resolver runs.
        var model = await new DbModelLoader(
            new SqliteDbConnFactory(ConnString), new MetadataLoader(Array.Empty<string>())).LoadAsync();
        var result = await ExecuteAsync(model,
            "mutation { orders(updateWhere: { set: { status: \"paid\" }, where: { id: { _eq: 1 } } }) }");

        result.Errors.Should().NotBeNullOrEmpty();
        (await CountAsync("status = 'paid'")).Should().Be(0);
    }

    [Fact]
    public async Task UpdateWhere_HooksRegistered_IsRefused()
    {
        var model = await LoadModelAsync();
        var factory = new SqliteDbConnFactory(ConnString);
        var services = new ServiceCollection()
            .AddSingleton(new BeforeCommitMutationHooks(new IBeforeCommitMutationHook[] { new NoopHook() }))
            .BuildServiceProvider();
        var ctx = PipelineContext(model, factory, services);

        var act = () => FilteredUpdatePipeline.UpdateByFilterAsync(
            model.GetTableFromDbName("orders"),
            new Dictionary<string, object?> { ["status"] = "paid" },
            new Dictionary<string, object?> { ["status"] = new Dictionary<string, object?> { ["_eq"] = "new" } },
            ctx);

        await act.Should().ThrowAsync<BifrostExecutionError>().WithMessage("*mutation hooks*");
        (await CountAsync("status = 'paid'")).Should().Be(0);
    }

    [Fact]
    public async Task UpdateWhere_ConcurrencyTokenTable_IsRefused()
    {
        var model = await LoadModelAsync(t => t.Metadata[MetadataKeys.Concurrency.Token] = "total");
        var ctx = PipelineContext(model, new SqliteDbConnFactory(ConnString), services: null);

        var act = () => FilteredUpdatePipeline.UpdateByFilterAsync(
            model.GetTableFromDbName("orders"),
            new Dictionary<string, object?> { ["status"] = "paid" },
            new Dictionary<string, object?> { ["status"] = new Dictionary<string, object?> { ["_eq"] = "new" } },
            ctx);

        await act.Should().ThrowAsync<BifrostExecutionError>().WithMessage("*concurrency-token*");
    }

    [Fact]
    public async Task UpdateWhere_NotEnabled_PipelineRefusesEvenWhenCalledDirectly()
    {
        // The SDL gate hides the argument, but a schema is not a security boundary:
        // the pipeline re-checks the opt-in fail-closed for any direct caller.
        var model = await new DbModelLoader(
            new SqliteDbConnFactory(ConnString), new MetadataLoader(Array.Empty<string>())).LoadAsync();
        var ctx = PipelineContext(model, new SqliteDbConnFactory(ConnString), services: null);

        var act = () => FilteredUpdatePipeline.UpdateByFilterAsync(
            model.GetTableFromDbName("orders"),
            new Dictionary<string, object?> { ["status"] = "paid" },
            new Dictionary<string, object?> { ["status"] = new Dictionary<string, object?> { ["_eq"] = "new" } },
            ctx);

        await act.Should().ThrowAsync<BifrostExecutionError>().WithMessage("*not enabled*");
    }

    [Fact]
    public async Task UpdateWhere_SetContainingPrimaryKey_IsRefused()
    {
        var model = await LoadModelAsync();
        var ctx = PipelineContext(model, new SqliteDbConnFactory(ConnString), services: null);

        var act = () => FilteredUpdatePipeline.UpdateByFilterAsync(
            model.GetTableFromDbName("orders"),
            new Dictionary<string, object?> { ["id"] = 99, ["status"] = "paid" },
            new Dictionary<string, object?> { ["status"] = new Dictionary<string, object?> { ["_eq"] = "new" } },
            ctx);

        await act.Should().ThrowAsync<BifrostExecutionError>().WithMessage("*primary-key*");
    }

    [Fact]
    public async Task UpdateWhere_DeniedFilterColumn_IsErrored_NeverStripped()
    {
        var model = await LoadModelAsync();
        var factory = new SqliteDbConnFactory(ConnString);
        var services = new ServiceCollection()
            .AddSingleton<IFilterTransformers>(new FilterTransformersWrap
            {
                Transformers = new IFilterTransformer[] { new DenyTotalColumnGuard() },
            })
            .BuildServiceProvider();
        var ctx = PipelineContext(model, factory, services);

        var act = () => FilteredUpdatePipeline.UpdateByFilterAsync(
            model.GetTableFromDbName("orders"),
            new Dictionary<string, object?> { ["status"] = "paid" },
            new Dictionary<string, object?> { ["total"] = new Dictionary<string, object?> { ["_gt"] = 15.0 } },
            ctx);

        // The affected count is a precision oracle: a denied filter column is an ERROR,
        // never silently removed from the predicate.
        await act.Should().ThrowAsync<BifrostExecutionError>();
        (await CountAsync("status = 'paid'")).Should().Be(0);
    }

    [Fact]
    public async Task UpdateWhere_RelationshipFilter_IsRefused()
    {
        // orders has no SingleLinks in this fixture, so simulate via direct rejection
        // of an unknown traversal name: FromObject treats it as a non-leaf node and the
        // renderer yields nothing — the empty-where refusal fires. The real relationship
        // rejection (non-empty Joins) is pinned at the unit level by the renderer's
        // contract; here we prove no unknown-name filter can slip through as a no-op.
        var model = await LoadModelAsync();
        var ctx = PipelineContext(model, new SqliteDbConnFactory(ConnString), services: null);

        var act = () => FilteredUpdatePipeline.UpdateByFilterAsync(
            model.GetTableFromDbName("orders"),
            new Dictionary<string, object?> { ["status"] = "paid" },
            new Dictionary<string, object?> { ["not_a_column"] = new Dictionary<string, object?> { ["_eq"] = 1 } },
            ctx);

        await act.Should().ThrowAsync<BifrostExecutionError>();
        (await CountAsync("status = 'paid'")).Should().Be(0);
    }

    // ---- fixtures ----

    private static MutationPipelineContext PipelineContext(
        IDbModel model, IDbConnFactory factory, IServiceProvider? services)
        => new()
        {
            Model = model,
            ConnFactory = factory,
            Transformers = new MutationTransformersWrap(),
            UserContext = new Dictionary<string, object?>(),
            Services = services,
        };

    private sealed class NoopHook : IBeforeCommitMutationHook
    {
        public ValueTask<IReadOnlyList<string>> BeforeCommitAsync(MutationObserverContext context)
            => ValueTask.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    /// <summary>A filter guard denying predicates on the 'total' column.</summary>
    private sealed class DenyTotalColumnGuard : IFilterTransformer, IColumnFilterGuard
    {
        public int Priority => 10;
        public bool AppliesTo(IDbTable table, QueryTransformContext context) => false;
        public BifrostQL.Core.QueryModel.TableFilter? GetAdditionalFilter(IDbTable table, QueryTransformContext context) => null;

        public void AssertColumnsFilterable(IDbTable table, IEnumerable<string> filteredColumns, QueryTransformContext context)
        {
            if (filteredColumns.Any(c => string.Equals(c, "total", StringComparison.OrdinalIgnoreCase)))
                throw new BifrostExecutionError("Access denied.");
        }
    }
}
