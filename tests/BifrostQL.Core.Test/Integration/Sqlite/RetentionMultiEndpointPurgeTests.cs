using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Retention;
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
/// One host can serve several endpoints over the same database, each with its own visibility and
/// metadata. The retention purge sweeps physical tables, not one endpoint's exposed view: each
/// physical table at most once per pass, through every endpoint's view of it (hidden or not), and
/// when the endpoints that declare retention for a table disagree, the table is skipped
/// (fail-closed) rather than swept under whichever endpoint came last.
/// </summary>
public sealed class RetentionMultiEndpointPurgeTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_retention_multi_endpoint_test;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;

    private static readonly DateTime Now = new(2050, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Expired = "2000-01-01 00:00:00";
    private const string Fresh = "2100-01-01 00:00:00";

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();

        await Exec("DROP TABLE IF EXISTS sessions");
        await Exec("CREATE TABLE sessions (id INTEGER PRIMARY KEY, expires_at DATETIME NOT NULL)");
        await Exec($"INSERT INTO sessions(id, expires_at) VALUES (1, '{Expired}'), (2, '{Fresh}')");

        // An exposed table on every endpoint so each endpoint's GraphQL schema has a surface.
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

    // Two endpoints over the SAME database, each with its own metadata rules.
    private static (RetentionPurgeEngine Engine, QueryIntentExecutor Reader) Build(string[] rulesA, string[] rulesB)
    {
        var pathCache = new PathCache<Inputs>();
        AddEndpoint(pathCache, "/a", rulesA);
        AddEndpoint(pathCache, "/b", rulesB);

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

        return (new RetentionPurgeEngine(reader, writer, pathCache, clock: () => Now), reader);
    }

    private static void AddEndpoint(PathCache<Inputs> pathCache, string path, string[] rules)
    {
        pathCache.AddLoader(path, async () =>
        {
            var factory = new SqliteDbConnFactory(ConnString);
            var model = await new DbModelLoader(factory, new MetadataLoader(rules)).LoadAsync();
            return new Inputs(new Dictionary<string, object?>
            {
                ["model"] = model,
                ["dbSchema"] = DbSchema.FromModel(model),
                ["connFactory"] = factory,
            });
        });
    }

    [Fact]
    public async Task TableHiddenOnOneEndpointVisibleOnAnother_IsSweptExactlyOnce_AndKeepsEachEndpointsVisibility()
    {
        // Arrange
        var (engine, reader) = Build(
            new[] { "*.sessions { visibility: hidden; ttl: 1d after expires_at }" },
            new[] { "*.sessions { ttl: 1d after expires_at }" });

        // Act: a dry run reports every candidate the pass reads without deleting, so a physical
        // table swept once per endpoint would report its expired row twice.
        var dry = await engine.PurgeEndpointsOnceAsync(CancellationToken.None, dryRun: true);
        var live = await engine.PurgeEndpointsOnceAsync(CancellationToken.None);

        // Assert
        dry.TablesSwept.Should().Be(1, "both endpoints map one physical table");
        Convert.ToInt64(dry.Candidates.Should().ContainSingle().Which.PrimaryKey[0]).Should().Be(1);
        live.TablesSwept.Should().Be(1);
        live.RowsPurged.Should().Be(1);
        (await ScalarAsync("SELECT COUNT(*) FROM sessions WHERE id = 1")).Should().Be("0", "the expired session is purged");
        (await ScalarAsync("SELECT COUNT(*) FROM sessions WHERE id = 2")).Should().Be("1", "the live session is kept");

        // Each endpoint's exposure is its own: hidden on /a, visible on /b.
        var modelA = await reader.GetModelAsync("/a");
        var modelB = await reader.GetModelAsync("/b");
        modelA.TryGetTableFromClientName("sessions", out _).Should().BeFalse();
        modelA.HiddenTables.Select(t => t.DbName).Should().Contain("sessions");
        modelB.TryGetTableFromClientName("sessions", out _).Should().BeTrue();
        modelB.HiddenTables.Should().BeEmpty();
    }

    [Fact]
    public async Task TableHiddenOnEveryEndpoint_DeclaredOnOne_IsSweptByTheDeclaringEndpoint()
    {
        // Arrange: /a hides the table and declares no retention (no opinion); /b hides it too
        // and declares the ttl.
        var (engine, _) = Build(
            new[] { "*.sessions { visibility: hidden }" },
            new[] { "*.sessions { visibility: hidden; ttl: 1d after expires_at }" });

        // Act
        var outcome = await engine.PurgeEndpointsOnceAsync(CancellationToken.None);

        // Assert
        outcome.TablesSwept.Should().Be(1);
        outcome.TablesSkipped.Should().Be(0);
        outcome.RowsPurged.Should().Be(1);
        (await ScalarAsync("SELECT COUNT(*) FROM sessions")).Should().Be("1", "only the live session remains");
    }

    [Fact]
    public async Task ConflictingTtlAcrossEndpoints_SkipsTheTable_FailClosed_NeitherWindowWins()
    {
        // Arrange: /a expires sessions after 1 day, /b after 30 days. The expired row is past
        // both windows, so ANY sweep — under either window — would delete it.
        var (engine, _) = Build(
            new[] { "*.sessions { visibility: hidden; ttl: 1d after expires_at }" },
            new[] { "*.sessions { ttl: 30d after expires_at }" });

        // Act
        var outcome = await engine.PurgeEndpointsOnceAsync(CancellationToken.None);

        // Assert: the disagreement is a config error for that table — skipped, nothing deleted.
        outcome.TablesSwept.Should().Be(0);
        outcome.TablesSkipped.Should().Be(1);
        outcome.RowsPurged.Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM sessions")).Should().Be("2", "a table the endpoints disagree about is never purged");
    }
}
