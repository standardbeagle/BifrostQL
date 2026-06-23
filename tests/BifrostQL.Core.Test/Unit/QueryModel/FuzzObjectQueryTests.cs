using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Test.TestSupport;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.QueryModel;

/// <summary>
/// Object-level fuzzer: builds randomized <see cref="GqlObjectQuery"/> trees
/// (random column subsets, sort, limit/offset, filters, child collections) from a
/// fixed model and runs <c>AddSqlParameterized</c> across every dialect. Each
/// generated statement must (1) never throw and (2) parse as valid SQL for its
/// engine. Seeds are fixed so any failure reproduces from the test name.
/// </summary>
public sealed class FuzzObjectQueryTests
{
    private static readonly (ISqlDialect Dialect, SqlFlavor Flavor)[] Dialects =
    {
        (SqlServerDialect.Instance, SqlFlavor.SqlServer),
        (PostgresDialect.Instance, SqlFlavor.Postgres),
        (MySqlDialect.Instance, SqlFlavor.MySql),
        (SqliteDialect.Instance, SqlFlavor.Sqlite),
    };

    private static IDbModel BuildModel() => DbModelTestFixture.Create()
        .WithTable("Users", t => t
            .WithPrimaryKey("Id")
            .WithColumn("Name", "nvarchar")
            .WithColumn("Email", "nvarchar")
            .WithColumn("Age", "int"))
        .WithTable("Orders", t => t
            .WithPrimaryKey("Id")
            .WithColumn("UserId", "int")
            .WithColumn("Total", "decimal")
            .WithColumn("Status", "nvarchar"))
        .WithSingleLink("Orders", "UserId", "Users", "Id", "User")
        .WithMultiLink("Users", "Id", "Orders", "UserId", "Orders")
        .Build();

    [Theory]
    [InlineData(0xA11CE)]
    [InlineData(0xB0B)]
    [InlineData(0xC0FFEE)]
    [InlineData(0xD15EA5E)]
    [InlineData(0xFEED)]
    public void RandomQueryTrees_AlwaysProduceParseableSql(int seed)
    {
        var model = BuildModel();
        var rnd = new Random(seed);

        for (var iter = 0; iter < 120; iter++)
        {
            var query = BuildRandomQuery(model, rnd);
            // ConnectLinks mutates Links -> Joins, so wire once before emitting SQL
            // for each dialect from the connected tree.
            query.ConnectLinks(model);

            foreach (var (dialect, flavor) in Dialects)
            {
                var sqls = new Dictionary<string, ParameterizedSql>();
                try
                {
                    query.AddSqlParameterized(model, dialect, sqls, new SqlParameterCollection());
                }
                catch (Exception ex)
                {
                    throw new Xunit.Sdk.XunitException(
                        $"seed=0x{seed:X} iter={iter} dialect={flavor}: AddSqlParameterized threw {ex.GetType().Name}: {ex.Message}");
                }

                foreach (var (key, sql) in sqls)
                    SqlSyntax.AssertValid(sql.Sql, flavor, $"seed=0x{seed:X} iter={iter} key='{key}'");
            }
        }
    }

    private static GqlObjectQuery BuildRandomQuery(IDbModel model, Random rnd)
    {
        var users = model.GetTableFromDbName("Users");
        var query = new GqlObjectQuery
        {
            DbTable = users,
            TableName = "Users",
            GraphQlName = "Users",
            Path = "Users",
            IncludeResult = true,
        };

        foreach (var c in RandomNonEmptySubset(users.Columns.Select(c => c.ColumnName).ToList(), rnd))
            query.ScalarColumns.Add(new GqlObjectColumn(c));

        // Random sort over a subset of columns.
        foreach (var c in RandomSubset(users.Columns.Select(c => c.ColumnName).ToList(), rnd))
            query.Sort.Add($"{c}_{(rnd.Next(2) == 0 ? "asc" : "desc")}");

        ApplyRandomPaging(query, rnd);

        // Sometimes filter on a random column.
        if (rnd.Next(2) == 0)
        {
            var col = Pick(users.Columns.ToList(), rnd);
            query.Filter = TableFilter.FromObject(RandomFilter(col, rnd), "Users");
        }

        // Sometimes attach the Orders child collection.
        if (rnd.Next(2) == 0 && users.MultiLinks.TryGetValue("Orders", out var link))
        {
            var child = new GqlObjectQuery
            {
                // ConnectLinks matches the child against the parent's MultiLinks key.
                GraphQlName = "Orders",
                IncludeResult = true,
            };
            foreach (var c in RandomNonEmptySubset(link.ChildTable.Columns.Select(c => c.ColumnName).ToList(), rnd))
                child.ScalarColumns.Add(new GqlObjectColumn(c));
            ApplyRandomPaging(child, rnd);
            query.Links.Add(child);
        }

        return query;
    }

    private static void ApplyRandomPaging(GqlObjectQuery q, Random rnd)
    {
        q.Limit = Pick(new int?[] { null, -1, 1, 5, 25, 100 }, rnd);
        q.Offset = Pick(new int?[] { null, 0, 3, 50 }, rnd);
    }

    private static Dictionary<string, object?> RandomFilter(ColumnDto col, Random rnd)
    {
        var numeric = IsNumeric(col.DataType);
        var op = numeric
            ? Pick(new[] { "_eq", "_neq", "_lt", "_lte", "_gt", "_gte", "_in", "_null" }, rnd)
            : Pick(new[] { "_eq", "_neq", "_contains", "_starts_with", "_ends_with", "_in", "_null" }, rnd);

        object? value = op switch
        {
            "_null" => rnd.Next(2) == 0,
            "_in" => numeric
                ? new List<object?> { rnd.Next(1, 100), rnd.Next(1, 100) }
                : new List<object?> { "a" + rnd.Next(50), "b" + rnd.Next(50) },
            _ => numeric ? rnd.Next(1, 1000) : "v" + rnd.Next(100),
        };

        return new Dictionary<string, object?>
        {
            [col.ColumnName] = new Dictionary<string, object?> { [op] = value },
        };
    }

    private static bool IsNumeric(string dataType) => dataType.ToLowerInvariant() switch
    {
        "int" or "bigint" or "smallint" or "tinyint" or "decimal" or "numeric" or "float" or "real" or "money" => true,
        _ => false,
    };

    private static T Pick<T>(IReadOnlyList<T> items, Random rnd) => items[rnd.Next(items.Count)];

    private static List<string> RandomSubset(List<string> items, Random rnd)
        => items.Where(_ => rnd.Next(2) == 0).ToList();

    private static List<string> RandomNonEmptySubset(List<string> items, Random rnd)
    {
        var subset = RandomSubset(items, rnd);
        if (subset.Count == 0)
            subset.Add(Pick(items, rnd));
        return subset;
    }
}
