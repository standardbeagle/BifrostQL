using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Read-side proof for <c>:root { policy-default: deny }</c> through the GraphQL
/// door. A table that declares no policy key of its own inherits the deny
/// default, so a non-admin read of it is refused with <c>ACCESS_DENIED</c> and
/// a message that never names the table, while the admin role reads its rows.
/// Until this fact existed the read-side default was proven only at the
/// <see cref="BifrostQL.Core.Auth.SchemaReadVisibility"/> unit seam.
/// </summary>
public sealed class PolicyDefaultDenyReadTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_policy_default_deny_read_test;Mode=Memory;Cache=Shared";

    private static readonly string[] Rules =
    {
        ":root { policy-default: deny }",
    };

    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS notes");
        await Exec("CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL)");
        await Exec("INSERT INTO notes (id, body) VALUES (1, 'first'), (2, 'second')");

        var factory = new SqliteDbConnFactory(ConnString);
        _model = await new DbModelLoader(factory, new MetadataLoader(Rules)).LoadAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<ExecutionResult> QueryAsync(string query, params string[] roles)
    {
        var schema = DbSchema.FromModel(_model);
        var transformerService = new QueryTransformerService(new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[] { new PolicyFilterTransformer() },
        });
        var services = new ServiceCollection();
        await using var provider = services.BuildServiceProvider();

        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = query;
            options.RequestServices = provider;
            options.UserContext = new Dictionary<string, object?>
            {
                ["user_id"] = "user-1",
                ["roles"] = roles,
            };
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, transformerService),
            });
        });
    }

    [Fact]
    public async Task NonAdmin_ReadsUndeclaredTableUnderDenyDefault_IsAccessDenied()
    {
        var result = await QueryAsync("{ notes { data { id body } } }", "member");

        result.Errors.Should().NotBeNullOrEmpty();
        var error = result.Errors![0];
        error.Message.Should().NotContain("notes", "a refusal must never name the table");
        error.InnerException.Should().BeOfType<BifrostExecutionError>()
            .Which.ErrorCode.Should().Be(BifrostExecutionError.AccessDeniedCode);
    }

    [Fact]
    public async Task Admin_ReadsUndeclaredTableUnderDenyDefault_GetsRows()
    {
        var result = await QueryAsync(
            "{ notes { data { id body } } }", MetadataKeys.Policy.DefaultAdminRole);

        result.Errors.Should().BeNullOrEmpty();
        using var doc = JsonDocument.Parse(new GraphQLSerializer().Serialize(result));
        var rows = doc.RootElement.GetProperty("data").GetProperty("notes").GetProperty("data");
        rows.GetArrayLength().Should().Be(2);
        rows[0].GetProperty("body").GetString().Should().Be("first");
    }
}
