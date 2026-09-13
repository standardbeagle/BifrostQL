using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// Loader-path proof for the S6b table keys <c>policy-self-deny</c> and
/// <c>policy-self-column</c>: both must be allow-listed in
/// <c>MetadataValidator.KnownTableKeys</c> or <see cref="ModelConfigValidator"/>'s
/// unknown-key gate hard-errors any real model that uses them. Unit fixtures
/// bypass <see cref="DbModelLoader"/>, so only this path proves the keys reach a
/// loaded model.
/// </summary>
public sealed class PolicySelfDenyLoaderPathTests : IAsyncLifetime
{
    private string _connectionString = null!;
    private SqliteConnection _keepAlive = null!;

    public async Task InitializeAsync()
    {
        _connectionString = $"Data Source=bifrost_s6b_loader_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        var ddl = new SqliteCommand(
            @"CREATE TABLE users (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                permission_profile_id INTEGER NULL,
                cost_rate REAL NOT NULL,
                display_name TEXT NOT NULL
            );", conn);
        await ddl.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public async Task Loader_ModelCarryingSelfDenyKeys_LoadsAndCollects()
    {
        var metadata = new[]
        {
            "main.users { policy-actions: read,update; policy-self-deny: permission_profile_id, cost_rate; policy-self-column: id }",
        };
        var loader = new DbModelLoader(
            new SqliteDbConnFactory(_connectionString), new MetadataLoader(metadata));

        var model = await loader.LoadAsync();

        var policy = PolicyConfigCollector.FromTable(model.GetTableFromDbName("users"));
        policy.SelfDenyColumns.Should().BeEquivalentTo("permission_profile_id", "cost_rate");
        policy.SelfColumn.Should().Be("id");
    }
}
