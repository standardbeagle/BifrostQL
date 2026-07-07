using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// A module query argument (e.g. <c>_includeDeleted</c>) is scoped into the shared
/// per-request user context under a table-scoped key. Because the context is shared,
/// a second field over the SAME table must NOT inherit an argument the first field
/// set but the second never sent. The scope must live only for its own node.
/// </summary>
public class QueryTransformerServiceScopeTests
{
    private static IDbModel NotesModel() =>
        DbModelTestFixture.Create()
            .WithTable("Notes", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("content", "nvarchar")
                .WithColumn("deleted_at", "datetime2", isNullable: true)
                .WithMetadata(MetadataKeys.SoftDelete.Column, "deleted_at"))
            .Build();

    private static QueryTransformerService Service() =>
        new(new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[] { new SoftDeleteFilterTransformer() },
        });

    private static GqlObjectQuery NotesNode(IDbTable table, IReadOnlyDictionary<string, object?>? moduleArgs)
    {
        var node = GqlObjectQueryBuilder.Create()
            .WithDbTable(table)
            .WithColumns("Id", "content")
            .Build();
        if (moduleArgs != null)
            node.ModuleQueryArguments = moduleArgs;
        return node;
    }

    [Fact]
    public void ApplyTransformers_SiblingWithoutIncludeDeleted_DoesNotInheritIt()
    {
        var model = NotesModel();
        var notes = model.GetTableFromDbName("Notes");

        // Sibling A sends _includeDeleted; sibling B (same table) sends nothing.
        var siblingA = NotesNode(notes, new Dictionary<string, object?>
        {
            [SoftDeleteModuleApi.IncludeDeletedKey] = true,
        });
        var siblingB = NotesNode(notes, moduleArgs: null);

        var root = GqlObjectQueryBuilder.Create()
            .WithDbTable(notes)
            .WithColumns("Id")
            .WithJoin(j => j.WithName("a").WithFromColumn("Id").WithConnectedColumn("Id").WithConnectedTable(siblingA))
            .WithJoin(j => j.WithName("b").WithFromColumn("Id").WithConnectedColumn("Id").WithConnectedTable(siblingB))
            .Build();

        Service().ApplyTransformers(root, model, new Dictionary<string, object?>());

        // A opted into deleted rows, so no soft-delete filter is applied to it.
        siblingA.Filter.Should().BeNull();

        // B never sent _includeDeleted; it must still get the soft-delete IS NULL
        // filter rather than inheriting A's opt-in.
        siblingB.Filter.Should().NotBeNull();
    }
}
