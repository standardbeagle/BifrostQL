using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace BifrostQL.Core.Test.Model;

public sealed class PolicyRowScopeExemptLoaderPathTests : IAsyncLifetime
{
    private string _connectionString = null!;
    private SqliteConnection _keepAlive = null!;

    public async Task InitializeAsync()
    {
        _connectionString = $"Data Source=bifrost_row_scope_exempt_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await new SqliteCommand(
            "CREATE TABLE time_entries (id INTEGER PRIMARY KEY, tenant_id INTEGER, user_id INTEGER);",
            conn).ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public async Task Loader_ModelCarryingRowScopeExemption_LoadsAndCollects()
    {
        var loader = new DbModelLoader(
            new SqliteDbConnFactory(_connectionString),
            new MetadataLoader(new[]
            {
                "main.time_entries { policy-actions: read,update,delete; policy-row-scope: user_id = {user_id}; policy-row-scope-exempt: time.edit_others }",
            }));

        var model = await loader.LoadAsync();

        PolicyConfigCollector.FromTable(model.GetTableFromDbName("time_entries"))
            .RowScopeExemptGrants.Should().Contain("time.edit_others");
    }

    [Fact]
    public async Task Loader_EmptyRowScopeExemption_IsRejected()
    {
        var loader = new DbModelLoader(
            new SqliteDbConnFactory(_connectionString),
            new MetadataLoader(new[]
            {
                "main.time_entries { policy-actions: read; policy-row-scope: user_id = {user_id}; policy-row-scope-exempt: }",
            }));

        var act = () => loader.LoadAsync();

        await act.Should().ThrowAsync<Exception>();
    }
}
