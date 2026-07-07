using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Verifies the fix for the CRITICAL finding: <c>_table</c> built its own SQL
/// directly against the database, gated only by the generic-table role, and
/// never ran the query through the filter-transformer pipeline. A tenant user
/// with the generic-table role could read every tenant's rows (and, on a
/// soft-delete table, deleted rows) via <c>_table</c> even though the same
/// user's normal table query would be tenant/soft-delete scoped.
///
/// The fix ANDs the table's combined tenant/soft-delete/policy filter (the same
/// one <see cref="QueryTransformerService"/> would apply) onto the generated
/// WHERE clause, resolved from <c>IFilterTransformers</c> via
/// <c>context.RequestServices</c>.
/// </summary>
public sealed class GenericTableQuerySecurityFilterTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_generic_table_security_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS orders");
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
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private static readonly string[] Rules =
    {
        ":root { generic-table: enabled }",
        "*.orders { tenant-filter: tenant_id }",
    };

    private async Task<IDbModel> LoadModelAsync()
    {
        var factory = new SqliteDbConnFactory(ConnString);
        var loader = new DbModelLoader(factory, new MetadataLoader(Rules));
        return await loader.LoadAsync();
    }

    private static IServiceProvider BuildServices(IEnumerable<IFilterTransformer> transformers)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFilterTransformers>(new FilterTransformersWrap
        {
            Transformers = transformers.ToArray(),
        });
        return services.BuildServiceProvider();
    }

    private static ClaimsPrincipal AdminPrincipal() => new(new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.Name, "test-admin"),
        new Claim("role", "bifrost-admin"),
    }, "test"));

    [Fact]
    public async Task GenericTableQuery_TenantUser_OnlySeesOwnTenantRows()
    {
        var model = await LoadModelAsync();
        var schema = DbSchema.FromModel(model);
        var factory = new SqliteDbConnFactory(ConnString);
        var services = BuildServices(new IFilterTransformer[] { new TenantFilterTransformer() });

        var config = GenericTableConfig.FromModel(model);
        var resolver = new GenericTableQueryResolver(model, config);

        var context = new FakeFieldContext
        {
            Arguments = new Dictionary<string, object?> { ["name"] = "orders" },
            UserContext = new Dictionary<string, object?>
            {
                ["user"] = AdminPrincipal(),
                ["tenant_id"] = 1,
            },
            RequestServices = services,
            InputExtensions = new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = model,
                ["tableReaderFactory"] = new SqlExecutionManager(model, schema),
            },
        };

        var result = (GenericTableResult)(await resolver.ResolveAsync(context))!;

        // Even though the caller never supplied a tenant filter (and has the
        // generic-table role), the tenant-1 caller must never see tenant 2's row.
        result.TotalCount.Should().Be(2);
        result.Rows.Should().HaveCount(2);
        result.Rows.Should().OnlyContain(r => (long)r["tenant_id"]! == 1L);
    }

    [Fact]
    public async Task GenericTableQuery_DifferentTenant_SeesOnlyItsOwnRow()
    {
        var model = await LoadModelAsync();
        var schema = DbSchema.FromModel(model);
        var factory = new SqliteDbConnFactory(ConnString);
        var services = BuildServices(new IFilterTransformer[] { new TenantFilterTransformer() });

        var config = GenericTableConfig.FromModel(model);
        var resolver = new GenericTableQueryResolver(model, config);

        var context = new FakeFieldContext
        {
            Arguments = new Dictionary<string, object?> { ["name"] = "orders" },
            UserContext = new Dictionary<string, object?>
            {
                ["user"] = AdminPrincipal(),
                ["tenant_id"] = 2,
            },
            RequestServices = services,
            InputExtensions = new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = model,
                ["tableReaderFactory"] = new SqlExecutionManager(model, schema),
            },
        };

        var result = (GenericTableResult)(await resolver.ResolveAsync(context))!;

        result.TotalCount.Should().Be(1);
        result.Rows.Should().ContainSingle(r => (string)r["name"]! == "tenant-two-a");
    }

    private sealed class FakeFieldContext : IBifrostFieldContext
    {
        public string FieldName => "_table";
        public string? FieldAlias => null;
        public object? Source => null;
        public IReadOnlyList<object> Path => Array.Empty<object>();
        public IDictionary<string, object?> UserContext { get; init; } = new Dictionary<string, object?>();
        public IServiceProvider? RequestServices { get; init; }
        public bool HasSubFields => false;
        public object Document => null!;
        public object Variables => null!;
        public IDictionary<string, object?> InputExtensions { get; init; } = new Dictionary<string, object?>();
        public CancellationToken CancellationToken => CancellationToken.None;
        public IDictionary<string, object?> Arguments { get; init; } = new Dictionary<string, object?>();

        public bool HasArgument(string name) => Arguments.ContainsKey(name);
        public T? GetArgument<T>(string name) => Arguments.TryGetValue(name, out var v) ? (T?)v : default;
    }
}
