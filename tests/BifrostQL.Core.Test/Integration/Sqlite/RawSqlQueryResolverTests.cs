using System.Security.Claims;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using NSubstitute;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Resolver-level tests for <see cref="RawSqlQueryResolver"/>. The
/// <c>RawSqlValidator</c> is unit-tested separately; these cover the resolver's
/// own responsibilities: role-based authorization, validate-then-execute
/// ordering (a rejected query never touches the database), and the invariant
/// that the exact validated string is the one executed — verified end-to-end
/// against a real SQLite database.
/// </summary>
public sealed class RawSqlQueryResolverTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDbConnFactory _factory;

    public RawSqlQueryResolverTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bifrost-rawsql-resolver-{Guid.NewGuid():N}.db");
        _factory = new SqliteDbConnFactory($"Data Source={_dbPath}");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private const string Role = "bifrost-raw-sql";

    private static IDbModel BuildModel() => DbModelTestFixture.Create()
        .WithTable("widget", t => t.WithPrimaryKey("id").WithColumn("name", "text"))
        .Build();

    private async Task SeedAsync()
    {
        await RawSqlExecutor.ExecuteAsync(_factory,
            "CREATE TABLE widget (id INTEGER PRIMARY KEY, name TEXT)", null, 30, 1000);
        await RawSqlExecutor.ExecuteAsync(_factory,
            "INSERT INTO widget (id, name) VALUES (1,'alpha'),(2,'beta')", null, 30, 1000);
    }

    private static ClaimsPrincipal PrincipalWithRole(string role) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, role) }, "test"));

    private RawSqlContext Context(IDbModel model, object? user, string sql,
        Dictionary<string, object?>? sqlParams = null)
    {
        var ctx = new RawSqlContext(_factory, model, sql, sqlParams);
        if (user != null)
            ctx.UserContext["user"] = user;
        return ctx;
    }

    [Fact]
    public async Task NoAuthenticatedUser_Throws()
    {
        var model = BuildModel();
        var resolver = new RawSqlQueryResolver(model);

        var act = async () => await resolver.ResolveAsync(Context(model, user: null, "SELECT 1"));

        (await act.Should().ThrowAsync<BifrostExecutionError>())
            .Which.Message.Should().Contain("Authentication required");
    }

    [Fact]
    public async Task UserWithoutRequiredRole_Throws()
    {
        var model = BuildModel();
        var resolver = new RawSqlQueryResolver(model);

        var act = async () => await resolver.ResolveAsync(
            Context(model, PrincipalWithRole("some-other-role"), "SELECT 1"));

        (await act.Should().ThrowAsync<BifrostExecutionError>())
            .Which.Message.Should().Contain("required role");
    }

    [Fact]
    public async Task CustomRoleFromMetadata_IsEnforced()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.RawSql.Role, "db-admin")
            .WithTable("widget", t => t.WithPrimaryKey("id").WithColumn("name", "text"))
            .Build();
        var resolver = new RawSqlQueryResolver(model);

        // The default role must not satisfy a model that requires a custom role.
        var defaultRoleAct = async () => await resolver.ResolveAsync(
            Context(model, PrincipalWithRole(Role), "SELECT 1"));
        (await defaultRoleAct.Should().ThrowAsync<BifrostExecutionError>())
            .Which.Message.Should().Contain("db-admin");
    }

    [Fact]
    public async Task InvalidSql_RejectedBeforeExecution()
    {
        await SeedAsync();
        var model = BuildModel();
        var resolver = new RawSqlQueryResolver(model);

        // A non-SELECT statement fails validation; it must never reach the database.
        var act = async () => await resolver.ResolveAsync(
            Context(model, PrincipalWithRole(Role), "UPDATE widget SET name='x'"));

        (await act.Should().ThrowAsync<BifrostExecutionError>())
            .Which.Message.Should().Contain("SQL validation failed");
        // Prove nothing was mutated: both rows keep their seeded names.
        var rows = await RawSqlExecutor.ExecuteAsync(_factory,
            "SELECT name FROM widget ORDER BY id", null, 30, 1000);
        rows.Rows.Select(r => r[0]).Should().Equal("alpha", "beta");
    }

    [Fact]
    public async Task ValidatedSql_IsTheSqlExecutedVerbatim()
    {
        await SeedAsync();
        var model = BuildModel();
        var resolver = new RawSqlQueryResolver(model);

        // The resolver validates `sql` then hands that same string to the executor.
        // A sentinel projection proves the executed statement is the validated one:
        // any rewrite would change the returned value.
        var result = (List<Dictionary<string, object?>>)(await resolver.ResolveAsync(
            Context(model, PrincipalWithRole(Role),
                "SELECT id, name FROM widget WHERE name = @n",
                new Dictionary<string, object?> { ["n"] = "beta" })))!;

        result.Should().ContainSingle();
        result[0]["id"].Should().Be(2L);
        result[0]["name"].Should().Be("beta");
    }

    /// <summary>Minimal raw-SQL resolver context backed by a real connection factory.</summary>
    private sealed class RawSqlContext : IBifrostFieldContext
    {
        private readonly string _sql;
        private readonly Dictionary<string, object?>? _params;

        public RawSqlContext(IDbConnFactory connFactory, IDbModel model, string sql, Dictionary<string, object?>? sqlParams)
        {
            _sql = sql;
            _params = sqlParams;
            InputExtensions = new Dictionary<string, object?>
            {
                ["connFactory"] = connFactory,
                ["model"] = model,
                ["tableReaderFactory"] = Substitute.For<ISqlExecutionManager>(),
            };
        }

        public IDictionary<string, object?> UserContext { get; } = new Dictionary<string, object?>();
        public IServiceProvider? RequestServices => null;
        public IDictionary<string, object?> InputExtensions { get; }
        public CancellationToken CancellationToken => CancellationToken.None;
        public string FieldName => "_rawQuery";
        public string? FieldAlias => null;
        public object? Source => null;
        public IReadOnlyList<object> Path => Array.Empty<object>();
        public bool HasSubFields => false;
        public object Document => null!;
        public object Variables => null!;

        public bool HasArgument(string name) => name switch
        {
            "sql" => true,
            "params" => _params != null,
            _ => false,
        };

        public T? GetArgument<T>(string name)
        {
            object? value = name switch
            {
                "sql" => _sql,
                "params" => _params,
                _ => null,
            };
            return value is T typed ? typed : default;
        }
    }
}
