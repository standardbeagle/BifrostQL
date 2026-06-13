using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel.TestFixtures;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

public class AuditMutationTransformerTests
{
    #region Insert Tests

    [Fact]
    public void Insert_SetsCreatedOnTimestamp()
    {
        var model = CreateAuditModel();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Alice" };

        transformer.Transform(table, MutationType.Insert, data, Context(model, EmptyUserContext()));

        Assert.True(data.ContainsKey("created_at"));
        Assert.IsType<DateTime>(data["created_at"]);
        var timestamp = (DateTime)data["created_at"]!;
        Assert.Equal(DateTimeKind.Utc, timestamp.Kind);
    }

    [Fact]
    public void Insert_SetsUpdatedOnTimestamp()
    {
        var model = CreateAuditModel();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Alice" };

        transformer.Transform(table, MutationType.Insert, data, Context(model, EmptyUserContext()));

        Assert.True(data.ContainsKey("updated_at"));
        Assert.IsType<DateTime>(data["updated_at"]);
    }

    [Fact]
    public void Insert_SetsCreatedByFromUserContext()
    {
        var model = CreateAuditModel();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Alice" };

        transformer.Transform(table, MutationType.Insert, data, Context(model, UserContextWithId("user-42")));

        Assert.Equal("user-42", data["created_by_user_id"]);
    }

    [Fact]
    public void Insert_SetsUpdatedByFromUserContext()
    {
        var model = CreateAuditModel();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Alice" };

        transformer.Transform(table, MutationType.Insert, data, Context(model, UserContextWithId("user-42")));

        Assert.Equal("user-42", data["updated_by_user_id"]);
    }

    [Fact]
    public void Insert_OverwritesClientProvidedTimestamp()
    {
        var model = CreateAuditModel();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var spoofedTime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var data = new Dictionary<string, object?>
        {
            ["Name"] = "Alice",
            ["created_at"] = spoofedTime
        };

        transformer.Transform(table, MutationType.Insert, data, Context(model, EmptyUserContext()));

        var actual = (DateTime)data["created_at"]!;
        Assert.NotEqual(spoofedTime, actual);
        Assert.True(actual > spoofedTime);
    }

    [Fact]
    public void Insert_OverwritesClientProvidedUserColumn()
    {
        var model = CreateAuditModel();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?>
        {
            ["Name"] = "Alice",
            ["created_by_user_id"] = "spoofed-user"
        };

        transformer.Transform(table, MutationType.Insert, data, Context(model, UserContextWithId("real-user")));

        Assert.Equal("real-user", data["created_by_user_id"]);
    }

    [Fact]
    public void Insert_DoesNotSetUserColumnsWhenNoAuditKey()
    {
        var model = CreateAuditModelWithoutAuditKey();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Alice" };

        transformer.Transform(table, MutationType.Insert, data, Context(model, UserContextWithId("user-42")));

        Assert.False(data.ContainsKey("created_by_user_id"));
        Assert.False(data.ContainsKey("updated_by_user_id"));
    }

    [Fact]
    public void Insert_SetsTimestampsEvenWithoutAuditKey()
    {
        var model = CreateAuditModelWithoutAuditKey();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Alice" };

        transformer.Transform(table, MutationType.Insert, data, Context(model, EmptyUserContext()));

        Assert.True(data.ContainsKey("created_at"));
        Assert.True(data.ContainsKey("updated_at"));
    }

    [Fact]
    public void Insert_DoesNotSetDeletedColumns()
    {
        var model = CreateFullAuditModel();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Alice" };

        transformer.Transform(table, MutationType.Insert, data, Context(model, UserContextWithId("user-42")));

        Assert.False(data.ContainsKey("deleted_at"));
        Assert.False(data.ContainsKey("deleted_by_user_id"));
    }

    [Fact]
    public void Insert_ReturnsNoErrors()
    {
        var model = CreateAuditModel();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Alice" };

        var result = transformer.Transform(table, MutationType.Insert, data, Context(model, EmptyUserContext()));

        Assert.Empty(result.Errors);
        Assert.Equal(MutationType.Insert, result.MutationType);
    }

    [Fact]
    public void Insert_HandlesNullUserContextValueGracefully()
    {
        var model = CreateAuditModel();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Alice" };
        var userContext = new Dictionary<string, object?> { ["id"] = null };

        transformer.Transform(table, MutationType.Insert, data, Context(model, userContext));

        Assert.Null(data["created_by_user_id"]);
    }

    [Fact]
    public void Insert_HandlesMissingAuditKeyInUserContext()
    {
        var model = CreateAuditModel();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Alice" };
        var userContext = new Dictionary<string, object?> { ["other_key"] = "value" };

        transformer.Transform(table, MutationType.Insert, data, Context(model, userContext));

        Assert.Null(data["created_by_user_id"]);
    }

