using BifrostQL.Core.QueryModel;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Integration.Test.Navigation;

/// <summary>
/// Cross-dialect composite-FK navigation against TenantInventory →
/// TenantLocations via composite (TenantId, LocationId).
/// Seed data deliberately collides on LocationId=10 between Tenant 1 and
/// Tenant 2 — a single-column join would crosswire those rows. The
/// composite emission must alias src_id_0 / src_id_1 and AND the ON-clause.
/// </summary>
public abstract class CompositeForeignKeyTestBase<TDatabase> : IClassFixture<DatabaseFixture<TDatabase>>
    where TDatabase : IIntegrationTestDatabase, new()
{
    protected readonly DatabaseFixture<TDatabase> Fixture;

    protected CompositeForeignKeyTestBase(DatabaseFixture<TDatabase> fixture) => Fixture = fixture;

    private GqlObjectQuery BuildQuery(string tableName, int? limit = null)
    {
        Fixture.EnsureAvailable();
        var db = Fixture.Database;
        var table = db.DbModel.GetTableFromDbName(tableName);
        return new GqlObjectQuery
        {
            DbTable = table,
            TableName = tableName,
            GraphQlName = tableName,
            SchemaName = table.TableSchema,
            Limit = limit ?? -1,
            ScalarColumns = table.Columns.Select(c => new GqlObjectColumn(c.ColumnName)).ToList(),
        };
    }

    [SkippableFact]
    public void CompositeFK_InventoryToLocation_NavigatesViaCompositeKey()
    {
        var inventoryQuery = BuildQuery("TenantInventory");
        inventoryQuery.Sort = new List<string> { "Id_asc" };
        var locationLink = BuildQuery("TenantLocations");
        locationLink.GraphQlName = "location";

        inventoryQuery.Links.Add(locationLink);
        inventoryQuery.ConnectLinks(Fixture.Database.DbModel);

        var results = QueryExecutor.Execute(inventoryQuery, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        results.Should().ContainKey("TenantInventory");
        results["TenantInventory"].data.Should().HaveCount(TestSchema.Counts.TenantInventory);

        var joinKey = inventoryQuery.Joins.First().JoinName;
        results.Should().ContainKey(joinKey);
        var joinTable = results[joinKey];

        joinTable.index.Should().ContainKey("src_id_0");
        joinTable.index.Should().ContainKey("src_id_1");
        joinTable.index.Should().NotContainKey("src_id",
            "composite-FK join must use suffixed src_id_<i> aliases, not the single-column form");

        // Cross-check: every inventory row's (TenantId, LocationId) resolves
        // to exactly one location row in the join result.
        var invIdx = results["TenantInventory"].index;
        var src0 = joinTable.index["src_id_0"];
        var src1 = joinTable.index["src_id_1"];
        var nameIdx = joinTable.index["Name"];

        foreach (var invRow in results["TenantInventory"].data)
        {
            var tenantId = Convert.ToInt64(invRow[invIdx["TenantId"]]);
            var locationId = Convert.ToInt64(invRow[invIdx["LocationId"]]);

            var matches = joinTable.data
                .Where(r => Convert.ToInt64(r[src0]) == tenantId
                         && Convert.ToInt64(r[src1]) == locationId)
                .ToList();
            matches.Should().HaveCount(1,
                $"composite ({tenantId},{locationId}) must match exactly one location row");

            var expected = (tenantId, locationId) switch
            {
                (1, 10) => "Acme/North",
                (1, 20) => "Acme/South",
                (2, 10) => "Globex/North",
                (2, 30) => "Globex/East",
                _ => throw new InvalidOperationException($"unexpected ({tenantId},{locationId})"),
            };
            ((string)matches[0][nameIdx]!).Should().Be(expected);
        }
    }

    [SkippableFact]
    public void CompositeFK_LocationToInventory_FansOutOnCompositeKey()
    {
        var locationsQuery = BuildQuery("TenantLocations");
        locationsQuery.Sort = new List<string> { "TenantId_asc", "LocationId_asc" };
        var inventoryLink = BuildQuery("TenantInventory");
        inventoryLink.GraphQlName = "inventory";

        locationsQuery.Links.Add(inventoryLink);
        locationsQuery.ConnectLinks(Fixture.Database.DbModel);

        var results = QueryExecutor.Execute(locationsQuery, Fixture.Database.DbModel, Fixture.Database.ConnFactory);

        results.Should().ContainKey("TenantLocations");
        results["TenantLocations"].data.Should().HaveCount(TestSchema.Counts.TenantLocations);

        var joinKey = locationsQuery.Joins.First().JoinName;
        results.Should().ContainKey(joinKey);
        var joinTable = results[joinKey];

        joinTable.index.Should().ContainKey("src_id_0");
        joinTable.index.Should().ContainKey("src_id_1");
        joinTable.index.Should().NotContainKey("src_id");

        var src0 = joinTable.index["src_id_0"];
        var src1 = joinTable.index["src_id_1"];

        // Each of 4 locations seeds 2 inventory rows = 8 total.
        joinTable.data.Should().HaveCount(TestSchema.Counts.TenantInventory);
        foreach (var (tenantId, locationId) in new[] { (1L, 10L), (1L, 20L), (2L, 10L), (2L, 30L) })
        {
            var count = joinTable.data.Count(r =>
                Convert.ToInt64(r[src0]) == tenantId &&
                Convert.ToInt64(r[src1]) == locationId);
            count.Should().Be(2,
                $"location ({tenantId},{locationId}) must fan out to exactly 2 inventory rows");
        }
    }
}

public sealed class SqliteCompositeForeignKeyTests : CompositeForeignKeyTestBase<SqliteTestDatabase>
{
    public SqliteCompositeForeignKeyTests(DatabaseFixture<SqliteTestDatabase> fixture) : base(fixture) { }
}

public sealed class SqlServerCompositeForeignKeyTests : CompositeForeignKeyTestBase<SqlServerTestDatabase>
{
    public SqlServerCompositeForeignKeyTests(DatabaseFixture<SqlServerTestDatabase> fixture) : base(fixture) { }
}

public sealed class PostgresCompositeForeignKeyTests : CompositeForeignKeyTestBase<PostgresTestDatabase>
{
    public PostgresCompositeForeignKeyTests(DatabaseFixture<PostgresTestDatabase> fixture) : base(fixture) { }
}

public sealed class MySqlCompositeForeignKeyTests : CompositeForeignKeyTestBase<MySqlTestDatabase>
{
    public MySqlCompositeForeignKeyTests(DatabaseFixture<MySqlTestDatabase> fixture) : base(fixture) { }
}
