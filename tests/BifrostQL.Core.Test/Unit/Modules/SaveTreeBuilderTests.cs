using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.Modules;

/// <summary>
/// The explicit-ops graph save builder: every node states its own operation (or gets the
/// generous default — key-present updates, key-absent inserts), NOTHING is inferred from
/// database state, and unlisted children are untouched — proven non-vacuously against the
/// sync engine, which for the same shape WOULD emit orphan deletes.
/// </summary>
public sealed class SaveTreeBuilderTests
{
    private static IDbModel BuildModel()
        => DbModelTestFixture.Create()
            .WithTable("Blogs", t => t.WithPrimaryKey("Id").WithColumn("Name", "nvarchar"))
            .WithTable("Posts", t => t.WithPrimaryKey("Id").WithColumn("BlogId", "int").WithColumn("Title", "nvarchar"))
            .WithMultiLink("Blogs", "Id", "Posts", "BlogId")
            .Build();

    private static Dictionary<string, object?> Node(params (string Key, object? Value)[] entries)
        => entries.ToDictionary(e => e.Key, e => e.Value);

    [Fact]
    public void DefaultOps_KeyPresentUpdates_KeyAbsentInserts()
    {
        var model = BuildModel();
        var ops = new SaveTreeBuilder().BuildOperations(model.GetTableFromDbName("Blogs"),
            Node(("Id", 1), ("Name", "renamed"),
                ("Posts", new List<object?>
                {
                    Node(("Title", "fresh")),
                    Node(("Id", 7), ("Title", "edited")),
                })));

        ops.Should().HaveCount(3);
        ops[0].OperationType.Should().Be(TreeSyncOperationType.Insert);
        ops[0].Table.DbName.Should().Be("Posts");
        ops[1].OperationType.Should().Be(TreeSyncOperationType.Update);
        ops[1].Table.DbName.Should().Be("Blogs");
        ops[2].OperationType.Should().Be(TreeSyncOperationType.Update);
        ops[2].Table.DbName.Should().Be("Posts");
        // The child insert under a known parent got the FK written directly.
        ops[0].Data["BlogId"].Should().Be(1);
    }

    [Fact]
    public void ExplicitDelete_NeedsOnlyTheKey_AndEmitsKeyOnlyData()
    {
        var model = BuildModel();
        var ops = new SaveTreeBuilder().BuildOperations(model.GetTableFromDbName("Posts"),
            Node(("Id", 9), ("_op", "delete"), ("Title", "ignored")));

        var op = ops.Should().ContainSingle().Which;
        op.OperationType.Should().Be(TreeSyncOperationType.Delete);
        op.Data.Should().HaveCount(1);
        op.Data["Id"].Should().Be(9);
    }

    [Fact]
    public void UnlistedChildren_AreUntouched_ProvenAgainstSyncOrphanInference()
    {
        var model = BuildModel();
        var blogs = model.GetTableFromDbName("Blogs");
        var submitted = Node(("Id", 1), ("Name", "kept"),
            ("Posts", new List<object?> { Node(("Id", 7), ("Title", "still here")) }));

        // Non-vacuity: the SYNC engine, given existing state with an extra child, infers
        // an orphan DELETE for the same submitted shape — save must not.
        var existing = Node(("Id", 1), ("Name", "old"),
            ("Posts", new List<object?>
            {
                Node(("Id", 7), ("Title", "old title")),
                Node(("Id", 8), ("Title", "would-be orphan")),
            }));
        var syncOps = new TreeSyncEngine(model).ComputeOperations(blogs, submitted, existing);
        syncOps.Should().Contain(o => o.OperationType == TreeSyncOperationType.Delete,
            "sync infers the orphan delete — proving the two writers genuinely differ");

        var saveOps = new SaveTreeBuilder().BuildOperations(blogs, submitted);
        saveOps.Should().NotContain(o => o.OperationType == TreeSyncOperationType.Delete,
            "save never infers deletes: unlisted children are untouched");
    }

    [Fact]
    public void NewParentWithNewChildren_DefersChildForeignKeys_PerInstance()
    {
        var model = BuildModel();
        var ops = new SaveTreeBuilder().BuildOperations(model.GetTableFromDbName("Blogs"),
            Node(("Name", "new blog"),
                ("Posts", new List<object?> { Node(("Title", "child")) })));

        ops.Should().HaveCount(2);
        var parent = ops[0];
        var child = ops[1];
        parent.Table.DbName.Should().Be("Blogs");
        child.ForeignKeyAssignments.Should().ContainKey("BlogId");
        child.ParentInstanceId.Should().Be(parent.InstanceId,
            "the deferred FK binds to THIS parent instance, not the table's last insert");
    }

    [Fact]
    public void UpdateWithoutKey_AndDeleteWithoutKey_AreCleanErrors()
    {
        var model = BuildModel();
        var builder = new SaveTreeBuilder();
        var posts = model.GetTableFromDbName("Posts");

        var updateAct = () => builder.BuildOperations(posts, Node(("_op", "update"), ("Title", "x")));
        updateAct.Should().Throw<BifrostExecutionError>().WithMessage("*primary key*");

        var deleteAct = () => builder.BuildOperations(posts, Node(("_op", "delete"), ("Title", "x")));
        deleteAct.Should().Throw<BifrostExecutionError>().WithMessage("*primary key*");
    }

    [Fact]
    public void UpdateWithOnlyKeyColumns_IsACleanError()
    {
        var model = BuildModel();
        var act = () => new SaveTreeBuilder().BuildOperations(
            model.GetTableFromDbName("Posts"), Node(("Id", 1), ("_op", "update")));
        act.Should().Throw<BifrostExecutionError>().WithMessage("*non-key column*");
    }

    [Fact]
    public void DepthBeyondMax_IsACleanError_NeverSilentTruncation()
    {
        var model = BuildModel();
        var act = () => new SaveTreeBuilder(new TreeSyncOptions { MaxDepth = 1 }).BuildOperations(
            model.GetTableFromDbName("Blogs"),
            Node(("Id", 1), ("Name", "x"),
                ("Posts", new List<object?> { Node(("Title", "too deep")) })));
        act.Should().Throw<BifrostExecutionError>().WithMessage("*maximum depth*");
    }

    [Fact]
    public void RootDelete_OrdersChildDeletesFirst()
    {
        var model = BuildModel();
        var ops = new SaveTreeBuilder().BuildOperations(model.GetTableFromDbName("Blogs"),
            Node(("Id", 1), ("_op", "delete"),
                ("Posts", new List<object?> { Node(("Id", 7), ("_op", "delete")) })));

        ops.Should().HaveCount(2);
        ops[0].Table.DbName.Should().Be("Posts", "deletes run children-first");
        ops[1].Table.DbName.Should().Be("Blogs");
    }
}
