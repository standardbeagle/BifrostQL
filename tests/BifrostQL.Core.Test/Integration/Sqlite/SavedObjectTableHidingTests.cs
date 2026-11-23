using BifrostQL.Core.Model;
using BifrostQL.Core.Schema;
using BifrostQL.Core.SavedObjects;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL.Types;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// The DB saved-object store provisions its table inside the database a GraphQL endpoint
/// usually serves. That table holds every owner's saved objects, so it must never be part of a
/// served model: neither under its default name nor under a configured one.
/// </summary>
public sealed class SavedObjectTableHidingTests : IDisposable
{
    private const string ConfiguredTableName = "_bifrost_saved_objects_tenant_a";
    private readonly List<SqliteConnection> _keepAlive = new();

    public void Dispose()
    {
        foreach (var c in _keepAlive) c.Dispose();
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task DefaultAndConfiguredSavedObjectTables_AreAbsentFromTheGraphQlSchema()
    {
        var conn = $"Data Source=bifrost_so_hide_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var keepAlive = new SqliteConnection(conn);
        await keepAlive.OpenAsync();
        _keepAlive.Add(keepAlive);
        await using (var cmd = new SqliteCommand("CREATE TABLE widgets (id INTEGER PRIMARY KEY, name TEXT)", keepAlive))
            await cmd.ExecuteNonQueryAsync();

        var factory = new SqliteDbConnFactory(conn);
        var owner = SavedObjectOwner.FromUserContext(new Dictionary<string, object?> { ["user_id"] = "alice" }, "tenant_id")!;
        // Provision both tables through the production store, so the fixture carries the exact
        // names and shape the store creates.
        await new DbSavedObjectStore(factory).ListAsync(owner, null);
        await new DbSavedObjectStore(factory, tableName: ConfiguredTableName).ListAsync(owner, null);

        var model = await new DbModelLoader(factory, new MetadataLoader(Array.Empty<string>())).LoadAsync();
        var schema = DbSchema.FromModel(model);
        schema.Initialize();
        var rootFields = schema.Query!.Fields.Select(f => f.Name).ToList();

        rootFields.Should().Contain("widgets", "control: an ordinary table is served");
        rootFields.Should().NotContain(f => f.Contains("saved_objects", StringComparison.OrdinalIgnoreCase));
        model.Tables.Select(t => t.DbName).Should()
            .NotContain(new[] { DbSavedObjectStore.DefaultTableName, ConfiguredTableName });
        model.HiddenTables.Select(t => t.DbName).Should()
            .Contain(new[] { DbSavedObjectStore.DefaultTableName, ConfiguredTableName });
    }

    [Theory]
    [InlineData("_bifrost_saved_objectsx")]
    [InlineData("saved_objects")]
    [InlineData("app_saved")]
    [InlineData("_bifrost_auth_saved")]
    public void AConfiguredTableNameOutsideTheReservedPrefix_IsRefused(string tableName)
    {
        var factory = new SqliteDbConnFactory("Data Source=:memory:");

        var act = () => new DbSavedObjectStore(factory, tableName: tableName);

        act.Should().Throw<ArgumentException>().WithParameterName("tableName");
    }
}