    #endregion

    #region Update Tests

    [Fact]
    public void Update_SetsUpdatedOnTimestamp()
    {
        var model = CreateAuditModel();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Bob" };

        transformer.Transform(table, MutationType.Update, data, Context(model, EmptyUserContext()));

        Assert.True(data.ContainsKey("updated_at"));
        Assert.IsType<DateTime>(data["updated_at"]);
    }

    [Fact]
    public void Update_SetsUpdatedByFromUserContext()
    {
        var model = CreateAuditModel();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Bob" };

        transformer.Transform(table, MutationType.Update, data, Context(model, UserContextWithId("user-99")));

        Assert.Equal("user-99", data["updated_by_user_id"]);
    }

    [Fact]
    public void Update_DoesNotSetCreatedColumns()
    {
        var model = CreateAuditModel();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Bob" };

        transformer.Transform(table, MutationType.Update, data, Context(model, UserContextWithId("user-99")));

        Assert.False(data.ContainsKey("created_at"));
        Assert.False(data.ContainsKey("created_by_user_id"));
    }

    [Fact]
    public void Update_DoesNotSetDeletedColumns()
    {
        var model = CreateFullAuditModel();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Bob" };

        transformer.Transform(table, MutationType.Update, data, Context(model, UserContextWithId("user-99")));

        Assert.False(data.ContainsKey("deleted_at"));
        Assert.False(data.ContainsKey("deleted_by_user_id"));
    }

    [Fact]
    public void Update_OverwritesClientProvidedTimestamp()
    {
        var model = CreateAuditModel();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var spoofedTime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var data = new Dictionary<string, object?>
        {
            ["Name"] = "Bob",
            ["updated_at"] = spoofedTime
        };

        transformer.Transform(table, MutationType.Update, data, Context(model, EmptyUserContext()));

        var actual = (DateTime)data["updated_at"]!;
        Assert.NotEqual(spoofedTime, actual);
    }

    [Fact]
    public void Update_ReturnsNoErrors()
    {
        var model = CreateAuditModel();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Bob" };

        var result = transformer.Transform(table, MutationType.Update, data, Context(model, EmptyUserContext()));

        Assert.Empty(result.Errors);
        Assert.Equal(MutationType.Update, result.MutationType);
    }

    #endregion

    #region Delete Tests

    [Fact]
    public void Delete_SetsUpdatedOnTimestamp()
    {
        var model = CreateFullAuditModel();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Id"] = 1 };

        transformer.Transform(table, MutationType.Delete, data, Context(model, EmptyUserContext()));

        Assert.True(data.ContainsKey("updated_at"));
        Assert.IsType<DateTime>(data["updated_at"]);
    }

    [Fact]
    public void Delete_SetsDeletedOnTimestamp()
    {
        var model = CreateFullAuditModel();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Id"] = 1 };

        transformer.Transform(table, MutationType.Delete, data, Context(model, EmptyUserContext()));

        Assert.True(data.ContainsKey("deleted_at"));
        Assert.IsType<DateTime>(data["deleted_at"]);
    }

    [Fact]
    public void Delete_SetsUpdatedByFromUserContext()
    {
        var model = CreateFullAuditModel();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Id"] = 1 };

        transformer.Transform(table, MutationType.Delete, data, Context(model, UserContextWithId("admin-1")));

        Assert.Equal("admin-1", data["updated_by_user_id"]);
    }

    [Fact]
    public void Delete_SetsDeletedByFromUserContext()
    {
        var model = CreateFullAuditModel();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Id"] = 1 };

        transformer.Transform(table, MutationType.Delete, data, Context(model, UserContextWithId("admin-1")));

        Assert.Equal("admin-1", data["deleted_by_user_id"]);
    }

    [Fact]
    public void Delete_DoesNotSetCreatedColumns()
    {
        var model = CreateFullAuditModel();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Id"] = 1 };

        transformer.Transform(table, MutationType.Delete, data, Context(model, UserContextWithId("admin-1")));

        Assert.False(data.ContainsKey("created_at"));
        Assert.False(data.ContainsKey("created_by_user_id"));
    }

    [Fact]
    public void Delete_ReturnsNoErrors()
    {
        var model = CreateFullAuditModel();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Id"] = 1 };

        var result = transformer.Transform(table, MutationType.Delete, data, Context(model, EmptyUserContext()));

        Assert.Empty(result.Errors);
        Assert.Equal(MutationType.Delete, result.MutationType);
    }

    #endregion

    #region Table Without Audit Columns

    [Fact]
    public void AppliesTo_NoAuditColumns_ReturnsFalse()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");

