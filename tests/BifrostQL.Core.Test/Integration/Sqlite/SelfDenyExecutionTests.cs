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
/// S6b <c>policy-self-deny</c> against a real SQLite database: the caller's own
/// row is excluded from a keyed update that writes a listed column, for admins
/// too, and the zero-row result is the silent no-op every out-of-scope policy
/// write surfaces today (<see cref="MutationCommandExecutor.EnsureAffectedRows"/>).
/// </summary>
public sealed class SelfDenyExecutionTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_self_deny_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        await Exec("DROP TABLE IF EXISTS users");
        await Exec("CREATE TABLE users (id INTEGER PRIMARY KEY, permission_profile_id INTEGER NULL, display_name TEXT NULL)");
        await Exec("INSERT INTO users(id, permission_profile_id, display_name) VALUES (1, 1, 'admin'), (2, 1, 'colleague')");
        _model = await new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(new[]
        {
            "main.users { policy-actions: read,create,update,delete; policy-self-deny: permission_profile_id; policy-self-column: id }",
        })).LoadAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

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

    [Fact]
    public async Task AdminUpdatingOwnPermissionProfile_AffectsNoRow_WithoutError()
    {
        var result = await ExecuteAsync("mutation { users(update: { id: 1, permission_profile_id: 9 }) }", "1", "admin");

        result.Errors.Should().BeNullOrEmpty();
        (await ScalarAsync("SELECT permission_profile_id FROM users WHERE id = 1")).Should().Be("1",
            "the self predicate excludes the caller's own row; zero rows is the silent policy no-op");
    }

    [Fact]
    public async Task AdminUpdatingColleaguePermissionProfile_Persists()
    {
        var result = await ExecuteAsync("mutation { users(update: { id: 2, permission_profile_id: 9 }) }", "1", "admin");

        result.Errors.Should().BeNullOrEmpty();
        (await ScalarAsync("SELECT permission_profile_id FROM users WHERE id = 2")).Should().Be("9");
    }

    [Fact]
    public async Task MemberUpdatingOwnUnlistedColumn_Persists()
    {
        var result = await ExecuteAsync("mutation { users(update: { id: 2, display_name: \"renamed\" }) }", "2", "member");

        result.Errors.Should().BeNullOrEmpty();
        (await ScalarAsync("SELECT display_name FROM users WHERE id = 2")).Should().Be("renamed");
    }

    private async Task<ExecutionResult> ExecuteAsync(string mutation, string userId, string role)
    {
        var schema = DbSchema.FromModel(_model);
        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[] { new PolicyMutationTransformer() },
        });
        await using var provider = services.BuildServiceProvider();

        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = mutation;
            options.RequestServices = provider;
            options.UserContext = new Dictionary<string, object?> { ["user_id"] = userId, ["roles"] = new[] { role } };
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, NullQueryTransformerService.Instance),
            });
        });
    }
}
