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
/// Pins the parameter-name namespace split that keeps a transformer-injected
/// predicate (tenant scoping, soft-delete, policy) independent of client data.
///
/// Two producers bind parameters onto the same <see cref="System.Data.Common.DbCommand"/>:
/// <c>DbParameterBinder.AddParameters</c> names them after the COLUMN
/// (<c>@{SqlParameterNames.Sanitize(column)}</c>, client-controlled value), and
/// <c>DbParameterBinder.AddExtraParameters</c> binds the AdditionalFilter's
/// generated names from <see cref="SqlParameterCollection"/> (server-controlled
/// value). If the two namespaces can ever produce the same name, the rendered SQL
/// contains ONE placeholder serving both the SET assignment and the tenant
/// predicate — the client's value silently becomes the tenant predicate's value,
/// which is a cross-tenant write.
///
/// The fixture deliberately gives the table a column whose name is a legal
/// parameter identifier that the generator could also produce; every other
/// mutation fixture in this suite uses only names like <c>body</c>/<c>name</c>,
/// so none of them can manifest the collision.
/// </summary>
public sealed class ParameterNameCollisionTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_param_collision_test;Mode=Memory;Cache=Shared";
    private const string EndpointPath = "/graphql";
    private SqliteConnection _keepAlive = null!;

    private static readonly string[] Rules =
    {
        "*.notes { tenant-filter: tenant_id }",
        "*.records { tenant-filter: tenant_id }",
    };

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS records");
        // The PRIMARY KEY itself sits in the reserved shape, so the key predicate
        // and the tenant AdditionalFilter both want @p0 on the same command.
        await Exec(
            """
            CREATE TABLE records (
                p0 INTEGER PRIMARY KEY,
                tenant_id INTEGER NOT NULL,
                body TEXT NOT NULL
            )
            """);
        await Exec(
            """
            INSERT INTO records(p0, tenant_id, body) VALUES
                (1, 1, 'tenant-one-record'),
                (2, 2, 'tenant-two-record')
            """);

        await Exec("DROP TABLE IF EXISTS notes");
        // "p0" is a perfectly legal SQL column name and a legal parameter
        // identifier — SqlParameterNames.Sanitize returns it unchanged.
        await Exec(
            """
            CREATE TABLE notes (
                id INTEGER PRIMARY KEY,
                tenant_id INTEGER NOT NULL,
                p0 TEXT NULL,
                body TEXT NOT NULL
            )
            """);
        await Exec(
            """
            INSERT INTO notes(id, tenant_id, p0, body) VALUES
                (1, 1, 'tenant-one-p0', 'tenant-one-note'),
                (2, 2, 'tenant-two-p0', 'tenant-two-note')
            """);
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string?> ScalarAsync(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        return (await cmd.ExecuteScalarAsync())?.ToString();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private static MutationIntentExecutor BuildExecutor()
    {
        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader(EndpointPath, async () =>
        {
            var factory = new SqliteDbConnFactory(ConnString);
            var model = await new DbModelLoader(factory, new MetadataLoader(Rules)).LoadAsync();
            return new Inputs(new Dictionary<string, object?>
            {
                ["model"] = model,
                ["connFactory"] = factory,
            });
        });

        var transformers = new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[]
            {
                new PolicyMutationTransformer(),
                new SoftDeleteMutationTransformer(),
                new TenantMutationTransformer(),
                new AuditMutationTransformer(),
            },
        };

        return new MutationIntentExecutor(pathCache, transformers);
    }

    private static IDictionary<string, object?> TenantContext(int tenantId) =>
        new Dictionary<string, object?> { ["tenant_id"] = tenantId };

    /// <summary>
    /// Tenant 2 addresses tenant 1's row while writing the column named <c>p0</c>.
    /// The tenant predicate must keep the TENANT's value (2), so the write matches
    /// no row and tenant 1's data is untouched — regardless of what the client
    /// puts in the <c>p0</c> column.
    /// </summary>
    [Fact]
    public async Task Update_CrossTenant_ColumnNamedLikeAGeneratedParameter_StillNoOp()
    {
        var executor = BuildExecutor();

        await executor.ExecuteAsync(new MutationIntent
        {
            Table = "notes",
            Action = MutationIntentAction.Update,
            // The client's value for the p0 COLUMN is the tenant id it wants the
            // predicate to use. If the namespaces collide, this hijacks the scope.
            Data = new Dictionary<string, object?>
            {
                ["p0"] = 1,
                ["body"] = "hijacked",
            },
            PrimaryKey = new object?[] { 1 },
            UserContext = TenantContext(2),
            Endpoint = EndpointPath,
        });

        (await ScalarAsync("SELECT body FROM notes WHERE id = 1"))
            .Should().Be("tenant-one-note", "tenant 2 must not be able to write tenant 1's row");
        (await ScalarAsync("SELECT p0 FROM notes WHERE id = 1"))
            .Should().Be("tenant-one-p0", "tenant 2 must not be able to write tenant 1's row");
    }

    /// <summary>
    /// The same-tenant write must still succeed and must land the CLIENT's value in
    /// the p0 column — proving the fix separates the namespaces rather than letting
    /// the server-side parameter clobber client data.
    /// </summary>
    [Fact]
    public async Task Update_OwnTenant_ColumnNamedLikeAGeneratedParameter_WritesClientValue()
    {
        var executor = BuildExecutor();

        await executor.ExecuteAsync(new MutationIntent
        {
            Table = "notes",
            Action = MutationIntentAction.Update,
            Data = new Dictionary<string, object?>
            {
                ["p0"] = "client-wrote-this",
                ["body"] = "own-tenant-update",
            },
            PrimaryKey = new object?[] { 1 },
            UserContext = TenantContext(1),
            Endpoint = EndpointPath,
        });

        (await ScalarAsync("SELECT body FROM notes WHERE id = 1")).Should().Be("own-tenant-update");
        (await ScalarAsync("SELECT p0 FROM notes WHERE id = 1")).Should().Be("client-wrote-this",
            "the client's column value must not be clobbered by a generated parameter");
    }

    /// <summary>
    /// The delete path binds the KEY columns and the tenant AdditionalFilter onto
    /// one command; here the primary key itself is named <c>p0</c>, so both
    /// producers want the same parameter. The tenant predicate must still keep the
    /// tenant's value, leaving the other tenant's row in place.
    /// </summary>
    [Fact]
    public async Task Delete_CrossTenant_PrimaryKeyNamedLikeAGeneratedParameter_RowRemains()
    {
        var executor = BuildExecutor();

        var result = await executor.ExecuteAsync(new MutationIntent
        {
            Table = "records",
            Action = MutationIntentAction.Delete,
            Data = new Dictionary<string, object?>(),
            PrimaryKey = new object?[] { 1 },
            UserContext = TenantContext(2),
            Endpoint = EndpointPath,
        });

        result.Value.Should().Be(0, "the tenant scope matched no rows");
        (await ScalarAsync("SELECT COUNT(*) FROM records WHERE p0 = 1")).Should().Be("1");
    }

    [Fact]
    public async Task Delete_OwnTenant_PrimaryKeyNamedLikeAGeneratedParameter_Deletes()
    {
        var executor = BuildExecutor();

        var result = await executor.ExecuteAsync(new MutationIntent
        {
            Table = "records",
            Action = MutationIntentAction.Delete,
            Data = new Dictionary<string, object?>(),
            PrimaryKey = new object?[] { 1 },
            UserContext = TenantContext(1),
            Endpoint = EndpointPath,
        });

        result.Value.Should().Be(1);
        (await ScalarAsync("SELECT COUNT(*) FROM records WHERE p0 = 1")).Should().Be("0");
    }

    /// <summary>
    /// The structural half of the guarantee, independent of any one dialect: the
    /// generated namespace and the column-derived namespace cannot intersect.
    /// </summary>
    [Theory]
    [InlineData("p0")]
    [InlineData("P0")]
    [InlineData("p1")]
    [InlineData("p42")]
    public void Sanitize_PushesReservedShapeColumnNamesOutOfTheGeneratedNamespace(string columnName)
    {
        var sanitized = SqlParameterNames.Sanitize(columnName);

        SqlParameterNames.IsGeneratedShape(sanitized).Should().BeFalse(
            "a column-derived parameter name must never occupy the generated shape");
        for (var i = 0; i < 64; i++)
        {
            sanitized.Should().NotBe(SqlParameterNames.Generated(i));
            sanitized.Should().NotBeEquivalentTo(SqlParameterNames.Generated(i),
                "providers may compare parameter names case-insensitively");
        }
    }

    [Theory]
    [InlineData("body")]
    [InlineData("tenant_id")]
    [InlineData("point")]
    [InlineData("p")]
    [InlineData("p0x")]
    public void Sanitize_LeavesOrdinaryColumnNamesUntouched(string columnName)
    {
        SqlParameterNames.Sanitize(columnName).Should().Be(columnName);
    }
}
