using BifrostQL.Core.Model;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// `bifrost config generate` output fed back through the real loader: detect patterns on the
/// bare schema, generate rules, then load the model with those rules (DbModelLoader runs
/// ModelConfigValidator). Generated actor populators need a model-level user-audit-key, or the
/// generated config is refused at load.
/// </summary>
public sealed class ConfigGeneratorRoundTripTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_config_generator_round_trip;Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        await Exec("DROP TABLE IF EXISTS orders");
        await Exec("DROP TABLE IF EXISTS notes");
        await Exec(
            "CREATE TABLE orders (id INTEGER PRIMARY KEY, title TEXT NOT NULL, " +
            "created_at DATETIME NULL, created_by TEXT NULL, updated_at DATETIME NULL, updated_by TEXT NULL, " +
            "deleted_at DATETIME NULL, deleted_by TEXT NULL)");
        await Exec("CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL, created_at DATETIME NULL)");
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public async Task GeneratedActorPopulators_LoadThroughTheRuntimeValidator()
    {
        var rules = await GenerateAsync();
        rules.Should().Contain(r => r.Contains("populate: created-by"), "the fixture must drive an actor populator");

        var load = () => new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(rules)).LoadAsync();

        await load.Should().NotThrowAsync();
        rules.Should().ContainSingle(r => r == $":root {{ user-audit-key: {MetadataKeys.Auth.DefaultUserAuditKey} }}");
    }

    [Fact]
    public async Task GeneratedTimestampOnlyPopulators_EmitNoUserAuditKey()
    {
        await Exec("DROP TABLE orders");

        var rules = await GenerateAsync();

        rules.Should().Contain(r => r.Contains("populate: created-on"));
        rules.Should().NotContain(r => r.Contains(MetadataKeys.Audit.UserKey));
    }

    private static async Task<IReadOnlyList<string>> GenerateAsync()
    {
        var bare = await new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(Array.Empty<string>())).LoadAsync();
        return new ConfigGenerator().Generate(new ConfigPatternDetector().Detect(bare));
    }

    private async Task Exec(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }
}
