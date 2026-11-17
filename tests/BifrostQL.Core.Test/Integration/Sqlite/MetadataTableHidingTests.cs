using BifrostQL.Core.Model;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using BifrostQL.SqlServer;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// The SQL Server metadata sources read their configuration from tables that usually sit in the
/// database being served. The schema-metadata table sets tenant filters, soft delete and policies,
/// so serving it would let a client rewrite the security metadata of the API it is calling.
/// </summary>
public sealed class MetadataTableHidingTests : IDisposable
{
    private readonly List<SqliteConnection> _keepAlive = new();

    public void Dispose()
    {
        foreach (var c in _keepAlive) c.Dispose();
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task MetadataTables_DefaultAndConfiguredNames_AreHiddenFromTheServedModel()
    {
        var conn = $"Data Source=bifrost_meta_hide_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var keepAlive = new SqliteConnection(conn);
        await keepAlive.OpenAsync();
        _keepAlive.Add(keepAlive);
        string[] metadataTables =
        {
            "_bifrost_config", "_bifrost_config_staging",
            "_bifrost_app_metadata", "_bifrost_app_metadata_v2",
        };
        foreach (var ddl in metadataTables
                     .Select(t => $"CREATE TABLE {t} (id INTEGER PRIMARY KEY, table_name TEXT, key TEXT, value TEXT)")
                     .Append("CREATE TABLE widgets (id INTEGER PRIMARY KEY, name TEXT)"))
        {
            await using var cmd = new SqliteCommand(ddl, keepAlive);
            await cmd.ExecuteNonQueryAsync();
        }

        var model = await new DbModelLoader(new SqliteDbConnFactory(conn), new MetadataLoader(Array.Empty<string>())).LoadAsync();

        model.Tables.Select(t => t.DbName).Should().Contain("widgets", "control: an ordinary table is served")
            .And.NotContain(metadataTables);
        model.HiddenTables.Select(t => t.DbName).Should().Contain(metadataTables);
    }

    [Theory]
    [InlineData("bifrost_config")]
    [InlineData("dbo.app_config")]
    [InlineData("_bifrost_auth_config")]
    public void SchemaMetadataSource_RefusesATableOutsideTheReservedPrefix(string name)
    {
        var act = () => new DatabaseMetadataSource("Server=unused", name);

        act.Should().Throw<ArgumentException>().WithParameterName("configTableName");
    }

    [Fact]
    public void SchemaMetadataSource_AcceptsASchemaQualifiedReservedName()
    {
        var act = () => new DatabaseMetadataSource("Server=unused", "cfg._bifrost_config_staging");

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("app_metadata")]
    [InlineData("_bifrost_config")]
    public void AppMetadataSource_RefusesATableOutsideTheReservedPrefix(string name)
    {
        var act = () => new DatabaseAppMetadataSource("Server=unused", name);

        act.Should().Throw<ArgumentException>().WithParameterName("tableName");
    }
}
