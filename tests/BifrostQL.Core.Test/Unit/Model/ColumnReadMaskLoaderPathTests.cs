using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// Loader-path proof for the S4b read-side grant keys: <c>read-requires</c>
/// (column selector) and <c>deny-mode</c> (column and table level) must be
/// allow-listed in <c>MetadataValidator.KnownColumnKeys</c>/<c>KnownTableKeys</c>,
/// or <see cref="ModelConfigValidator"/>'s unknown-key gate hard-errors any real
/// model that uses them. Unit fixtures bypass <see cref="DbModelLoader"/>, so only
/// this path proves the feature is reachable on a loaded model. The closed
/// <c>deny-mode</c> grammar (null|refuse) must also fail load on a typo rather
/// than silently reading as the permissive default. Runs a shared-cache
/// in-memory SQLite database through the production loader.
/// </summary>
public sealed class ColumnReadMaskLoaderPathTests : IAsyncLifetime
{
    private string _connectionString = null!;
    private SqliteConnection _keepAlive = null!;

    public async Task InitializeAsync()
    {
        _connectionString = $"Data Source=bifrost_s4b_loader_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        var ddl = new SqliteCommand(
            @"CREATE TABLE members (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                cost_rate REAL NOT NULL,
                display_name TEXT NOT NULL
            );", conn);
        await ddl.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public async Task Loader_ModelCarryingReadGrantKeys_LoadsAndCollects()
    {
        var metadata = new[]
        {
            "main.members { policy-actions: read,update }",
            "main.members.cost_rate { read-requires: rates.view_cost; deny-mode: refuse }",
            "main.*.display_name { deny-mode: null }",
        };
        var loader = new DbModelLoader(
            new SqliteDbConnFactory(_connectionString), new MetadataLoader(metadata));

        var model = await loader.LoadAsync();

        var policy = PolicyConfigCollector.FromTable(model.GetTableFromDbName("members"));
        policy.ReadRequires.Should().ContainKey("cost_rate")
            .WhoseValue.Should().BeEquivalentTo("rates.view_cost");
        policy.ColumnDenyModes.Should().ContainKey("cost_rate")
            .WhoseValue.Should().Be("refuse");
        policy.ColumnDenyModes.Should().ContainKey("display_name",
            "the schema.*.column wildcard selector applies to every table's column")
            .WhoseValue.Should().Be("null");
    }

    [Fact]
    public async Task Loader_TableDenyMode_LoadsAndCollects()
    {
        var metadata = new[]
        {
            "main.members { policy-actions: read; policy-read-deny: cost_rate; deny-mode: null }",
        };
        var loader = new DbModelLoader(
            new SqliteDbConnFactory(_connectionString), new MetadataLoader(metadata));

        var model = await loader.LoadAsync();

        PolicyConfigCollector.FromTable(model.GetTableFromDbName("members"))
            .TableDenyMode.Should().Be("null");
    }

    [Fact]
    public async Task Loader_DenyModeTypo_FailsLoad()
    {
        // Closed grammar: a typo'd deny-mode must hard-error at model load, never
        // silently read as one of the two modes.
        var metadata = new[]
        {
            "main.members { policy-actions: read }",
            "main.members.cost_rate { read-requires: rates.view_cost; deny-mode: refus }",
        };
        var loader = new DbModelLoader(
            new SqliteDbConnFactory(_connectionString), new MetadataLoader(metadata));

        var act = () => loader.LoadAsync();

        await act.Should().ThrowAsync<Exception>().WithMessage("*deny-mode*");
    }

    [Fact]
    public async Task Loader_TableDenyModeTypo_FailsLoad()
    {
        var metadata = new[]
        {
            "main.members { policy-actions: read; deny-mode: hidden }",
        };
        var loader = new DbModelLoader(
            new SqliteDbConnFactory(_connectionString), new MetadataLoader(metadata));

        var act = () => loader.LoadAsync();

        await act.Should().ThrowAsync<Exception>().WithMessage("*deny-mode*");
    }
}
