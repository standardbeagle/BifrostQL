using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Test.TestSupport;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.QueryModel;

/// <summary>
/// Asserts the <em>meaning</em> of generated SQL via the parsed AST (SqlFacts):
/// the projection references only known columns, the queried table appears, the
/// configured limit/offset are emitted as parsed integers, and a filter produces
/// a real WHERE with a bound parameter. Catches semantic drift that substring
/// assertions miss.
/// </summary>
public sealed class SqlSemanticTests
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

    private static IDbModel Model() => DbModelTestFixture.Create()
        .WithTable("Users", t => t
            .WithPrimaryKey("Id")
            .WithColumn("Name", "nvarchar")
            .WithColumn("Email", "nvarchar"))
        .Build();

    private static string GenerateUsersSql(IDbModel model, Action<GqlObjectQuery> configure)
    {
        var users = model.GetTableFromDbName("Users");
        var query = new GqlObjectQuery
        {
            DbTable = users,
            TableName = "Users",
            GraphQlName = "Users",
            Path = "Users",
            ScalarColumns = { new GqlObjectColumn("Id"), new GqlObjectColumn("Name") },
        };
        configure(query);
        query.ConnectLinks(model);
        var sqls = new Dictionary<string, ParameterizedSql>();
        query.AddSqlParameterized(model, Dialect, sqls, new SqlParameterCollection());
        return sqls["Users"].Sql;
    }

    [Fact]
    public void Projection_ReferencesOnlySelectedKnownColumns()
    {
        var model = Model();
        var facts = SqlFacts.Parse(GenerateUsersSql(model, _ => { }));

        var modelColumns = model.GetTableFromDbName("Users").Columns.Select(c => c.ColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        facts.Tables.Should().Contain("Users");
        facts.Columns.Should().Contain("Id").And.Contain("Name");
        // Not selected → must not appear in the projection.
        facts.Columns.Should().NotContain("Email");
        // Every referenced column is a real model column or a known synthetic alias.
        facts.Columns.Where(c => !SqlFacts.SyntheticColumns.Contains(c))
            .Should().OnlyContain(c => modelColumns.Contains(c),
                "the builder must never reference a column that does not exist on the table");
    }

    [Fact]
    public void LimitAndOffset_AreEmittedAsConfiguredIntegers()
    {
        var model = Model();
        var facts = SqlFacts.Parse(GenerateUsersSql(model, q => { q.Limit = 7; q.Offset = 3; }));

        facts.Fetch.Should().Be(7);
        facts.Offset.Should().Be(3);
    }

    [Fact]
    public void Filter_ProducesWhereWithBoundParameterOnFilterColumn()
    {
        var model = Model();
        var facts = SqlFacts.Parse(GenerateUsersSql(model, q =>
            q.Filter = TableFilter.FromObject(
                new Dictionary<string, object?> { ["Id"] = new Dictionary<string, object?> { ["_eq"] = 42 } },
                "Users")));

        facts.HasWhere.Should().BeTrue();
        facts.Columns.Should().Contain("Id");
        facts.ParameterCount.Should().BeGreaterThan(0, "the filter value must be parameterized, not inlined");
    }

    [Fact]
    public void NoFilter_EmitsNoWhereClause()
    {
        var model = Model();
        var facts = SqlFacts.Parse(GenerateUsersSql(model, _ => { }));

        facts.HasWhere.Should().BeFalse();
    }
}
