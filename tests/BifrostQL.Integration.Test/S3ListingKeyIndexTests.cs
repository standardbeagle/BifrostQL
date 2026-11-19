using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Integration.Test.Infrastructure;
using BifrostQL.Model;
using FluentAssertions;

namespace BifrostQL.Integration.Test;

/// <summary>
/// The provisioning SQL in <c>docs/src/content/docs/guides/s3.md</c> ("Indexing the object
/// key") makes the engine serve a listing seek from the index instead of computing the key
/// for every row. The seek is the query <c>S3Listing</c> sends: the table's key and file
/// columns, <c>file IS NOT NULL</c>, an object-key window resuming after a key, and a page
/// of 1000 + 1 rows. Paired control: the same plan before the index is created does not
/// use it.
/// </summary>
public abstract class S3ListingKeyIndexTestBase
{
    protected const string Table = "okindexed";
    private const int Rows = 20000;
    private const int Fetch = 1001;

    protected abstract IIntegrationTestDatabase CreateDatabase();
    protected abstract bool Available { get; }
    protected abstract string CreateTableSql { get; }

    /// <summary>The guide's provisioning statements for an integer key, verbatim.</summary>
    protected abstract IReadOnlyList<string> IndexSql { get; }

    /// <summary>Statements that make the planner see the table's size (ANALYZE and the like).</summary>
    protected abstract IReadOnlyList<string> RefreshStatisticsSql { get; }

    protected abstract Task<string> PlanAsync(DbConnection conn, string sql, IReadOnlyList<SqlParameterInfo> parameters);

    /// <summary>
    /// A plan step that reads the provisioned index in key order from the resume bound: a
    /// range read, not a scan of the whole index.
    /// </summary>
    protected abstract System.Text.RegularExpressions.Regex IndexRangeRead { get; }

    [SkippableFact]
    public async Task The_guide_index_serves_the_listing_seek()
    {
        Skip.IfNot(Available, "test database connection string not set");
        await using var db = CreateDatabase();
        await db.InitializeAsync();
        await using var conn = db.ConnFactory.GetConnection();
        await conn.OpenAsync();
        await ExecAsync(conn, CreateTableSql);
        await ExecAsync(conn, SeedSql);
        foreach (var statement in RefreshStatisticsSql)
            await ExecAsync(conn, statement);

        var (sql, parameters) = await SeekSqlAsync(db);
        var before = await PlanAsync(conn, sql, parameters);
        IndexRangeRead.IsMatch(before).Should().BeFalse($"control: without the index the plan cannot read it; plan:\n{before}");

        foreach (var statement in IndexSql)
            await ExecAsync(conn, statement);
        foreach (var statement in RefreshStatisticsSql)
            await ExecAsync(conn, statement);
        var after = await PlanAsync(conn, sql, parameters);

        IndexRangeRead.IsMatch(after).Should().BeTrue($"the seek must read a range of the key index; plan:\n{after}");
    }

    /// <summary>Rows 1..20000, each with a file pointer.</summary>
    protected virtual string SeedSql =>
        $"WITH RECURSIVE g(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM g WHERE i < {Rows}) "
        + $"INSERT INTO {Table} (k, body) SELECT i, 'x' FROM g";

    private async Task<(string Sql, IReadOnlyList<SqlParameterInfo> Parameters)> SeekSqlAsync(IIntegrationTestDatabase db)
    {
        var model = await new DbModelLoader(db.ConnFactory, new MetadataLoader(new[] { $"*.{Table}.body {{ file: json }}" })).LoadAsync();
        var table = model.Tables.Single(t => t.DbName == Table);
        var body = table.ColumnLookup["body"];
        var query = new GqlObjectQuery
        {
            DbTable = table,
            SchemaName = table.TableSchema,
            TableName = table.DbName,
            GraphQlName = table.GraphQlName,
            Path = table.GraphQlName,
            Limit = Fetch,
            ObjectKeyRange = new ObjectKeyRange("5000", null, null, new[] { (Type?)typeof(int) }),
            Filter = TableFilterBuilder.For(table).IsNull(body.GraphQlName, isNull: false).Build(),
        };
        query.ScalarColumns.Add(new GqlObjectColumn("k", "k"));
        query.ScalarColumns.Add(new GqlObjectColumn("body", "body"));
        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, db.Dialect, sqls, parameters);
        return (sqls.Values.Single().Sql, parameters.Parameters);
    }

    protected static DbCommand Command(DbConnection conn, string sql, IReadOnlyList<SqlParameterInfo> parameters)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var info in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = info.Name;
            p.Value = info.Value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    protected static async Task<string> ReadPlanAsync(DbCommand cmd)
    {
        var lines = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        do
        {
            while (await reader.ReadAsync())
                lines.Add(string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i)?.ToString())));
        } while (await reader.NextResultAsync());
        return string.Join("\n", lines);
    }

    protected static async Task ExecAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}

