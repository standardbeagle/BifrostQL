using System.Security.Claims;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// <c>_table</c> renders its filter through the shared <c>TableFilter</c> path (so
/// <c>_eq: null</c> is <c>IS NULL</c> and dialect casts apply) and pages in key order.
/// The key is TEXT so SQLite's storage (rowid, i.e. insertion) order differs from key
/// order: an unordered LIMIT/OFFSET returns rows in insertion order.
/// </summary>
public sealed class GenericTableFilterAndPagingTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_generic_table_filter_paging;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        foreach (var sql in new[]
        {
            "DROP TABLE IF EXISTS notes",
            "CREATE TABLE notes (code TEXT PRIMARY KEY, archived_at TEXT NULL)",
            "INSERT INTO notes (code, archived_at) VALUES ('c', NULL), ('a', NULL), ('b', '2024-01-01')",
        })
        {
            await using var cmd = new SqliteCommand(sql, _keepAlive);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private static async Task<GenericTableResult> QueryAsync(Dictionary<string, object?> arguments)
    {
        var factory = new SqliteDbConnFactory(ConnString);
        var model = await new DbModelLoader(factory, new MetadataLoader(new[] { ":root { generic-table: enabled }" })).LoadAsync();
        var resolver = new GenericTableQueryResolver(model, GenericTableConfig.FromModel(model));
        arguments["name"] = "notes";
        var context = new FieldContext
        {
            Arguments = arguments,
            UserContext = new Dictionary<string, object?>
            {
                ["user"] = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("role", "bifrost-admin") }, "test")),
            },
            InputExtensions = new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = model,
                ["tableReaderFactory"] = new SqlExecutionManager(model, DbSchema.FromModel(model), BifrostQL.Core.Modules.NullQueryTransformerService.Instance),
            },
        };
        return (GenericTableResult)(await resolver.ResolveAsync(context))!;
    }

    private static Dictionary<string, object?> Ops(string column, params (string Op, object? Value)[] ops) => new()
    {
        [column] = ops.ToDictionary(o => o.Op, o => o.Value),
    };

    private static IEnumerable<string> Codes(GenericTableResult result) => result.Rows.Select(r => (string)r["code"]!);

    [Fact]
    public async Task Filter_EqNull_MatchesNullRows()
    {
        var result = await QueryAsync(new() { ["filter"] = Ops("archived_at", ("_eq", null)) });

        Codes(result).Should().Equal("a", "c");
        result.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task Filter_NeqNull_MatchesNonNullRows()
    {
        var result = await QueryAsync(new() { ["filter"] = Ops("archived_at", ("_neq", null)) });

        Codes(result).Should().Equal("b");
    }

    [Fact]
    public async Task Filter_TwoOperatorsOnOneColumn_AreBothApplied()
    {
        var result = await QueryAsync(new() { ["filter"] = Ops("code", ("_gte", "a"), ("_lt", "c")) });

        Codes(result).Should().Equal("a", "b");
    }

    [Fact]
    public async Task Filter_UnknownOperator_Throws()
    {
        var act = () => QueryAsync(new() { ["filter"] = Ops("code", ("_unknown_op", "a")) });

        await act.Should().ThrowAsync<BifrostExecutionError>().WithMessage("*Unknown filter operator*");
    }

    [Fact]
    public async Task Paging_ConsecutivePages_FollowKeyOrder()
    {
        var first = await QueryAsync(new() { ["limit"] = 2, ["offset"] = 0 });
        var second = await QueryAsync(new() { ["limit"] = 2, ["offset"] = 2 });

        Codes(first).Should().Equal("a", "b");
        Codes(second).Should().Equal("c");
    }

    private sealed class FieldContext : IBifrostFieldContext
    {
        public string FieldName => "_table";
        public string? FieldAlias => null;
        public object? Source => null;
        public IReadOnlyList<object> Path => Array.Empty<object>();
        public IDictionary<string, object?> UserContext { get; init; } = new Dictionary<string, object?>();
        public IServiceProvider? RequestServices => null;
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
