using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using GraphQL.SystemTextJson;
using Microsoft.Data.Sqlite;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// A many-to-many field renamed by <c>LinkFieldNames.Unique</c> (here because the source
/// table already has a column named after the target) must be readable under its renamed
/// field name. The join used to be named after the target table, so the reader looked for
/// the renamed field, missed, and the query failed with "Unable to find queryField".
/// </summary>
public sealed class ManyToManyRenamedFieldTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_m2m_renamed_field_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS user_roles");
        await Exec("DROP TABLE IF EXISTS users");
        await Exec("DROP TABLE IF EXISTS roles");
        await Exec("CREATE TABLE roles (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        // users.roles is a denormalised text column, so the m2m field cannot be "roles".
        await Exec("CREATE TABLE users (id INTEGER PRIMARY KEY, roles TEXT NOT NULL)");
        await Exec(
            """
            CREATE TABLE user_roles (
                user_id INTEGER NOT NULL,
                role_id INTEGER NOT NULL,
                PRIMARY KEY (user_id, role_id),
                FOREIGN KEY (user_id) REFERENCES users(id),
                FOREIGN KEY (role_id) REFERENCES roles(id)
            )
            """);
        await Exec("INSERT INTO roles(id, name) VALUES (1, 'admin'), (2, 'editor'), (3, 'viewer')");
        await Exec("INSERT INTO users(id, roles) VALUES (1, 'admin,editor'), (2, 'viewer')");
        await Exec("INSERT INTO user_roles(user_id, role_id) VALUES (1, 1), (1, 2), (2, 3)");

        var loader = new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(Array.Empty<string>()));
        _model = await loader.LoadAsync();
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public async Task RenamedManyToManyField_ReturnsEachParentsTargets()
    {
        var fieldName = _model.GetTableFromDbName("users").ManyToManyLinks.Should().ContainSingle().Subject.Key;
        fieldName.Should().NotBe("roles", "the users.roles column already owns that field name");

        var schema = DbSchema.FromModel(_model);
        var execution = await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = $"{{ users(sort: [id_asc]) {{ data {{ id roles {fieldName}(sort: [id_asc]) {{ data {{ id name }} }} }} }} }}";
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = new SqliteDbConnFactory(ConnString),
                ["model"] = _model,
                ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, BifrostQL.Core.Modules.NullQueryTransformerService.Instance),
            });
        });

        execution.Errors.Should().BeNullOrEmpty(string.Join(" | ", execution.Errors?.Select(e => $"{e.Message} :: {e.InnerException?.Message}") ?? []));
        var json = new GraphQLSerializer().Serialize(execution);
        json.Should().Be(
            "{\"data\":{\"users\":{\"data\":[" +
            $"{{\"id\":1,\"roles\":\"admin,editor\",\"{fieldName}\":{{\"data\":[{{\"id\":1,\"name\":\"admin\"}},{{\"id\":2,\"name\":\"editor\"}}]}}}}," +
            $"{{\"id\":2,\"roles\":\"viewer\",\"{fieldName}\":{{\"data\":[{{\"id\":3,\"name\":\"viewer\"}}]}}}}" +
            "]}}}");
    }
}
