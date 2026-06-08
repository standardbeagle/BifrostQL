using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// Hardening coverage for computed-column parsing and SQL rendering — the
/// security guard against injected SQL tokens and the dependency-resolution
/// failure paths that had no direct tests.
/// </summary>
public sealed class ComputedColumnHardeningTests
{
    [Theory]
    [InlineData("{subtotal} -- comment")]
    [InlineData("{subtotal} /* block */")]
    [InlineData("{subtotal} */")]
    public void RenderSqlExpression_RejectsSqlControlTokens(string expression)
    {
        var model = BuildModel($"bad:Float:{expression}");
        var table = model.GetTableFromDbName("Orders");
        var computed = ComputedColumnConfigCollector.Find(table, "bad")!;

        var act = () => computed.RenderSqlExpression(table, SqlServerDialect.Instance);

        act.Should().Throw<BifrostExecutionError>()
            .WithMessage("*unsupported SQL control tokens*");
    }

    [Fact]
    public void RenderSqlExpression_RejectsStatementSeparator()
    {
        // ';' is the metadata entry separator, so the collector strips it before
        // it can reach an expression. The guard is defense-in-depth — verify it
        // directly on a hand-built definition.
        var model = BuildModel();
        var table = model.GetTableFromDbName("Orders");
        var computed = new ComputedColumnDefinition(
            "bad", "Float", ComputedColumnKind.Sql,
            "{subtotal}; DROP TABLE Orders", new[] { "subtotal" });

        var act = () => computed.RenderSqlExpression(table, SqlServerDialect.Instance);

        act.Should().Throw<BifrostExecutionError>()
            .WithMessage("*unsupported SQL control tokens*");
    }

    [Fact]
    public void RenderSqlExpression_UnknownDependency_Throws()
    {
        var model = BuildModel("bad:Float:({subtotal} + {does_not_exist})");
        var table = model.GetTableFromDbName("Orders");
        var computed = ComputedColumnConfigCollector.Find(table, "bad")!;

        var act = () => computed.RenderSqlExpression(table, SqlServerDialect.Instance);

        act.Should().Throw<BifrostExecutionError>()
            .WithMessage("*does_not_exist*was not found*");
    }

    [Fact]
    public void RenderSqlExpression_WithTableAlias_QualifiesColumns()
    {
        var model = BuildModel("totalWithTax:Float:({subtotal} + {tax})");
        var table = model.GetTableFromDbName("Orders");
        var computed = ComputedColumnConfigCollector.Find(table, "totalWithTax")!;

        var sql = computed.RenderSqlExpression(table, SqlServerDialect.Instance, "a");

        sql.Should().Be("([a].[subtotal] + [a].[tax])");
    }

    [Fact]
    public void RenderSqlExpression_OnProviderColumn_Throws()
    {
        var model = BuildModel(providerRaw: "ship:String:shipping-api");
        var table = model.GetTableFromDbName("Orders");
        var computed = ComputedColumnConfigCollector.Find(table, "ship")!;

        var act = () => computed.RenderSqlExpression(table, SqlServerDialect.Instance);

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("1bad:Float:{subtotal}")]   // invalid graphql name (leading digit)
    [InlineData("ok:Float")]                 // too few parts
    [InlineData("ok::({subtotal})")]         // empty type
    [InlineData("ok:Float:   ")]             // empty expression
    public void ParseSql_SkipsMalformedEntries(string raw)
    {
        var model = BuildModel(raw);
        var table = model.GetTableFromDbName("Orders");

        ComputedColumnConfigCollector.FromTable(table)
            .Where(c => c.Kind == ComputedColumnKind.Sql)
            .Should().BeEmpty();
    }

    [Fact]
    public void ParseProvider_WithoutDepends_HasNoDeclaredDependencies()
    {
        var model = BuildModel(providerRaw: "ship:String:shipping-api");
        var table = model.GetTableFromDbName("Orders");
        var computed = ComputedColumnConfigCollector.Find(table, "ship")!;

        computed.Kind.Should().Be(ComputedColumnKind.Provider);
        computed.ExpressionOrProvider.Should().Be("shipping-api");
        computed.Dependencies.Should().BeEmpty();
    }

    [Fact]
    public void ParseProvider_ParsesDependencyList()
    {
        var model = BuildModel(providerRaw: "ship:String:shipping-api:depends=Id,destination_zip");
        var table = model.GetTableFromDbName("Orders");
        var computed = ComputedColumnConfigCollector.Find(table, "ship")!;

        computed.Dependencies.Should().BeEquivalentTo("Id", "destination_zip");
    }

    [Fact]
    public void Find_IsCaseInsensitiveOnName()
    {
        var model = BuildModel("totalWithTax:Float:({subtotal} + {tax})");
        var table = model.GetTableFromDbName("Orders");

        ComputedColumnConfigCollector.Find(table, "TOTALWITHTAX").Should().NotBeNull();
    }

    private static IDbModel BuildModel(string? sqlRaw = null, string? providerRaw = null)
    {
        return DbModelTestFixture.Create()
            .WithTable("Orders", t =>
            {
                t.WithPrimaryKey("Id")
                    .WithColumn("subtotal", "decimal")
                    .WithColumn("tax", "decimal")
                    .WithColumn("destination_zip", "varchar");
                if (sqlRaw != null)
                    t.WithMetadata(MetadataKeys.Computed.Sql, sqlRaw);
                if (providerRaw != null)
                    t.WithMetadata(MetadataKeys.Computed.Provider, providerRaw);
            })
            .Build();
    }
}
