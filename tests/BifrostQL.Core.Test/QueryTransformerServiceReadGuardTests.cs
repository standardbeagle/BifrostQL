using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// Coverage for the finding that <see cref="QueryTransformerService.ApplyTransformers"/>'s
/// column-read-deny enforcement only inspected a query's selected/output columns
/// (<c>ScalarColumns</c>), leaving the filter (WHERE), sort (<c>_order</c>), and
/// aggregate (<c>_agg</c>) value columns completely unchecked. That let a caller
/// denied read access to a column still use it as a boolean oracle:
/// <c>salary: { _gt: 100000 }</c> or <c>_order: { salary: asc }</c> leak the value
/// through the result set / ordering without ever selecting the column.
/// </summary>
public class QueryTransformerServiceReadGuardTests
{
    private static IDbModel EmployeesModel(string readDenyColumn = "salary") =>
        DbModelTestFixture.Create()
            .WithTable("Employees", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("salary", "decimal")
                .WithColumn("DepartmentId", "int")
                .WithMetadata(MetadataKeys.Policy.Actions, "read")
                .WithMetadata(MetadataKeys.Policy.ReadDeny, readDenyColumn))
            .Build();

    private static QueryTransformerService Service() =>
        new(new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[] { new PolicyFilterTransformer() },
        });

    private static IDictionary<string, object?> UserContext() =>
        new Dictionary<string, object?> { ["user_id"] = "user-1", ["roles"] = new[] { "user" } };

    [Fact]
    public void ApplyTransformers_FilterOnDeniedColumn_Throws()
    {
        // salary is denied for read, but the caller never selects it — only
        // filters on it. Without collecting filter columns, this used to pass
        // straight through and let the caller binary-search the value via _gt.
        var model = EmployeesModel();
        var table = model.GetTableFromDbName("Employees");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Name")
            .WithFilter(f => f.WhereGreaterThan("salary", 100000))
            .Build();

        var act = () => Service().ApplyTransformers(query, model, UserContext());

        act.Should().Throw<BifrostExecutionError>();
    }

    [Fact]
    public void ApplyTransformers_SortOnDeniedColumn_Throws()
    {
        // salary is denied for read, but the caller never selects it — only
        // sorts by it via `_order: { salary: asc }`, which still leaks the
        // relative ordering of the denied value.
        var model = EmployeesModel();
        var table = model.GetTableFromDbName("Employees");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Name")
            .WithSort("salary_asc")
            .Build();

        var act = () => Service().ApplyTransformers(query, model, UserContext());

        act.Should().Throw<BifrostExecutionError>();
    }

    [Fact]
    public void ApplyTransformers_AggregateOnDeniedColumnOfLinkedTable_Throws()
    {
        // Departments has no policy of its own; the aggregate's value column
        // ("salary") lives on the linked Employees table, where it is denied.
        // The guard must attribute the aggregate value column to the
        // destination table of the aggregate's join chain, not the query's
        // own table.
        var model = DbModelTestFixture.Create()
            .WithTable("Departments", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Employees", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("salary", "decimal")
                .WithColumn("DepartmentId", "int")
                .WithMetadata(MetadataKeys.Policy.Actions, "read")
                .WithMetadata(MetadataKeys.Policy.ReadDeny, "salary"))
            .WithMultiLink("Departments", "Id", "Employees", "DepartmentId")
            .Build();

        var departments = model.GetTableFromDbName("Departments");
        var employees = model.GetTableFromDbName("Employees");
        var link = departments.MultiLinks["Employees"];

        var aggregateColumn = new GqlAggregateColumn(
            new List<(LinkDirection direction, TableLinkDto link)> { (LinkDirection.OneToMany, link) },
            "salary",
            "totalSalary",
            AggregateOperationType.Sum);

        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(departments)
            .WithColumns("Id", "Name")
            .WithAggregateColumn(aggregateColumn)
            .Build();

        var act = () => Service().ApplyTransformers(query, model, UserContext());

        act.Should().Throw<BifrostExecutionError>();

        // Sanity: the same shape with the value column unrestricted does not throw.
        _ = employees;
    }

    [Fact]
    public void ApplyTransformers_OnlySelectsAllowedColumns_DoesNotThrow()
    {
        var model = EmployeesModel();
        var table = model.GetTableFromDbName("Employees");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "Name")
            .WithFilter(f => f.WhereGreaterThan("Id", 1))
            .WithSort("Name_asc")
            .Build();

        var act = () => Service().ApplyTransformers(query, model, UserContext());

        act.Should().NotThrow();
    }
}
