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
/// End-to-end proof that the batch mutation resolver captures module mutation
/// arguments (e.g. _hardDelete) declared on the batch field and threads them
/// into each delete's transform context — so a batch delete with _hardDelete:true
/// performs a real DELETE on a soft-delete table, while the default batch delete
/// is rewritten to a soft-delete UPDATE.
/// </summary>
public sealed class BatchHardDeleteTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_batch_harddelete_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS users");
        await Exec(
            """
            CREATE TABLE users (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                deleted_at TEXT
            )
            """);
        await Exec(
            """
            INSERT INTO users(id, name, deleted_at) VALUES
                (1, 'hard', NULL),
                (2, 'soft', NULL)
            """);

        var factory = new SqliteDbConnFactory(ConnString);
        var loader = new DbModelLoader(factory, new MetadataLoader(new[]
        {
            "*.users { soft-delete: deleted_at }",
        }));
        _model = await loader.LoadAsync();
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string where)
    {
        await using var cmd = new SqliteCommand($"SELECT COUNT(*) FROM users WHERE {where}", _keepAlive);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public void Schema_SoftDeleteTable_BatchField_ExposesHardDeleteArgument()
    {
        var users = _model.GetTableFromDbName("users");
        var sdl = new TableSchemaGenerator(users).GetInputFieldDefinition();

        sdl.Should().Contain("users_batch(actions: [batch_users!]!, _hardDelete: Boolean) : Int");
    }

    [Fact]
    public async Task BatchDelete_WithHardDeleteTrue_PerformsRealDelete()
    {
        var result = await ExecuteMutationAsync(
            "mutation { users_batch(actions: [{ delete: { id: 1 } }], _hardDelete: true) }");

        result.Errors.Should().BeNullOrEmpty();
        // _hardDelete:true bypasses the soft-delete rewrite — the row is gone.
        (await CountAsync("id = 1")).Should().Be(0, "hard delete physically removes the row");
    }

    [Fact]
    public async Task BatchDelete_Default_SoftDeletesViaUpdate()
    {
        var result = await ExecuteMutationAsync(
            "mutation { users_batch(actions: [{ delete: { id: 2 } }]) }");

        result.Errors.Should().BeNullOrEmpty();
        // Default delete is rewritten to UPDATE deleted_at — the row survives.
        (await CountAsync("id = 2")).Should().Be(1, "soft delete keeps the row");
        (await CountAsync("id = 2 AND deleted_at IS NOT NULL")).Should().Be(1,
            "soft delete stamps the deleted_at column");
    }

    private async Task<ExecutionResult> ExecuteMutationAsync(string mutation)
    {
        var schema = DbSchema.FromModel(_model);
        var factory = new SqliteDbConnFactory(ConnString);

        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[] { new SoftDeleteMutationTransformer() },
        });
        await using var provider = services.BuildServiceProvider();

        var executor = new DocumentExecuter();
        return await executor.ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = mutation;
            options.RequestServices = provider;
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = factory,
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema),
            });
        });
    }
}
