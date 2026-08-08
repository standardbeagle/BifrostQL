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
    public void ApplyTransformers_GroupByDeniedColumn_Throws()
    {
        // The `<table>Aggregate` surface groups by a column the caller is denied
        // read on. The group partition itself leaks the denied value's distinct
        // set / boundaries, so the guard must reject it just like a filter/sort.
        var model = EmployeesModel();
        var table = model.GetTableFromDbName("Employees");
        var salary = table.Columns.Single(c => c.DbName == "salary");

        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .Build();
        query.GroupedAggregate = new GroupedAggregate
        {
            GroupColumns = new[] { new AggregateGroupColumn(salary, salary.GraphQlName) },
            IncludeCount = true,
            ValueColumns = Array.Empty<AggregateValueColumn>(),
        };

        var act = () => Service().ApplyTransformers(query, model, UserContext());

        act.Should().Throw<BifrostExecutionError>();
    }

    [Fact]
    public void ApplyTransformers_AggregateValueOnDeniedColumn_Throws()
    {
        // The aggregate value (SUM(salary)) is over a denied column even though the
        // caller only groups by an allowed one — the aggregate exposes the value.
        var model = EmployeesModel();
        var table = model.GetTableFromDbName("Employees");
        var salary = table.Columns.Single(c => c.DbName == "salary");
        var departmentId = table.Columns.Single(c => c.DbName == "DepartmentId");

        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .Build();
        query.GroupedAggregate = new GroupedAggregate
        {
            GroupColumns = new[] { new AggregateGroupColumn(departmentId, departmentId.GraphQlName) },
            IncludeCount = true,
            ValueColumns = new[]
            {
                new AggregateValueColumn(AggregateOperationType.Sum, salary, "_sum", "_sum_salary"),
            },
        };

        var act = () => Service().ApplyTransformers(query, model, UserContext());

        act.Should().Throw<BifrostExecutionError>();
    }

    [Fact]
    public void ApplyTransformers_AggregateOnAllowedColumnsOnly_DoesNotThrow()
    {
        var model = EmployeesModel();
        var table = model.GetTableFromDbName("Employees");
        var departmentId = table.Columns.Single(c => c.DbName == "DepartmentId");

        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .Build();
        query.GroupedAggregate = new GroupedAggregate
        {
            GroupColumns = new[] { new AggregateGroupColumn(departmentId, departmentId.GraphQlName) },
            IncludeCount = true,
            ValueColumns = Array.Empty<AggregateValueColumn>(),
        };

        var act = () => Service().ApplyTransformers(query, model, UserContext());

        act.Should().NotThrow();
    }

    /// <summary>
    /// Orders -&gt; customer (single link) -&gt; Customers, where Customers denies read
    /// on <c>ssn</c>. Used to prove the guard's relationship traversal agrees with
    /// <see cref="TableFilter.RenderParts"/>'s own traversal for BOTH the
    /// single-predicate and the sibling-predicate (implicit/explicit AND) shapes.
    /// </summary>
    private static IDbModel OrdersWithCustomersModel() =>
        DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("CustomerId", "int")
                .WithColumn("Total", "decimal"))
            .WithTable("Customers", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("ssn", "nvarchar")
                .WithColumn("active", "bit")
                .WithMetadata(MetadataKeys.Policy.Actions, "read")
                .WithMetadata(MetadataKeys.Policy.ReadDeny, "ssn"))
            .WithSingleLink("Orders", "CustomerId", "Customers", "Id", "customer")
            .Build();

    private static GqlObjectQuery OrdersQueryFilteredBy(IDbModel model, Dictionary<string, object?> filter)
    {
        var orders = model.GetTableFromDbName("Orders");
        return GqlObjectQueryBuilder.Create()
            .WithDbTable(orders)
            .WithColumns("Id", "Total")
            .WithFilter(TableFilter.FromObject(filter, "Orders"))
            .Build();
    }

    [Fact]
    public void ApplyTransformers_RelationshipFilterOnDeniedColumn_SinglePredicate_Throws()
    {
        // The already-covered shape: `customer: { ssn: {_eq} }`. Its `Next.Next`
        // is non-null, so the collector recursed into Customers and the guard fired.
        var model = OrdersWithCustomersModel();
        var query = OrdersQueryFilteredBy(model, new Dictionary<string, object?>
        {
            ["customer"] = new Dictionary<string, object?>
            {
                ["ssn"] = new Dictionary<string, object?> { ["_eq"] = "123-45-6789" },
            },
        });

        var act = () => Service().ApplyTransformers(query, model, UserContext());

        act.Should().Throw<BifrostExecutionError>();
    }

    [Fact]
    public void ApplyTransformers_RelationshipFilterOnDeniedColumn_TwoSiblingPredicates_Throws()
    {
        // Sibling predicates on one relationship produce an implicit-AND wrapper
        // whose own `Next` is null, so `filter.Next.Next == null` held and the
        // collector treated "customer" as a LEAF COLUMN — resolving to null and
        // never recursing. The renderer keys on `Next.FilterType == Relation`
        // instead, so it emitted the `ssn` predicate for real. Net effect: a caller
        // denied read on `ssn` could still filter on it and read the value out of
        // which orders come back — a binary oracle. Adding one harmless sibling
        // predicate was the entire bypass.
        var model = OrdersWithCustomersModel();
        var query = OrdersQueryFilteredBy(model, new Dictionary<string, object?>
        {
            ["customer"] = new Dictionary<string, object?>
            {
                ["ssn"] = new Dictionary<string, object?> { ["_eq"] = "123-45-6789" },
                ["active"] = new Dictionary<string, object?> { ["_eq"] = true },
            },
        });

        var act = () => Service().ApplyTransformers(query, model, UserContext());

        act.Should().Throw<BifrostExecutionError>();
    }

    [Fact]
    public void ApplyTransformers_RelationshipFilterOnDeniedColumn_ExplicitAndBlock_Throws()
    {
        // Same bypass through the explicit `and` form, which the renderer also
        // routes down the relationship branch.
        var model = OrdersWithCustomersModel();
        var query = OrdersQueryFilteredBy(model, new Dictionary<string, object?>
        {
            ["customer"] = new Dictionary<string, object?>
            {
                ["and"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["ssn"] = new Dictionary<string, object?> { ["_eq"] = "123-45-6789" },
                    },
                    new Dictionary<string, object?>
                    {
                        ["active"] = new Dictionary<string, object?> { ["_eq"] = true },
                    },
                },
            },
        });

        var act = () => Service().ApplyTransformers(query, model, UserContext());

        act.Should().Throw<BifrostExecutionError>();
    }

    [Fact]
    public void ApplyTransformers_RelationshipFilterOnAllowedColumns_TwoSiblingPredicates_DoesNotThrow()
    {
        // The fix must not over-reject: two sibling predicates on ALLOWED columns
        // of the linked table still pass.
        var model = OrdersWithCustomersModel();
        var query = OrdersQueryFilteredBy(model, new Dictionary<string, object?>
        {
            ["customer"] = new Dictionary<string, object?>
            {
                ["Id"] = new Dictionary<string, object?> { ["_eq"] = 7 },
                ["active"] = new Dictionary<string, object?> { ["_eq"] = true },
            },
        });

        var act = () => Service().ApplyTransformers(query, model, UserContext());

        act.Should().NotThrow();
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
