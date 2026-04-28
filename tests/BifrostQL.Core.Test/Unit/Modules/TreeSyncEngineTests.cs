using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel.TestFixtures;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

public class TreeSyncEngineTests
{
    #region Insert Tests

    [Fact]
    public void ComputeOperations_NewRootRecord_InfersInsert()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var engine = new TreeSyncEngine(model);
        var table = model.GetTableFromDbName("Users");

        var submitted = new Dictionary<string, object?>
        {
            ["Name"] = "Alice",
            ["Email"] = "alice@example.com"
        };

        var ops = engine.ComputeOperations(table, submitted, existing: null);

        Assert.Single(ops);
        Assert.Equal(TreeSyncOperationType.Insert, ops[0].OperationType);
        Assert.Equal(table, ops[0].Table);
        Assert.Equal("Alice", ops[0].Data["Name"]);
        Assert.Equal("alice@example.com", ops[0].Data["Email"]);
        Assert.Equal(0, ops[0].Depth);
    }

    [Fact]
    public void ComputeOperations_NewRootWithChildren_InfersInsertsInOrder()
    {
        var model = StandardTestFixtures.UsersWithOrders();
        var engine = new TreeSyncEngine(model);
        var usersTable = model.GetTableFromDbName("Users");

        var submitted = new Dictionary<string, object?>
        {
            ["Name"] = "Alice",
            ["orders"] = new List<Dictionary<string, object?>>
            {
                new() { ["Total"] = 50.0m, ["Status"] = "pending" },
                new() { ["Total"] = 100.0m, ["Status"] = "shipped" },
            }
        };

        var ops = engine.ComputeOperations(usersTable, submitted, existing: null);

        Assert.Equal(3, ops.Count);

        Assert.Equal(TreeSyncOperationType.Insert, ops[0].OperationType);
        Assert.Equal("Users", ops[0].Table.DbName);
        Assert.Equal(0, ops[0].Depth);

        Assert.Equal(TreeSyncOperationType.Insert, ops[1].OperationType);
        Assert.Equal("Orders", ops[1].Table.DbName);
        Assert.Equal(1, ops[1].Depth);

        Assert.Equal(TreeSyncOperationType.Insert, ops[2].OperationType);
        Assert.Equal("Orders", ops[2].Table.DbName);
        Assert.Equal(1, ops[2].Depth);
    }

    [Fact]
    public void ComputeOperations_ChildInsert_HasForeignKeyAssignment()
    {
        var model = StandardTestFixtures.UsersWithOrders();
        var engine = new TreeSyncEngine(model);
        var usersTable = model.GetTableFromDbName("Users");

        var submitted = new Dictionary<string, object?>
        {
            ["Name"] = "Alice",
            ["orders"] = new List<Dictionary<string, object?>>
            {
                new() { ["Total"] = 50.0m, ["Status"] = "pending" },
            }
        };

        var ops = engine.ComputeOperations(usersTable, submitted, existing: null);

        var childInsert = ops.First(op => op.Table.DbName == "Orders");
        Assert.Contains("UserId", childInsert.ForeignKeyAssignments.Keys);
        Assert.Equal("Users", childInsert.ForeignKeyAssignments["UserId"]);
    }

    [Fact]
    public void ComputeOperations_NoPrimaryKey_InfersInsert()
    {
        var model = StandardTestFixtures.UsersWithOrders();
        var engine = new TreeSyncEngine(model);
        var usersTable = model.GetTableFromDbName("Users");

        var submitted = new Dictionary<string, object?>
        {
            ["Name"] = "Bob",
            ["Email"] = "bob@example.com"
        };

        var ops = engine.ComputeOperations(usersTable, submitted, existing: null);

        Assert.Single(ops);
        Assert.Equal(TreeSyncOperationType.Insert, ops[0].OperationType);
    }

    #endregion

    #region Update Tests

    [Fact]
    public void ComputeOperations_ExistingRecordChanged_InfersUpdate()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var engine = new TreeSyncEngine(model);
        var table = model.GetTableFromDbName("Users");

        var submitted = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            ["Name"] = "Alice Updated",
            ["Email"] = "alice@example.com"
        };
        var existing = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            ["Name"] = "Alice",
            ["Email"] = "alice@example.com"
        };

        var ops = engine.ComputeOperations(table, submitted, existing);

        Assert.Single(ops);
        Assert.Equal(TreeSyncOperationType.Update, ops[0].OperationType);
        Assert.Equal("Alice Updated", ops[0].Data["Name"]);
        Assert.Contains("Id", ops[0].Data.Keys);
    }

    [Fact]
    public void ComputeOperations_ExistingRecordUnchanged_NoOperations()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var engine = new TreeSyncEngine(model);
        var table = model.GetTableFromDbName("Users");

        var submitted = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            ["Name"] = "Alice",
            ["Email"] = "alice@example.com"
        };
        var existing = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            ["Name"] = "Alice",
            ["Email"] = "alice@example.com"
        };

        var ops = engine.ComputeOperations(table, submitted, existing);

        Assert.Empty(ops);
    }

    [Fact]
    public void ComputeOperations_UpdateIncludesPrimaryKeyInData()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var engine = new TreeSyncEngine(model);
        var table = model.GetTableFromDbName("Users");

        var submitted = new Dictionary<string, object?>
        {
            ["Id"] = 5,
            ["Name"] = "Changed"
        };
        var existing = new Dictionary<string, object?>
        {
            ["Id"] = 5,
            ["Name"] = "Original"
        };

        var ops = engine.ComputeOperations(table, submitted, existing);

        Assert.Single(ops);
        Assert.Equal(5, ops[0].Data["Id"]);
        Assert.Equal("Changed", ops[0].Data["Name"]);
    }

    #endregion

    #region Delete Tests

    [Fact]
    public void ComputeOperations_OrphanChild_InfersDelete()
    {
        var model = StandardTestFixtures.UsersWithOrders();
        var engine = new TreeSyncEngine(model);
        var usersTable = model.GetTableFromDbName("Users");

        var submitted = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            ["Name"] = "Alice",
            ["orders"] = new List<Dictionary<string, object?>>()
        };
        var existing = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            ["Name"] = "Alice",
            ["orders"] = new List<Dictionary<string, object?>>
            {
                new() { ["Id"] = 10, ["UserId"] = 1, ["Total"] = 50.0m, ["Status"] = "pending" },
            }
        };

        var ops = engine.ComputeOperations(usersTable, submitted, existing);

        Assert.Single(ops);
        Assert.Equal(TreeSyncOperationType.Delete, ops[0].OperationType);
        Assert.Equal("Orders", ops[0].Table.DbName);
        Assert.Equal(10, ops[0].Data["Id"]);
    }

    [Fact]
    public void ComputeOperations_DeleteOrphansDisabled_NoDeleteGenerated()
    {
        var model = StandardTestFixtures.UsersWithOrders();
        var options = new TreeSyncOptions { DeleteOrphans = false };
        var engine = new TreeSyncEngine(model, options);
        var usersTable = model.GetTableFromDbName("Users");

        var submitted = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            ["Name"] = "Alice",
            ["orders"] = new List<Dictionary<string, object?>>()
        };
        var existing = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            ["Name"] = "Alice",
            ["orders"] = new List<Dictionary<string, object?>>
            {
                new() { ["Id"] = 10, ["UserId"] = 1, ["Total"] = 50.0m, ["Status"] = "pending" },
            }
        };

        var ops = engine.ComputeOperations(usersTable, submitted, existing);

        Assert.Empty(ops);
    }

    #endregion

    #region Mixed Operations Tests

    [Fact]
    public void ComputeOperations_MixedChildOperations_InsertsUpdatesDeletes()
    {
        var model = StandardTestFixtures.UsersWithOrders();
        var engine = new TreeSyncEngine(model);
        var usersTable = model.GetTableFromDbName("Users");

        var submitted = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            ["Name"] = "Alice",
            ["orders"] = new List<Dictionary<string, object?>>
            {
                new() { ["Id"] = 10, ["Total"] = 75.0m, ["Status"] = "updated" },
                new() { ["Total"] = 200.0m, ["Status"] = "new" },
            }
        };
        var existing = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            ["Name"] = "Alice",
            ["orders"] = new List<Dictionary<string, object?>>
            {
                new() { ["Id"] = 10, ["UserId"] = 1, ["Total"] = 50.0m, ["Status"] = "pending" },
                new() { ["Id"] = 20, ["UserId"] = 1, ["Total"] = 100.0m, ["Status"] = "shipped" },
            }
        };

        var ops = engine.ComputeOperations(usersTable, submitted, existing);

        var inserts = ops.Where(op => op.OperationType == TreeSyncOperationType.Insert).ToList();
        var updates = ops.Where(op => op.OperationType == TreeSyncOperationType.Update).ToList();
        var deletes = ops.Where(op => op.OperationType == TreeSyncOperationType.Delete).ToList();

        Assert.Single(inserts);
        Assert.Single(updates);
        Assert.Single(deletes);

        Assert.Equal("new", inserts[0].Data["Status"]);
        Assert.Equal(10, updates[0].Data["Id"]);
        Assert.Equal(20, deletes[0].Data["Id"]);
    }

    #endregion

    #region Operation Ordering Tests

    [Fact]
    public void ComputeOperations_OrdersInsertBeforeUpdate()
    {
        var model = StandardTestFixtures.UsersWithOrders();
        var engine = new TreeSyncEngine(model);
        var usersTable = model.GetTableFromDbName("Users");

        var submitted = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            ["Name"] = "Updated",
            ["orders"] = new List<Dictionary<string, object?>>
            {
                new() { ["Total"] = 200.0m, ["Status"] = "new" },
            }
        };
        var existing = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            ["Name"] = "Original",
            ["orders"] = new List<Dictionary<string, object?>>()
        };

        var ops = engine.ComputeOperations(usersTable, submitted, existing);

        var insertIdx = ops.ToList().FindIndex(op => op.OperationType == TreeSyncOperationType.Insert);
        var updateIdx = ops.ToList().FindIndex(op => op.OperationType == TreeSyncOperationType.Update);

        Assert.True(insertIdx < updateIdx, "Inserts should come before updates");
    }

    [Fact]
    public void ComputeOperations_OrdersParentInsertBeforeChildInsert()
    {
        var model = StandardTestFixtures.UsersWithOrders();
        var engine = new TreeSyncEngine(model);
        var usersTable = model.GetTableFromDbName("Users");

        var submitted = new Dictionary<string, object?>
        {
            ["Name"] = "NewUser",
            ["orders"] = new List<Dictionary<string, object?>>
            {
                new() { ["Total"] = 50.0m, ["Status"] = "pending" },
            }
        };

        var ops = engine.ComputeOperations(usersTable, submitted, existing: null);

        var parentInsert = ops.First(op => op.Table.DbName == "Users");
        var childInsert = ops.First(op => op.Table.DbName == "Orders");

        var parentIdx = ops.ToList().IndexOf(parentInsert);
        var childIdx = ops.ToList().IndexOf(childInsert);

        Assert.True(parentIdx < childIdx, "Parent insert should come before child insert");
    }

    [Fact]
    public void ComputeOperations_OrdersChildDeleteBeforeParentDelete()
    {
        var model = StandardTestFixtures.CompanyHierarchy();
        var engine = new TreeSyncEngine(model);
        var companiesTable = model.GetTableFromDbName("Companies");

        var submitted = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            ["Name"] = "Acme",
            ["departments"] = new List<Dictionary<string, object?>>()
        };
        var existing = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            ["Name"] = "Acme",
            ["departments"] = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["Id"] = 10, ["CompanyId"] = 1, ["Name"] = "Engineering",
                    ["employees"] = new List<Dictionary<string, object?>>
                    {
                        new() { ["Id"] = 100, ["DepartmentId"] = 10, ["Name"] = "Bob" }
                    }
                }
            }
        };

        var ops = engine.ComputeOperations(companiesTable, submitted, existing);

        var deletes = ops.Where(op => op.OperationType == TreeSyncOperationType.Delete).ToList();
        Assert.Equal(2, deletes.Count);

        var employeeDelete = deletes.First(op => op.Table.DbName == "Employees");
        var deptDelete = deletes.First(op => op.Table.DbName == "Departments");

        var empIdx = ops.ToList().IndexOf(employeeDelete);
        var deptIdx = ops.ToList().IndexOf(deptDelete);

        Assert.True(empIdx < deptIdx, "Child delete should come before parent delete");
    }

    #endregion

    #region Depth Limit Tests

    [Fact]
    public void ComputeOperations_RespectsMaxDepth()
    {
        var model = StandardTestFixtures.CompanyHierarchy();
        var options = new TreeSyncOptions { MaxDepth = 2 };
        var engine = new TreeSyncEngine(model, options);
        var companiesTable = model.GetTableFromDbName("Companies");

        var submitted = new Dictionary<string, object?>
        {
            ["Name"] = "Acme",
            ["departments"] = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["Name"] = "Engineering",
                    ["employees"] = new List<Dictionary<string, object?>>
                    {
                        new() { ["Name"] = "Bob" }
                    }
                }
            }
        };

        var ops = engine.ComputeOperations(companiesTable, submitted, existing: null);

        Assert.DoesNotContain(ops, op => op.Table.DbName == "Employees");
        Assert.Equal(2, ops.Count);
    }

    [Fact]
    public void ComputeOperations_MaxDepthOne_OnlyRootProcessed()
    {
        var model = StandardTestFixtures.UsersWithOrders();
        var options = new TreeSyncOptions { MaxDepth = 1 };
        var engine = new TreeSyncEngine(model, options);
        var usersTable = model.GetTableFromDbName("Users");

        var submitted = new Dictionary<string, object?>
        {
            ["Name"] = "Alice",
            ["orders"] = new List<Dictionary<string, object?>>
            {
                new() { ["Total"] = 50.0m },
            }
        };

        var ops = engine.ComputeOperations(usersTable, submitted, existing: null);

        Assert.Single(ops);
        Assert.Equal("Users", ops[0].Table.DbName);
    }

    [Fact]
    public void Constructor_MaxDepthZero_Throws()
    {
        var model = StandardTestFixtures.SimpleUsers();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TreeSyncEngine(model, new TreeSyncOptions { MaxDepth = 0 }));
    }

    [Fact]
    public void Constructor_NullModel_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TreeSyncEngine(null!));
    }

    #endregion

    #region Three-Level Hierarchy Tests

    [Fact]
    public void ComputeOperations_ThreeLevelInsert_AllLevelsInOrder()
    {
        var model = StandardTestFixtures.CompanyHierarchy();
        var engine = new TreeSyncEngine(model);
        var companiesTable = model.GetTableFromDbName("Companies");

        var submitted = new Dictionary<string, object?>
        {
            ["Name"] = "Acme",
            ["departments"] = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["Name"] = "Engineering",
                    ["employees"] = new List<Dictionary<string, object?>>
                    {
                        new() { ["Name"] = "Bob", ["Salary"] = 90000m },
                        new() { ["Name"] = "Carol", ["Salary"] = 95000m },
                    }
                }
            }
        };

        var ops = engine.ComputeOperations(companiesTable, submitted, existing: null);

        Assert.Equal(4, ops.Count);
        Assert.All(ops, op => Assert.Equal(TreeSyncOperationType.Insert, op.OperationType));

        Assert.Equal("Companies", ops[0].Table.DbName);
        Assert.Equal(0, ops[0].Depth);

        Assert.Equal("Departments", ops[1].Table.DbName);
        Assert.Equal(1, ops[1].Depth);

        Assert.Equal("Employees", ops[2].Table.DbName);
        Assert.Equal(2, ops[2].Depth);

        Assert.Equal("Employees", ops[3].Table.DbName);
        Assert.Equal(2, ops[3].Depth);
    }

    [Fact]
    public void ComputeOperations_ThreeLevelForeignKeyAssignments()
    {
        var model = StandardTestFixtures.CompanyHierarchy();
        var engine = new TreeSyncEngine(model);
        var companiesTable = model.GetTableFromDbName("Companies");

        var submitted = new Dictionary<string, object?>
        {
            ["Name"] = "Acme",
            ["departments"] = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["Name"] = "Engineering",
                    ["employees"] = new List<Dictionary<string, object?>>
                    {
                        new() { ["Name"] = "Bob" },
                    }
                }
            }
        };

        var ops = engine.ComputeOperations(companiesTable, submitted, existing: null);

        var deptInsert = ops.First(op => op.Table.DbName == "Departments");
        Assert.Contains("CompanyId", deptInsert.ForeignKeyAssignments.Keys);
        Assert.Equal("Companies", deptInsert.ForeignKeyAssignments["CompanyId"]);

        var empInsert = ops.First(op => op.Table.DbName == "Employees");
        Assert.Contains("DepartmentId", empInsert.ForeignKeyAssignments.Keys);
        Assert.Equal("Departments", empInsert.ForeignKeyAssignments["DepartmentId"]);
    }

    #endregion

    #region Edge Case Tests

    [Fact]
    public void ComputeOperations_EmptySubmittedTree_InsertsEmptyRecord()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var engine = new TreeSyncEngine(model);
        var table = model.GetTableFromDbName("Users");

        var submitted = new Dictionary<string, object?>();

        var ops = engine.ComputeOperations(table, submitted, existing: null);

        Assert.Single(ops);
        Assert.Equal(TreeSyncOperationType.Insert, ops[0].OperationType);
        Assert.Empty(ops[0].Data);
    }

    [Fact]
    public void ComputeOperations_IgnoresUnknownColumns()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var engine = new TreeSyncEngine(model);
        var table = model.GetTableFromDbName("Users");

        var submitted = new Dictionary<string, object?>
        {
            ["Name"] = "Alice",
            ["UnknownField"] = "should be ignored"
        };

        var ops = engine.ComputeOperations(table, submitted, existing: null);

        Assert.Single(ops);
        Assert.DoesNotContain("UnknownField", ops[0].Data.Keys);
        Assert.Contains("Name", ops[0].Data.Keys);
    }

    [Fact]
    public void ComputeOperations_NullChildCollection_SkipsChildren()
    {
        var model = StandardTestFixtures.UsersWithOrders();
        var engine = new TreeSyncEngine(model);
        var usersTable = model.GetTableFromDbName("Users");

        var submitted = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            ["Name"] = "Alice Updated"
        };
        var existing = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            ["Name"] = "Alice",
            ["orders"] = new List<Dictionary<string, object?>>
            {
                new() { ["Id"] = 10, ["Total"] = 50.0m },
            }
        };

        var ops = engine.ComputeOperations(usersTable, submitted, existing);

        Assert.Single(ops);
        Assert.Equal(TreeSyncOperationType.Update, ops[0].OperationType);
        Assert.Equal("Users", ops[0].Table.DbName);
    }

    [Fact]
    public void ComputeOperations_ChildWithPrimaryKeyMatchesExisting_InfersUpdate()
    {
        var model = StandardTestFixtures.UsersWithOrders();
        var engine = new TreeSyncEngine(model);
        var usersTable = model.GetTableFromDbName("Users");

        var submitted = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            ["Name"] = "Alice",
            ["orders"] = new List<Dictionary<string, object?>>
            {
                new() { ["Id"] = 10, ["Total"] = 99.0m, ["Status"] = "changed" },
            }
        };
        var existing = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            ["Name"] = "Alice",
            ["orders"] = new List<Dictionary<string, object?>>
            {
                new() { ["Id"] = 10, ["Total"] = 50.0m, ["Status"] = "pending" },
            }
        };

        var ops = engine.ComputeOperations(usersTable, submitted, existing);

        Assert.Single(ops);
        Assert.Equal(TreeSyncOperationType.Update, ops[0].OperationType);
        Assert.Equal("Orders", ops[0].Table.DbName);
        Assert.Equal(10, ops[0].Data["Id"]);
        Assert.Equal(99.0m, ops[0].Data["Total"]);
    }

    [Fact]
    public void ComputeOperations_ChildWithPrimaryKeyNoMatch_InfersInsert()
    {
        var model = StandardTestFixtures.UsersWithOrders();
        var engine = new TreeSyncEngine(model);
        var usersTable = model.GetTableFromDbName("Users");

        var submitted = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            ["Name"] = "Alice",
            ["orders"] = new List<Dictionary<string, object?>>
            {
                new() { ["Id"] = 99, ["Total"] = 50.0m, ["Status"] = "pending" },
            }
        };
        var existing = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            ["Name"] = "Alice",
            ["orders"] = new List<Dictionary<string, object?>>()
        };

        var ops = engine.ComputeOperations(usersTable, submitted, existing);

        Assert.Single(ops);
        Assert.Equal(TreeSyncOperationType.Insert, ops[0].OperationType);
        Assert.Equal("Orders", ops[0].Table.DbName);
    }

    [Fact]
    public void ComputeOperations_NullPrimaryKeyValue_InfersInsert()
    {
        var model = StandardTestFixtures.UsersWithOrders();
        var engine = new TreeSyncEngine(model);
        var usersTable = model.GetTableFromDbName("Users");

        var submitted = new Dictionary<string, object?>
        {
            ["Id"] = null,
            ["Name"] = "NewUser"
        };

        var ops = engine.ComputeOperations(usersTable, submitted, existing: null);

        Assert.Single(ops);
        Assert.Equal(TreeSyncOperationType.Insert, ops[0].OperationType);
    }

    [Fact]
    public void ComputeOperations_CascadeDeleteForNestedOrphans()
    {
        var model = StandardTestFixtures.CompanyHierarchy();
        var engine = new TreeSyncEngine(model);
        var companiesTable = model.GetTableFromDbName("Companies");

        var submitted = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            ["Name"] = "Acme",
            ["departments"] = new List<Dictionary<string, object?>>()
        };
        var existing = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            ["Name"] = "Acme",
            ["departments"] = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["Id"] = 10, ["CompanyId"] = 1, ["Name"] = "Engineering",
                    ["employees"] = new List<Dictionary<string, object?>>
                    {
                        new() { ["Id"] = 100, ["DepartmentId"] = 10, ["Name"] = "Bob" },
                        new() { ["Id"] = 101, ["DepartmentId"] = 10, ["Name"] = "Carol" },
                    }
                }
            }
        };

        var ops = engine.ComputeOperations(companiesTable, submitted, existing);

        var deletes = ops.Where(op => op.OperationType == TreeSyncOperationType.Delete).ToList();
        Assert.Equal(3, deletes.Count);

        var employeeDeletes = deletes.Where(op => op.Table.DbName == "Employees").ToList();
        var deptDeletes = deletes.Where(op => op.Table.DbName == "Departments").ToList();
        Assert.Equal(2, employeeDeletes.Count);
        Assert.Single(deptDeletes);
    }

    [Fact]
    public void ComputeOperations_DefaultOptions_MaxDepthThree()
    {
        var options = new TreeSyncOptions();

        Assert.Equal(3, options.MaxDepth);
        Assert.True(options.DeleteOrphans);
    }

    #endregion
}
