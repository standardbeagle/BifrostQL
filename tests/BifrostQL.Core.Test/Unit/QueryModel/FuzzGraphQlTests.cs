using System.Text.RegularExpressions;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using GraphQLParser;
using Xunit;

namespace BifrostQL.Core.Test.QueryModel;

/// <summary>
/// GraphQL fuzzer: generates schema-valid GraphQL query strings (random column
/// subsets, args, nested link collections) against a fixed model, runs them
/// through the real pipeline (Parser → SqlVisitor → GetFinalQueries →
/// AddSqlParameterized), and asserts every emitted statement parses as valid SQL
/// for each dialect. Exercises the full string→SQL path the server uses. Seeds
/// are fixed so failures reproduce from the test name.
/// </summary>
[Trait("Category", "Fuzz")]
public sealed class FuzzGraphQlTests
{
    private static readonly (ISqlDialect Dialect, SqlFlavor Flavor)[] Dialects =
    {
        (SqlServerDialect.Instance, SqlFlavor.SqlServer),
        (PostgresDialect.Instance, SqlFlavor.Postgres),
        (MySqlDialect.Instance, SqlFlavor.MySql),
        (SqliteDialect.Instance, SqlFlavor.Sqlite),
    };

    private static readonly Regex Ident = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    [Theory]
    [InlineData(0x6011)]
    [InlineData(0x5EED)]
    [InlineData(0x1234)]
    [InlineData(0xABCD)]
    public async Task RandomGraphQlQueries_ProduceParseableSql(int seed)
    {
        var rnd = new Random(seed);

        for (var iter = 0; iter < 80; iter++)
        {
            // Fresh model per iteration: GetFinalQueries connects links onto the
            // model's link DTOs, so a shared instance could accumulate state.
            var model = new DbModel { Tables = BifrostQL.Core.QueryModel.SqlVisitorToSqlTest.GetFakeTables() };
            var gql = BuildQuery(model, rnd);

            List<GqlObjectQuery> queries;
            try
            {
                var ctx = new SqlContext();
                await new SqlVisitor().VisitAsync(Parser.Parse(gql), ctx);
                queries = ctx.GetFinalQueries(model);
            }
            catch (Exception ex)
            {
                throw new Xunit.Sdk.XunitException(
                    $"seed=0x{seed:X} iter={iter}: pipeline threw {ex.GetType().Name}: {ex.Message}\nGraphQL: {gql}");
            }

            foreach (var (dialect, flavor) in Dialects)
            {
                foreach (var q in queries)
                {
                    var sqls = new Dictionary<string, ParameterizedSql>();
                    try
                    {
                        q.AddSqlParameterized(model, dialect, sqls, new SqlParameterCollection());
                    }
                    catch (Exception ex)
                    {
                        throw new Xunit.Sdk.XunitException(
                            $"seed=0x{seed:X} iter={iter} {flavor}: AddSqlParameterized threw {ex.GetType().Name}: {ex.Message}\nGraphQL: {gql}");
                    }

                    foreach (var (key, sql) in sqls)
                        SqlSyntax.AssertValid(sql.Sql, flavor, $"seed=0x{seed:X} iter={iter} key='{key}'\nGraphQL: {gql}");
                }
            }
        }
    }

    private static string BuildQuery(IDbModel model, Random rnd)
    {
        var tables = model.Tables.ToList();
        var root = Pick(tables, rnd);
        var args = BuildArgs(root, rnd);
        var body = BuildBody(model, root, rnd, depth: 0);
        return $"query {{ {root.GraphQlName}{args} {{ data {{ {body} }} }} }}";
    }

    private static string BuildArgs(IDbTable table, Random rnd)
    {
        var parts = new List<string>();
        if (rnd.Next(2) == 0)
        {
            var col = TryPickColumn(table, rnd);
            if (col != null)
            {
                var op = Pick(new[] { "_eq", "_neq", "_lt", "_lte", "_gt", "_gte" }, rnd);
                parts.Add($"filter: {{ {col}: {{ {op}: {rnd.Next(1, 1000)} }} }}");
            }
        }
        if (rnd.Next(2) == 0) parts.Add($"limit: {Pick(new[] { 1, 5, 25, 100 }, rnd)}");
        if (rnd.Next(2) == 0) parts.Add($"offset: {Pick(new[] { 0, 3, 50 }, rnd)}");
        return parts.Count == 0 ? "" : $"({string.Join(" ", parts)})";
    }

    private static string BuildBody(IDbModel model, IDbTable table, Random rnd, int depth)
    {
        var parts = ColumnNames(table).Where(_ => rnd.Next(2) == 0).ToList();
        if (parts.Count == 0)
        {
            var any = TryPickColumn(table, rnd);
            parts.Add(any ?? "id");
        }

        if (depth < 1)
        {
            // Multi-link child collection: paged, so it uses the { data { ... } } wrapper.
            foreach (var (name, link) in table.MultiLinks.Where(_ => rnd.Next(3) == 0))
            {
                if (!Ident.IsMatch(name) || !InModel(model, link.ChildTable)) continue;
                parts.Add($"{name} {{ data {{ {BuildBody(model, link.ChildTable, rnd, depth + 1)} }} }}");
            }
            // Single-link: scalar object, no data wrapper. Target is the ParentTable.
            foreach (var (name, link) in table.SingleLinks.Where(_ => rnd.Next(3) == 0))
            {
                if (!Ident.IsMatch(name) || !InModel(model, link.ParentTable)) continue;
                parts.Add($"{name} {{ {BuildBody(model, link.ParentTable, rnd, depth + 1)} }}");
            }
        }

        return string.Join(" ", parts);
    }

    private static bool InModel(IDbModel model, IDbTable t)
        => model.Tables.Any(mt => mt.GraphQlName == t.GraphQlName);

    private static IReadOnlyList<string> ColumnNames(IDbTable table)
        => table.GraphQlLookup.Keys.Where(k => Ident.IsMatch(k)).ToList();

    private static string? TryPickColumn(IDbTable table, Random rnd)
    {
        var cols = ColumnNames(table);
        return cols.Count == 0 ? null : cols[rnd.Next(cols.Count)];
    }

    private static T Pick<T>(IReadOnlyList<T> items, Random rnd) => items[rnd.Next(items.Count)];
}
