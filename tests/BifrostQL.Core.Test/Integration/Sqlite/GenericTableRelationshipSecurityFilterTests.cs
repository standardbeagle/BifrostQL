using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
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
/// A relationship-shaped security filter (row scope through a related table)
/// contributes an INNER JOIN, not just a WHERE predicate. The generic-table
/// resolver previously wrapped the entire rendered filter in <c>WHERE (...)</c>,
/// producing <c>WHERE ( INNER JOIN ... )</c> — invalid SQL. The join must be
/// spliced before WHERE so the filter both compiles and actually scopes rows.
/// </summary>
public sealed class GenericTableRelationshipSecurityFilterTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_generic_table_rel_security_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("PRAGMA foreign_keys = ON");
        await Exec("DROP TABLE IF EXISTS orders");
        await Exec("DROP TABLE IF EXISTS customers");
        await Exec(
            """
            CREATE TABLE customers (
                id INTEGER PRIMARY KEY,
                region TEXT NOT NULL
            )
            """);
        await Exec(
            """
            CREATE TABLE orders (
                id INTEGER PRIMARY KEY,
                customer_id INTEGER NOT NULL,
                name TEXT NOT NULL,
                FOREIGN KEY (customer_id) REFERENCES customers(id)
            )
            """);
        await Exec("INSERT INTO customers(id, region) VALUES (1, 'US'), (2, 'EU')");
        await Exec(
            """
            INSERT INTO orders(id, customer_id, name) VALUES
                (1, 1, 'us-a'),
                (2, 1, 'us-b'),
                (3, 2, 'eu-a')
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
    };

    private async Task<IDbModel> LoadModelAsync()
    {
        var factory = new SqliteDbConnFactory(ConnString);
        var loader = new DbModelLoader(factory, new MetadataLoader(Rules));
        return await loader.LoadAsync();
    }

    private static ClaimsPrincipal AdminPrincipal() => new(new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.Name, "test-admin"),
        new Claim("role", "bifrost-admin"),
    }, "test"));

    [Fact]
    public async Task GenericTableQuery_RelationshipShapedSecurityFilter_ScopesRowsWithoutSqlError()
    {
        var model = await LoadModelAsync();
        var schema = DbSchema.FromModel(model);
        var factory = new SqliteDbConnFactory(ConnString);

        var services = new ServiceCollection();
        services.AddSingleton<IFilterTransformers>(new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[] { new CustomerRegionFilterTransformer() },
        });
        var provider = services.BuildServiceProvider();

        var config = GenericTableConfig.FromModel(model);
        var resolver = new GenericTableQueryResolver(model, config);

        var context = new FakeFieldContext
        {
            Arguments = new Dictionary<string, object?> { ["name"] = "orders" },
            UserContext = new Dictionary<string, object?> { ["user"] = AdminPrincipal() },
            RequestServices = provider,
            InputExtensions = new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = model,
                ["tableReaderFactory"] = new SqlExecutionManager(model, schema),
            },
        };

        // A relationship-shaped filter must not produce invalid SQL; it must also
        // actually scope the rows to customers in region 'US' (orders 1 and 2).
        var result = (GenericTableResult)(await resolver.ResolveAsync(context))!;

        result.TotalCount.Should().Be(2);
        result.Rows.Should().HaveCount(2);
        result.Rows.Should().OnlyContain(r => (long)r["customer_id"]! == 1L);
    }

    /// <summary>
    /// Emits a relationship (single-link) filter on <c>orders</c> scoping through
    /// the related <c>customers</c> table — the shape that injects an INNER JOIN.
    /// </summary>
    private sealed class CustomerRegionFilterTransformer : IFilterTransformer
    {
        public int Priority => 10;

        public bool AppliesTo(IDbTable table, QueryTransformContext context)
            => string.Equals(table.DbName, "orders", StringComparison.OrdinalIgnoreCase);

        public TableFilter GetAdditionalFilter(IDbTable table, QueryTransformContext context)
            => TableFilter.FromObject(new Dictionary<string, object?>
            {
                ["customers"] = new Dictionary<string, object?>
                {
                    ["region"] = new Dictionary<string, object?> { ["_eq"] = "US" },
                },
            }, table.DbName);
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
