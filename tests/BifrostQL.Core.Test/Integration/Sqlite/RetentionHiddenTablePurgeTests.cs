using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Retention;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using GraphQL.Types;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// A table marked <c>visibility: hidden</c> is withheld from every exposed surface, but its
/// <c>ttl</c>/<c>retain</c> still has to run: a host hides an internal table (sessions, tokens)
/// precisely because only the server touches it, and those are the tables that expire. Before
/// the fix the purge iterated only the exposed tables, so a hidden table's expired rows stayed
/// forever.
/// </summary>
public sealed class RetentionHiddenTablePurgeTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_retention_hidden_test;Mode=Memory;Cache=Shared";
    private const string EndpointPath = "/graphql";
    private SqliteConnection _keepAlive = null!;

    private static readonly DateTime Now = new(2050, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Expired = "2000-01-01 00:00:00";
    private const string Fresh = "2100-01-01 00:00:00";

    private static readonly string[] Rules =
    {
        // The agency shape: an internal session table, hidden, expiring on its own column.
        "*.app_sessions { visibility: hidden; ttl: 1d after expires_at }",
        // Hidden AND tenant-scoped: the purge must still fan out per tenant under the pipeline.
        "*.tenant_tokens { visibility: hidden; tenant-filter: tenant_id; ttl: 1d after expires_at }",
    };

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS app_sessions");
        await Exec("CREATE TABLE app_sessions (id INTEGER PRIMARY KEY, expires_at DATETIME NOT NULL)");
        await Exec($"INSERT INTO app_sessions(id, expires_at) VALUES (1, '{Expired}'), (2, '{Fresh}')");

        await Exec("DROP TABLE IF EXISTS tenant_tokens");
        await Exec("CREATE TABLE tenant_tokens (id INTEGER PRIMARY KEY, tenant_id INTEGER NOT NULL, expires_at DATETIME NOT NULL)");
        await Exec(
            $"""
            INSERT INTO tenant_tokens(id, tenant_id, expires_at) VALUES
                (1, 1, '{Expired}'),
                (2, 2, '{Expired}'),
                (3, 1, '{Fresh}')
            """);

        // One exposed table so the GraphQL schema has a surface to inspect.
        await Exec("DROP TABLE IF EXISTS notes");
        await Exec("CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL)");
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

    private static (RetentionPurgeEngine Engine, IMutationIntentExecutor Writer, QueryIntentExecutor Reader) Build()
    {
        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader(EndpointPath, async () =>
        {
            var factory = new SqliteDbConnFactory(ConnString);
            var model = await new DbModelLoader(factory, new MetadataLoader(Rules)).LoadAsync();
            return new Inputs(new Dictionary<string, object?>
            {
                ["model"] = model,
                ["dbSchema"] = DbSchema.FromModel(model),
                ["connFactory"] = factory,
            });
        });

        var reader = new QueryIntentExecutor(pathCache, new QueryTransformerService(new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[]
            {
                new TenantFilterTransformer(),
                new SoftDeleteFilterTransformer(),
                new PolicyFilterTransformer(),
            },
        }));

        var writer = new MutationIntentExecutor(pathCache, new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[]
            {
                new PolicyMutationTransformer(),
                new SoftDeleteMutationTransformer(),
                new TenantMutationTransformer(),
                new AuditMutationTransformer(),
            },
        });

        var engine = new RetentionPurgeEngine(reader, writer, pathCache, clock: () => Now);
        return (engine, writer, reader);
    }

    [Fact]
    public async Task PurgeOnce_HiddenTableWithTtl_PurgesExpiredRow_KeepsLiveRow()
    {
        // Arrange
        var (engine, _, reader) = Build();
        var model = await reader.GetModelAsync(EndpointPath);
        var connFactory = new SqliteDbConnFactory(ConnString);

        // Act
        var outcome = await engine.PurgeOnceAsync(model, connFactory, EndpointPath, CancellationToken.None);

        // Assert: both hidden tables were swept; app_sessions lost its 1 expired row and
        // tenant_tokens lost the expired row of each tenant.
        outcome.TablesSwept.Should().Be(2, "both hidden tables declare a ttl");
        outcome.TablesSkipped.Should().Be(0);
        outcome.RowsPurged.Should().Be(3);
        (await ScalarAsync("SELECT COUNT(*) FROM app_sessions WHERE id = 1")).Should().Be("0", "the expired session is purged");
        (await ScalarAsync("SELECT COUNT(*) FROM app_sessions WHERE id = 2")).Should().Be("1", "the live session is kept");
        (await ScalarAsync("SELECT COUNT(*) FROM tenant_tokens WHERE id IN (1, 2)")).Should().Be("0");
        (await ScalarAsync("SELECT COUNT(*) FROM tenant_tokens WHERE id = 3")).Should().Be("1", "the live token is kept");
    }

    [Fact]
    public async Task HiddenTable_StaysAbsentFromExposedModelAndGraphQlSchema()
    {
        // Arrange
        var (_, _, reader) = Build();

        // Act
        var model = await reader.GetModelAsync(EndpointPath);
        var schema = DbSchema.FromModel(model);
        schema.Initialize();

        // Assert: neither the exposed table list, the client-name lookup every wire write
        // resolves through, nor the GraphQL schema names a hidden table.
        model.Tables.Select(t => t.DbName).Should().BeEquivalentTo(new[] { "notes" });
        model.TryGetTableFromClientName("app_sessions", out _).Should().BeFalse();
        model.TryGetTableFromClientName("tenant_tokens", out _).Should().BeFalse();
        var typeNames = schema.AllTypes.Select(t => t.Name).ToArray();
        typeNames.Should().Contain("notes");
        typeNames.Should().NotContain(n => n.Contains("app_sessions") || n.Contains("tenant_tokens"));
        schema.Query.Fields.Select(f => f.Name).Should().NotContain(n => n.Contains("app_sessions") || n.Contains("tenant_tokens"));
    }

    [Fact]
    public async Task HiddenTenantTable_SweepUnderTenantA_LeavesTenantBExpiredRow()
    {
        // Arrange
        var (engine, _, reader) = Build();
        var model = await reader.GetModelAsync(EndpointPath);
        var tokens = model.HiddenTables.Single(t => t.DbName == "tenant_tokens");
        var config = RetentionConfig.FromTable(tokens);

        // Act: tenant 1's synthesized context only.
        var purged = await engine.SweepTableForTenantAsync(
            model, tokens, config, "tenant_id", tenantValue: 1L, EndpointPath, Now, CancellationToken.None);

        // Assert: the pipeline narrowed the purge to tenant 1, hidden or not.
        purged.Should().Be(1);
        (await ScalarAsync("SELECT COUNT(*) FROM tenant_tokens WHERE id = 1")).Should().Be("0");
        (await ScalarAsync("SELECT COUNT(*) FROM tenant_tokens WHERE id = 2")).Should().Be("1", "tenant 2's expired token survives a tenant-1 sweep");
        (await ScalarAsync("SELECT COUNT(*) FROM tenant_tokens WHERE id = 3")).Should().Be("1");
    }

    [Fact]
    public async Task IntentWithoutHiddenTableAccess_CannotReadOrWriteHiddenTable()
    {
        // Arrange: a wire caller (adapter, MCP tool) builds intents without the capability.
        var (_, writer, reader) = Build();
        var model = await reader.GetModelAsync(EndpointPath);
        var sessions = model.HiddenTables.Single(t => t.DbName == "app_sessions");

        // Act
        var write = () => writer.ExecuteAsync(new MutationIntent
        {
            Table = "app_sessions",
            Action = MutationIntentAction.Delete,
            Data = new Dictionary<string, object?>(),
            PrimaryKey = new object?[] { 1 },
            Endpoint = EndpointPath,
        });
        var read = () => reader.ExecuteAsync(new QueryIntent
        {
            Query = new GqlObjectQuery
            {
                DbTable = sessions,
                SchemaName = sessions.TableSchema,
                TableName = sessions.DbName,
                GraphQlName = sessions.GraphQlName,
                Path = sessions.GraphQlName,
                ScalarColumns = { new GqlObjectColumn("id") },
            },
            Endpoint = EndpointPath,
        });

        // Assert: both are the same sanitized miss an unknown table gets, and nothing changed.
        await write.Should().ThrowAsync<BifrostExecutionError>();
        await read.Should().ThrowAsync<BifrostExecutionError>();
        (await ScalarAsync("SELECT COUNT(*) FROM app_sessions")).Should().Be("2");
    }
}