        Assert.False(transformer.AppliesTo(table, MutationType.Insert, Context(model, EmptyUserContext())));
        Assert.False(transformer.AppliesTo(table, MutationType.Update, Context(model, EmptyUserContext())));
        Assert.False(transformer.AppliesTo(table, MutationType.Delete, Context(model, EmptyUserContext())));
    }

    [Fact]
    public void AppliesTo_WithAuditColumns_ReturnsTrue()
    {
        var model = CreateAuditModel();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");

        Assert.True(transformer.AppliesTo(table, MutationType.Insert, Context(model, EmptyUserContext())));
        Assert.True(transformer.AppliesTo(table, MutationType.Update, Context(model, EmptyUserContext())));
        Assert.True(transformer.AppliesTo(table, MutationType.Delete, Context(model, EmptyUserContext())));
    }

    [Fact]
    public void Insert_NoAuditColumns_DataUnchanged()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Alice" };

        transformer.Transform(table, MutationType.Insert, data, Context(model, UserContextWithId("user-1")));

        Assert.Single(data);
        Assert.Equal("Alice", data["Name"]);
    }

    [Fact]
    public void Update_NoAuditColumns_DataUnchanged()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Bob" };

        transformer.Transform(table, MutationType.Update, data, Context(model, UserContextWithId("user-1")));

        Assert.Single(data);
        Assert.Equal("Bob", data["Name"]);
    }

    [Fact]
    public void Delete_NoAuditColumns_DataUnchanged()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Id"] = 1 };

        transformer.Transform(table, MutationType.Delete, data, Context(model, UserContextWithId("user-1")));

        Assert.Single(data);
        Assert.Equal(1, data["Id"]);
    }

    #endregion

    #region Module Metadata

    [Fact]
    public void ModuleName_IsAudit()
    {
        Assert.Equal("audit", new AuditMutationTransformer().ModuleName);
    }

    [Fact]
    public void Priority_RunsBeforeSoftDeleteConversion()
    {
        // Audit must run before the soft-delete transformer (priority 100) so it sees
        // the original DELETE intent and stamps deleted-* before DELETE→UPDATE rewrite.
        var priority = new AuditMutationTransformer().Priority;
        Assert.True(priority < 100, "audit must precede soft-delete (100)");
    }

    #endregion

    #region Soft-Delete Pipeline Interaction

    [Fact]
    public void Pipeline_SoftDeleteWithAudit_StampsDeletedColumnsOnDelete()
    {
        // Regression: in the legacy IMutationModule system, audit's Delete() ran on the
        // soft-delete (DELETE→UPDATE) path and stamped deleted-*. The transformer port
        // must preserve that — audit (50) runs before soft-delete (100), so a DELETE on a
        // soft-delete + audit table still stamps deleted_at/deleted_by even though the
        // mutation is ultimately rewritten to an UPDATE.
        var model = CreateSoftDeleteAuditModel();
        var table = model.GetTableFromDbName("Users");
        var pipeline = new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[]
            {
                new SoftDeleteMutationTransformer(),
                new AuditMutationTransformer(),
            },
        };
        var data = new Dictionary<string, object?> { ["Id"] = 1 };

        var result = pipeline.Transform(table, MutationType.Delete, data, Context(model, UserContextWithId("admin-1")));

        // DELETE rewritten to UPDATE by soft-delete...
        Assert.Equal(MutationType.Update, result.MutationType);
        Assert.Empty(result.Errors);
        // ...yet audit's deleted-* stamps survived. deleted_by_user_id is the decisive
        // proof: soft-delete only sets it when soft-delete-by metadata is configured
        // (it isn't here), so this value can only come from audit's DELETE branch.
        Assert.Equal("admin-1", result.Data["deleted_by_user_id"]);
        Assert.NotNull(result.Data["deleted_at"]); // set by both; soft-delete writes last
        Assert.True(result.Data.ContainsKey("updated_at"));
        Assert.Equal("admin-1", result.Data["updated_by_user_id"]);
    }

    #endregion

    #region Timestamp Consistency

    [Fact]
    public void Insert_CreatedOnAndUpdatedOnUseSameTimestamp()
    {
        var model = CreateAuditModel();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Alice" };

        transformer.Transform(table, MutationType.Insert, data, Context(model, EmptyUserContext()));

        var createdAt = (DateTime)data["created_at"]!;
        var updatedAt = (DateTime)data["updated_at"]!;
        Assert.Equal(createdAt, updatedAt);
    }

    [Fact]
    public void Delete_UpdatedOnAndDeletedOnUseSameTimestamp()
    {
        var model = CreateFullAuditModel();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Id"] = 1 };

        transformer.Transform(table, MutationType.Delete, data, Context(model, EmptyUserContext()));

        var updatedAt = (DateTime)data["updated_at"]!;
        var deletedAt = (DateTime)data["deleted_at"]!;
        Assert.Equal(updatedAt, deletedAt);
    }

    #endregion

    #region Column Metadata Verification

    [Fact]
    public void AuditColumns_HaveCorrectPopulateMetadata()
    {
        var model = CreateAuditModel();
        var table = model.GetTableFromDbName("Users");

        var createdAtCol = table.ColumnLookup["created_at"];
        var createdByCol = table.ColumnLookup["created_by_user_id"];
        var updatedAtCol = table.ColumnLookup["updated_at"];
        var updatedByCol = table.ColumnLookup["updated_by_user_id"];

        Assert.True(createdAtCol.CompareMetadata("populate", "created-on"));
        Assert.True(createdByCol.CompareMetadata("populate", "created-by"));
        Assert.True(updatedAtCol.CompareMetadata("populate", "updated-on"));
        Assert.True(updatedByCol.CompareMetadata("populate", "updated-by"));
    }

    #endregion

    #region Helper Methods

    private static MutationTransformContext Context(IDbModel model, IDictionary<string, object?> userContext)
    {
        return new MutationTransformContext { Model = model, UserContext = userContext };
    }

    private static IDbModel CreateAuditModel()
    {
        return DbModelTestFixture.Create()
            .WithModelMetadata("user-audit-key", "id")
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("created_at", "datetime2")
                .WithColumnMetadata("created_at", "populate", "created-on")
                .WithColumn("created_by_user_id", "nvarchar")
                .WithColumnMetadata("created_by_user_id", "populate", "created-by")
                .WithColumn("updated_at", "datetime2")
                .WithColumnMetadata("updated_at", "populate", "updated-on")
                .WithColumn("updated_by_user_id", "nvarchar")
                .WithColumnMetadata("updated_by_user_id", "populate", "updated-by"))
            .Build();
    }

    private static IDbModel CreateAuditModelWithoutAuditKey()
    {
        return DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("created_at", "datetime2")
                .WithColumnMetadata("created_at", "populate", "created-on")
                .WithColumn("created_by_user_id", "nvarchar")
                .WithColumnMetadata("created_by_user_id", "populate", "created-by")
                .WithColumn("updated_at", "datetime2")
                .WithColumnMetadata("updated_at", "populate", "updated-on")
                .WithColumn("updated_by_user_id", "nvarchar")
                .WithColumnMetadata("updated_by_user_id", "populate", "updated-by"))
            .Build();
    }

    private static IDbModel CreateFullAuditModel()
    {
        return DbModelTestFixture.Create()
            .WithModelMetadata("user-audit-key", "id")
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("created_at", "datetime2")
                .WithColumnMetadata("created_at", "populate", "created-on")
                .WithColumn("created_by_user_id", "nvarchar")
                .WithColumnMetadata("created_by_user_id", "populate", "created-by")
                .WithColumn("updated_at", "datetime2")
                .WithColumnMetadata("updated_at", "populate", "updated-on")
                .WithColumn("updated_by_user_id", "nvarchar")
                .WithColumnMetadata("updated_by_user_id", "populate", "updated-by")
                .WithColumn("deleted_at", "datetime2", isNullable: true)
                .WithColumnMetadata("deleted_at", "populate", "deleted-on")
                .WithColumn("deleted_by_user_id", "nvarchar", isNullable: true)
                .WithColumnMetadata("deleted_by_user_id", "populate", "deleted-by"))
            .Build();
    }

    private static IDbModel CreateSoftDeleteAuditModel()
    {
        return DbModelTestFixture.Create()
            .WithModelMetadata("user-audit-key", "id")
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithMetadata("soft-delete", "deleted_at")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("updated_at", "datetime2")
                .WithColumnMetadata("updated_at", "populate", "updated-on")
                .WithColumn("updated_by_user_id", "nvarchar")
                .WithColumnMetadata("updated_by_user_id", "populate", "updated-by")
                .WithColumn("deleted_at", "datetime2", isNullable: true)
                .WithColumnMetadata("deleted_at", "populate", "deleted-on")
                .WithColumn("deleted_by_user_id", "nvarchar", isNullable: true)
                .WithColumnMetadata("deleted_by_user_id", "populate", "deleted-by"))
            .Build();
    }

    private static IDictionary<string, object?> EmptyUserContext()
    {
        return new Dictionary<string, object?>();
    }

    private static IDictionary<string, object?> UserContextWithId(string userId)
    {
        return new Dictionary<string, object?> { ["id"] = userId };
    }

    #endregion
}
