using BifrostQL.Core.Model;
using BifrostQL.Core.Model.Relationships;
using BifrostQL.Core.QueryModel;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Integration.Test.Navigation;

/// <summary>
/// End-to-end composite-FK navigation against a real SQLite database.
/// Schema: Tenants(Id) -- Users(TenantId, UserId composite PK)
///                    -- Orders(Id, TenantId, UserId) FK→Users(TenantId,UserId).
/// Asserts that:
///   (1) The query executes successfully (multi-column ON-clause is valid SQL).
///   (2) The link result set carries suffixed src_id_0 / src_id_1 columns,
///       not the single-column src_id alias.
///   (3) Row keys cross-reference correctly — a child row with (1,10)
///       cannot match a parent row keyed (2,10), which is exactly the
///       single-column join's failure mode.
/// </summary>
public sealed class CompositeForeignKeyTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private SqliteDbConnFactory _connFactory = null!;
    private IDbModel _model = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bifrost_compfk_{Guid.NewGuid():N}.db");
        var connString = $"Data Source={_dbPath}";
        _connFactory = new SqliteDbConnFactory(connString);

        await using var conn = new SqliteConnection(connString);
        await conn.OpenAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE Tenants (
                    Id INTEGER PRIMARY KEY,
                    Name TEXT NOT NULL
                );
                CREATE TABLE Users (
                    TenantId INTEGER NOT NULL,
                    UserId INTEGER NOT NULL,
                    Name TEXT NOT NULL,
                    PRIMARY KEY (TenantId, UserId)
                );
                CREATE TABLE Orders (
                    Id INTEGER PRIMARY KEY,
                    TenantId INTEGER NOT NULL,
                    UserId INTEGER NOT NULL,
                    Total REAL NOT NULL,
                    FOREIGN KEY (TenantId, UserId) REFERENCES Users(TenantId, UserId)
                );

                INSERT INTO Tenants (Id, Name) VALUES (1,'Acme'),(2,'Globex');
                INSERT INTO Users (TenantId, UserId, Name) VALUES
                    (1, 10, 'Alice@Acme'),
                    (1, 11, 'Bob@Acme'),
                    (2, 10, 'Alice@Globex'),
                    (2, 20, 'Carol@Globex');
                INSERT INTO Orders (Id, TenantId, UserId, Total) VALUES
                    (1, 1, 10, 100.0),
                    (2, 1, 11, 200.0),
                    (3, 2, 10, 300.0),
                    (4, 2, 20, 400.0),
                    (5, 1, 10, 500.0);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        _model = BuildModel();
    }

    public Task DisposeAsync()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public void CompositeFK_ChildToParent_NavigatesViaCompositeKey()
    {
        var ordersTable = _model.GetTableFromDbName("Orders");
        var usersTable = _model.GetTableFromDbName("Users");

        var ordersQuery = new GqlObjectQuery
        {
            DbTable = ordersTable,
            TableName = "Orders",
            GraphQlName = "Orders",
            SchemaName = "",
            Limit = -1,
            Sort = new List<string> { "Id_asc" },
            ScalarColumns = ordersTable.Columns
                .Select(c => new GqlObjectColumn(c.ColumnName))
                .ToList(),
        };
        var userLink = new GqlObjectQuery
        {
            DbTable = usersTable,
            TableName = "Users",
            GraphQlName = usersTable.GraphQlName,
            SchemaName = "",
            Limit = -1,
            ScalarColumns = usersTable.Columns
                .Select(c => new GqlObjectColumn(c.ColumnName))
                .ToList(),
        };
        ordersQuery.Links.Add(userLink);
        ordersQuery.ConnectLinks(_model);

        var results = BifrostQL.Integration.Test.Infrastructure.QueryExecutor
            .Execute(ordersQuery, _model, _connFactory);

        results.Should().ContainKey("Orders");
        results["Orders"].data.Should().HaveCount(5);

        var joinKey = ordersQuery.Joins.First().JoinName;
        results.Should().ContainKey(joinKey);
        var joinTable = results[joinKey];

        // Composite emission must alias src_id columns with suffixes.
        joinTable.index.Should().ContainKey("src_id_0");
        joinTable.index.Should().ContainKey("src_id_1");
        joinTable.index.Should().NotContainKey("src_id");
        joinTable.index.Should().ContainKey("Name");

        // Walk every order row, locate the matching user row by (TenantId,UserId),
        // assert names line up. A single-column join would cross-wire (1,10) and (2,10).
        var ordersIdx = results["Orders"].index;
        var src0 = joinTable.index["src_id_0"];
        var src1 = joinTable.index["src_id_1"];
        var nameIdx = joinTable.index["Name"];

        foreach (var orderRow in results["Orders"].data)
        {
            var tenantId = Convert.ToInt64(orderRow[ordersIdx["TenantId"]]);
            var userId = Convert.ToInt64(orderRow[ordersIdx["UserId"]]);

            var matches = joinTable.data
                .Where(r => Convert.ToInt64(r[src0]) == tenantId
                         && Convert.ToInt64(r[src1]) == userId)
                .ToList();

            matches.Should().HaveCount(1,
                $"composite key ({tenantId},{userId}) must match exactly one user row");
            var name = (string)matches[0][nameIdx]!;
            var expected = (tenantId, userId) switch
            {
                (1, 10) => "Alice@Acme",
                (1, 11) => "Bob@Acme",
                (2, 10) => "Alice@Globex",
                (2, 20) => "Carol@Globex",
                _ => throw new InvalidOperationException($"unexpected ({tenantId},{userId})"),
            };
            name.Should().Be(expected);
        }
    }

    [Fact]
    public void CompositeFK_ParentToChildren_FansOutOnCompositeKey()
    {
        var usersTable = _model.GetTableFromDbName("Users");
        var ordersTable = _model.GetTableFromDbName("Orders");

        var usersQuery = new GqlObjectQuery
        {
            DbTable = usersTable,
            TableName = "Users",
            GraphQlName = "Users",
            SchemaName = "",
            Limit = -1,
            Sort = new List<string> { "TenantId_asc", "UserId_asc" },
            ScalarColumns = usersTable.Columns
                .Select(c => new GqlObjectColumn(c.ColumnName))
                .ToList(),
        };
        var ordersLink = new GqlObjectQuery
        {
            DbTable = ordersTable,
            TableName = "Orders",
            GraphQlName = ordersTable.GraphQlName,
            SchemaName = "",
            Limit = -1,
            ScalarColumns = ordersTable.Columns
                .Select(c => new GqlObjectColumn(c.ColumnName))
                .ToList(),
        };
        usersQuery.Links.Add(ordersLink);
        usersQuery.ConnectLinks(_model);

        var results = BifrostQL.Integration.Test.Infrastructure.QueryExecutor
            .Execute(usersQuery, _model, _connFactory);

        results.Should().ContainKey("Users");
        results["Users"].data.Should().HaveCount(4);

        var joinKey = usersQuery.Joins.First().JoinName;
        results.Should().ContainKey(joinKey);
        var joinTable = results[joinKey];

        joinTable.index.Should().ContainKey("src_id_0");
        joinTable.index.Should().ContainKey("src_id_1");
        joinTable.index.Should().NotContainKey("src_id");

        var src0 = joinTable.index["src_id_0"];
        var src1 = joinTable.index["src_id_1"];

        // Acme/Alice (1,10) → 2 orders; Acme/Bob (1,11) → 1; Globex/Alice (2,10) → 1; Globex/Carol (2,20) → 1.
        var expectedCounts = new Dictionary<(long, long), int>
        {
            { (1, 10), 2 },
            { (1, 11), 1 },
            { (2, 10), 1 },
            { (2, 20), 1 },
        };

        foreach (var ((tenantId, userId), expected) in expectedCounts)
        {
            var count = joinTable.data.Count(r =>
                Convert.ToInt64(r[src0]) == tenantId &&
                Convert.ToInt64(r[src1]) == userId);
            count.Should().Be(expected,
                $"user ({tenantId},{userId}) must fan out to exactly {expected} orders");
        }
    }

    private static IDbModel BuildModel()
    {
        var tenants = BuildTable("Tenants", new[]
        {
            Col("Id", "int", isPrimaryKey: true),
            Col("Name", "nvarchar"),
        });
        var users = BuildTable("Users", new[]
        {
            Col("TenantId", "int", isPrimaryKey: true),
            Col("UserId", "int", isPrimaryKey: true),
            Col("Name", "nvarchar"),
        });
        var orders = BuildTable("Orders", new[]
        {
            Col("Id", "int", isPrimaryKey: true),
            Col("TenantId", "int"),
            Col("UserId", "int"),
            Col("Total", "decimal"),
        });

        var model = new InMemoryModel(new IDbTable[] { tenants, users, orders });
        new ForeignKeyRelationshipStrategy().DiscoverRelationships(model, new[]
        {
            new DbForeignKey
            {
                ConstraintName = "FK_Orders_Users",
                ChildTableSchema = "",
                ChildTableName = "Orders",
                ChildColumnNames = new[] { "TenantId", "UserId" },
                ParentTableSchema = "",
                ParentTableName = "Users",
                ParentColumnNames = new[] { "TenantId", "UserId" },
            }
        });
        return model;
    }

    private static DbTable BuildTable(string name, ColumnDto[] columns)
    {
        return new DbTable
        {
            DbName = name,
            GraphQlName = name,
            NormalizedName = name.ToLowerInvariant(),
            TableSchema = "",
            TableType = "BASE TABLE",
            ColumnLookup = columns.ToDictionary(c => c.ColumnName, c => c),
            GraphQlLookup = columns.ToDictionary(c => c.GraphQlName, c => c),
        };
    }

    private static ColumnDto Col(string name, string dataType, bool isPrimaryKey = false, bool isNullable = false) => new()
    {
        ColumnName = name,
        GraphQlName = name,
        NormalizedName = name.ToLowerInvariant(),
        DataType = dataType,
        IsPrimaryKey = isPrimaryKey,
        IsNullable = isNullable,
    };

    private sealed class InMemoryModel : IDbModel
    {
        private readonly Dictionary<string, IDbTable> _byDbName;
        private readonly Dictionary<string, IDbTable> _byGqlName;

        public InMemoryModel(IReadOnlyList<IDbTable> tables)
        {
            Tables = tables;
            _byDbName = tables.ToDictionary(t => t.DbName, t => t);
            _byGqlName = tables.ToDictionary(t => t.GraphQlName, t => t);
        }

        public IReadOnlyCollection<IDbTable> Tables { get; }
        public IReadOnlyCollection<DbStoredProcedure> StoredProcedures { get; } = Array.Empty<DbStoredProcedure>();
        public IDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>();
        public string? GetMetadataValue(string property) =>
            Metadata.TryGetValue(property, out var v) ? v?.ToString() : null;
        public bool GetMetadataBool(string property, bool defaultValue) =>
            Metadata.TryGetValue(property, out var v) ? v?.ToString() == "true" : defaultValue;
        public IDbTable GetTableByFullGraphQlName(string fullName) =>
            _byGqlName.TryGetValue(fullName, out var t) ? t : throw new KeyNotFoundException(fullName);
        public IDbTable GetTableFromDbName(string tableName) =>
            _byDbName.TryGetValue(tableName, out var t) ? t : throw new KeyNotFoundException(tableName);
    }
}
