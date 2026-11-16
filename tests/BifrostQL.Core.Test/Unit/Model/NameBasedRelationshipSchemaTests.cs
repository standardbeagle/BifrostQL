using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Model;
using FluentAssertions;
using NSubstitute;
using System.Data.Common;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// Name-based relationship inference (<c>employee_id</c> → <c>employee</c>) resolves the
/// parent inside the child table's own schema first. The pre-fix lookup was one global
/// NormalizedName → table map filled first-wins, so with <c>payroll.employee</c> read
/// before <c>hr.employee</c>, <c>hr.timesheet.employee_id</c> linked across schemas to
/// payroll. A name that exists in two other schemas (and not the child's own) is not
/// inferred at all; a name that exists in exactly one schema still links across.
/// </summary>
public sealed class NameBasedRelationshipSchemaTests
{
    private static DbTable MakeTable(string schema, string name, Action<DbModelTestFixture.TableBuilder> configure)
    {
        var builder = new DbModelTestFixture.TableBuilder(name)
            .WithSchema(schema)
            .WithGraphQlName($"{schema}_{name}");
        configure(builder);
        return builder.Build();
    }

    private static IDbModel Build(params DbTable[] tables)
    {
        var connFactory = Substitute.For<IDbConnFactory>();
        connFactory.GetConnection().Returns(Substitute.For<DbConnection>());
        connFactory.TypeMapper.Returns(SqlServerTypeMapper.Instance);
        var loader = new DbModelLoader(connFactory, new MetadataLoader(Array.Empty<string>()));
        var read = new SchemaData(
            new Dictionary<ColumnRef, List<ColumnConstraintDto>>(),
            Array.Empty<ColumnDto>(),
            tables.Cast<IDbTable>().ToList());
        return loader.BuildModel(read, new MetadataLoader(Array.Empty<string>()));
    }

    private static IEnumerable<IDbTable> ParentsOf(IDbModel model, string schema, string table) =>
        model.GetTableFromDbName(schema, table).SingleLinks.Values.Select(l => l.ParentTable);

    private static DbTable Employee(string schema) =>
        MakeTable(schema, "employee", t => t.WithPrimaryKey("id").WithColumn("name", "nvarchar"));

    [Fact]
    public void Infer_ParentNameInOwnAndEarlierSchema_LinksWithinOwnSchema()
    {
        var model = Build(
            Employee("payroll"),
            Employee("hr"),
            MakeTable("hr", "timesheet", t => t.WithPrimaryKey("id").WithColumn("employee_id", "int")));

        var parents = ParentsOf(model, "hr", "timesheet").ToList();

        parents.Should().ContainSingle()
            .Which.Should().BeSameAs(model.GetTableFromDbName("hr", "employee"));
        model.GetTableFromDbName("payroll", "employee").MultiLinks.Should().BeEmpty(
            "the old target, payroll.employee, must not gain a child link");
    }

    [Fact]
    public void Infer_ParentNameOnlyInTwoOtherSchemas_IsNotInferred()
    {
        var model = Build(
            Employee("payroll"),
            Employee("hr"),
            MakeTable("ops", "timesheet", t => t.WithPrimaryKey("id").WithColumn("employee_id", "int")));

        ParentsOf(model, "ops", "timesheet").Should().BeEmpty();
    }

    [Fact]
    public void Infer_ParentNameOnlyInOneOtherSchema_StillLinksAcross()
    {
        var model = Build(
            Employee("hr"),
            MakeTable("ops", "timesheet", t => t.WithPrimaryKey("id").WithColumn("employee_id", "int")));

        ParentsOf(model, "ops", "timesheet").Should().ContainSingle()
            .Which.Should().BeSameAs(model.GetTableFromDbName("hr", "employee"));
    }
}
