using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// Loader-path proof for the S4a write-side grant keys: <c>write-requires</c>
/// (column selector, including the <c>schema.*.column</c> wildcard form) and
/// <c>policy-write-deny-roles</c> (table level) must be allow-listed in
/// <c>MetadataValidator.KnownColumnKeys</c>/<c>KnownTableKeys</c>, or
/// <see cref="ModelConfigValidator"/>'s unknown-key gate hard-errors any real
/// model that uses them. Unit fixtures bypass <see cref="DbModelLoader"/>, so
/// only this path proves the feature is reachable on a loaded model. Runs a
/// shared-cache in-memory SQLite database through the production loader.
/// </summary>
public sealed class PolicyWriteGrantLoaderPathTests : IAsyncLifetime
{
    private string _connectionString = null!;
    private SqliteConnection _keepAlive = null!;

    public async Task InitializeAsync()
    {
        _connectionString = $"Data Source=bifrost_s4a_loader_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        var ddl = new SqliteCommand(
            @"CREATE TABLE users (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                cost_rate REAL NOT NULL,
                rate_code TEXT NULL,
                total REAL NOT NULL
            );", conn);
        await ddl.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public async Task Loader_ModelCarryingWriteGrantKeys_LoadsAndCollects()
    {
        var metadata = new[]
        {
            "main.users { policy-write-deny: total }",
            "main.users { policy-write-deny-roles: member }",
            "main.users.cost_rate { write-requires: team.manage }",
            "main.*.rate_code { write-requires: profiles.manage }",
        };
        var loader = new DbModelLoader(
            new SqliteDbConnFactory(_connectionString), new MetadataLoader(metadata));

        var model = await loader.LoadAsync();

        var policy = PolicyConfigCollector.FromTable(model.GetTableFromDbName("users"));
        policy.WriteRequires.Should().ContainKey("cost_rate")
            .WhoseValue.Should().BeEquivalentTo("team.manage");
        policy.WriteRequires.Should().ContainKey("rate_code",
            "the schema.*.column wildcard selector applies to every table's column");
        policy.WriteRequires["rate_code"].Should().BeEquivalentTo("profiles.manage");
        policy.WriteDenyColumns.Should().BeEquivalentTo("total");
        policy.WriteDenyRoles.Should().BeEquivalentTo("member");
    }
}