public sealed class PostgresS3ListingKeyIndexTests : S3ListingKeyIndexTestBase
{
    protected override IIntegrationTestDatabase CreateDatabase() => new PostgresTestDatabase();
    protected override bool Available => PostgresTestDatabase.ConnectionString is not null;
    protected override string CreateTableSql => $"CREATE TABLE {Table} (k integer PRIMARY KEY, body text NULL)";
    protected override IReadOnlyList<string> IndexSql => new[]
    {
        $"CREATE INDEX {Table}_s3_key ON {Table} ((CAST(k AS text) COLLATE \"C\"))",
    };
    protected override IReadOnlyList<string> RefreshStatisticsSql => new[] { $"ANALYZE {Table}" };
    protected override System.Text.RegularExpressions.Regex IndexRangeRead =>
        new($@"Index Scan using {Table}_s3_key on {Table}[^\n]*\n\s+Index Cond: ");

    protected override async Task<string> PlanAsync(DbConnection conn, string sql, IReadOnlyList<SqlParameterInfo> parameters)
    {
        await using var cmd = Command(conn, "EXPLAIN " + sql, parameters);
        return await ReadPlanAsync(cmd);
    }
}

public sealed class MySqlS3ListingKeyIndexTests : S3ListingKeyIndexTestBase
{
    protected override IIntegrationTestDatabase CreateDatabase() => new MySqlTestDatabase();
    protected override bool Available => MySqlTestDatabase.ConnectionString is not null;
    protected override string CreateTableSql => $"CREATE TABLE {Table} (k INT NOT NULL PRIMARY KEY, body TEXT NULL)";
    protected override IReadOnlyList<string> IndexSql => new[]
    {
        $"CREATE INDEX {Table}_s3_key ON {Table} ((CAST(CAST(k AS CHAR) AS BINARY)))",
    };
    protected override IReadOnlyList<string> RefreshStatisticsSql => new[] { $"ANALYZE TABLE {Table}" };
    protected override System.Text.RegularExpressions.Regex IndexRangeRead =>
        new($@"Index range scan on {Table} using {Table}_s3_key");
    // MySQL takes the CTE after INSERT and caps recursion at 1000 by default.
    protected override string SeedSql => "SET SESSION cte_max_recursion_depth = 30000; "
        + $"INSERT INTO {Table} (k, body) WITH RECURSIVE g(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM g WHERE i < 20000) SELECT i, 'x' FROM g";

    protected override async Task<string> PlanAsync(DbConnection conn, string sql, IReadOnlyList<SqlParameterInfo> parameters)
    {
        await using var cmd = Command(conn, "EXPLAIN FORMAT=TREE " + sql, parameters);
        return await ReadPlanAsync(cmd);
    }
}

public sealed class SqlServerS3ListingKeyIndexTests : S3ListingKeyIndexTestBase
{
    protected override IIntegrationTestDatabase CreateDatabase() => new SqlServerTestDatabase();
    protected override bool Available => SqlServerTestDatabase.ConnectionString is not null;
    protected override string CreateTableSql => $"CREATE TABLE {Table} (k INT NOT NULL PRIMARY KEY, body NVARCHAR(MAX) NULL)";
    protected override IReadOnlyList<string> IndexSql => new[]
    {
        $"ALTER TABLE {Table} ADD s3_key AS (CONVERT(varchar(20), k) COLLATE Latin1_General_100_BIN2) PERSISTED",
        $"CREATE INDEX {Table}_s3_key ON {Table} (s3_key) INCLUDE (body)",
    };
    protected override IReadOnlyList<string> RefreshStatisticsSql => new[] { $"UPDATE STATISTICS {Table}" };
    protected override System.Text.RegularExpressions.Regex IndexRangeRead =>
        new($@"Index Seek\(OBJECT:\(\[[^\]]+\]\.\[dbo\]\.\[{Table}\]\.\[{Table}_s3_key\]\), SEEK:");
    protected override string SeedSql =>
        $"WITH g(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM g WHERE i < 20000) INSERT INTO {Table} (k, body) SELECT i, 'x' FROM g OPTION (MAXRECURSION 0)";

    protected override async Task<string> PlanAsync(DbConnection conn, string sql, IReadOnlyList<SqlParameterInfo> parameters)
    {
        // SHOWPLAN does not plan an sp_executesql call's inner statement, so the bounds go in
        // as literals of the type they are bound with (nvarchar).
        await ExecAsync(conn, "SET SHOWPLAN_TEXT ON");
        try
        {
            foreach (var p in parameters.Reverse())
                sql = sql.Replace(p.Name, "N'" + p.Value + "'");
            await using var cmd = Command(conn, sql, Array.Empty<SqlParameterInfo>());
            return await ReadPlanAsync(cmd);
        }
        finally
        {
            await ExecAsync(conn, "SET SHOWPLAN_TEXT OFF");
        }
    }
}
