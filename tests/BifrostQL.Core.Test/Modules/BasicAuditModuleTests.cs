using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel.TestFixtures;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

public class BasicAuditModuleTests
{
    #region Insert Tests

    [Fact]
    public void Insert_SetsCreatedOnTimestamp()
    {
        var model = CreateAuditModel();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Alice" };

        module.Insert(data, table, EmptyUserContext(), model);

        Assert.True(data.ContainsKey("created_at"));
        Assert.IsType<DateTime>(data["created_at"]);
        var timestamp = (DateTime)data["created_at"]!;
        Assert.Equal(DateTimeKind.Utc, timestamp.Kind);
    }

    [Fact]
    public void Insert_SetsUpdatedOnTimestamp()
    {
        var model = CreateAuditModel();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Alice" };

        module.Insert(data, table, EmptyUserContext(), model);

        Assert.True(data.ContainsKey("updated_at"));
        Assert.IsType<DateTime>(data["updated_at"]);
    }

    [Fact]
    public void Insert_SetsCreatedByFromUserContext()
    {
        var model = CreateAuditModel();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Alice" };

        module.Insert(data, table, UserContextWithId("user-42"), model);

        Assert.Equal("user-42", data["created_by_user_id"]);
    }

    [Fact]
    public void Insert_SetsUpdatedByFromUserContext()
    {
        var model = CreateAuditModel();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Alice" };

        module.Insert(data, table, UserContextWithId("user-42"), model);

        Assert.Equal("user-42", data["updated_by_user_id"]);
    }

    [Fact]
    public void Insert_OverwritesClientProvidedTimestamp()
    {
        var model = CreateAuditModel();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var spoofedTime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var data = new Dictionary<string, object?>
        {
            ["Name"] = "Alice",
            ["created_at"] = spoofedTime
        };

        module.Insert(data, table, EmptyUserContext(), model);

        var actual = (DateTime)data["created_at"]!;
        Assert.NotEqual(spoofedTime, actual);
        Assert.True(actual > spoofedTime);
    }

    [Fact]
    public void Insert_OverwritesClientProvidedUserColumn()
    {
        var model = CreateAuditModel();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?>
        {
            ["Name"] = "Alice",
            ["created_by_user_id"] = "spoofed-user"
        };

        module.Insert(data, table, UserContextWithId("real-user"), model);

        Assert.Equal("real-user", data["created_by_user_id"]);
    }

    [Fact]
    public void Insert_DoesNotSetUserColumnsWhenNoAuditKey()
    {
        var model = CreateAuditModelWithoutAuditKey();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Alice" };

        module.Insert(data, table, UserContextWithId("user-42"), model);

        Assert.False(data.ContainsKey("created_by_user_id"));
        Assert.False(data.ContainsKey("updated_by_user_id"));
    }

    [Fact]
    public void Insert_SetsTimestampsEvenWithoutAuditKey()
    {
        var model = CreateAuditModelWithoutAuditKey();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Alice" };

        module.Insert(data, table, EmptyUserContext(), model);

        Assert.True(data.ContainsKey("created_at"));
        Assert.True(data.ContainsKey("updated_at"));
    }

    [Fact]
    public void Insert_DoesNotSetDeletedColumns()
    {
        var model = CreateFullAuditModel();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Alice" };

        module.Insert(data, table, UserContextWithId("user-42"), model);

        Assert.False(data.ContainsKey("deleted_at"));
        Assert.False(data.ContainsKey("deleted_by_user_id"));
    }

    [Fact]
    public void Insert_ReturnsEmptyErrorArray()
    {
        var model = CreateAuditModel();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Alice" };

        var errors = module.Insert(data, table, EmptyUserContext(), model);

        Assert.Empty(errors);
    }

    [Fact]
    public void Insert_HandlesNullUserContextValueGracefully()
    {
        var model = CreateAuditModel();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Alice" };
        var userContext = new Dictionary<string, object?> { ["id"] = null };

        module.Insert(data, table, userContext, model);

        Assert.Null(data["created_by_user_id"]);
    }

    [Fact]
    public void Insert_HandlesMissingAuditKeyInUserContext()
    {
        var model = CreateAuditModel();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Alice" };
        var userContext = new Dictionary<string, object?> { ["other_key"] = "value" };

        module.Insert(data, table, userContext, model);

        Assert.Null(data["created_by_user_id"]);
    }

    #endregion

    #region Update Tests

    [Fact]
    public void Update_SetsUpdatedOnTimestamp()
    {
        var model = CreateAuditModel();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Bob" };

        module.Update(data, table, EmptyUserContext(), model);

        Assert.True(data.ContainsKey("updated_at"));
        Assert.IsType<DateTime>(data["updated_at"]);
    }

    [Fact]
    public void Update_SetsUpdatedByFromUserContext()
    {
        var model = CreateAuditModel();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Bob" };

        module.Update(data, table, UserContextWithId("user-99"), model);

        Assert.Equal("user-99", data["updated_by_user_id"]);
    }

    [Fact]
    public void Update_DoesNotSetCreatedColumns()
    {
        var model = CreateAuditModel();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Bob" };

        module.Update(data, table, UserContextWithId("user-99"), model);

        Assert.False(data.ContainsKey("created_at"));
        Assert.False(data.ContainsKey("created_by_user_id"));
    }

    [Fact]
    public void Update_DoesNotSetDeletedColumns()
    {
        var model = CreateFullAuditModel();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Bob" };

        module.Update(data, table, UserContextWithId("user-99"), model);

        Assert.False(data.ContainsKey("deleted_at"));
        Assert.False(data.ContainsKey("deleted_by_user_id"));
    }

    [Fact]
    public void Update_OverwritesClientProvidedTimestamp()
    {
        var model = CreateAuditModel();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var spoofedTime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var data = new Dictionary<string, object?>
        {
            ["Name"] = "Bob",
            ["updated_at"] = spoofedTime
        };

        module.Update(data, table, EmptyUserContext(), model);

        var actual = (DateTime)data["updated_at"]!;
        Assert.NotEqual(spoofedTime, actual);
    }

    [Fact]
    public void Update_ReturnsEmptyErrorArray()
    {
        var model = CreateAuditModel();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Bob" };

        var errors = module.Update(data, table, EmptyUserContext(), model);

        Assert.Empty(errors);
    }

    #endregion

    #region Delete Tests

    [Fact]
    public void Delete_SetsUpdatedOnTimestamp()
    {
        var model = CreateFullAuditModel();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Id"] = 1 };

        module.Delete(data, table, EmptyUserContext(), model);

        Assert.True(data.ContainsKey("updated_at"));
        Assert.IsType<DateTime>(data["updated_at"]);
    }

    [Fact]
    public void Delete_SetsDeletedOnTimestamp()
    {
        var model = CreateFullAuditModel();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Id"] = 1 };

        module.Delete(data, table, EmptyUserContext(), model);

        Assert.True(data.ContainsKey("deleted_at"));
        Assert.IsType<DateTime>(data["deleted_at"]);
    }

    [Fact]
    public void Delete_SetsUpdatedByFromUserContext()
    {
        var model = CreateFullAuditModel();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Id"] = 1 };

        module.Delete(data, table, UserContextWithId("admin-1"), model);

        Assert.Equal("admin-1", data["updated_by_user_id"]);
    }

    [Fact]
    public void Delete_SetsDeletedByFromUserContext()
    {
        var model = CreateFullAuditModel();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Id"] = 1 };

        module.Delete(data, table, UserContextWithId("admin-1"), model);

        Assert.Equal("admin-1", data["deleted_by_user_id"]);
    }

    [Fact]
    public void Delete_DoesNotSetCreatedColumns()
    {
        var model = CreateFullAuditModel();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Id"] = 1 };

        module.Delete(data, table, UserContextWithId("admin-1"), model);

        Assert.False(data.ContainsKey("created_at"));
        Assert.False(data.ContainsKey("created_by_user_id"));
    }

    [Fact]
    public void Delete_ReturnsEmptyErrorArray()
    {
        var model = CreateFullAuditModel();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Id"] = 1 };

        var errors = module.Delete(data, table, EmptyUserContext(), model);

        Assert.Empty(errors);
    }

    #endregion

    #region Table Without Audit Columns

    [Fact]
    public void Insert_NoAuditColumns_DataUnchanged()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Alice" };

        module.Insert(data, table, UserContextWithId("user-1"), model);

        Assert.Single(data);
        Assert.Equal("Alice", data["Name"]);
    }

    [Fact]
    public void Update_NoAuditColumns_DataUnchanged()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Bob" };

        module.Update(data, table, UserContextWithId("user-1"), model);

        Assert.Single(data);
        Assert.Equal("Bob", data["Name"]);
    }

    [Fact]
    public void Delete_NoAuditColumns_DataUnchanged()
    {
        var model = StandardTestFixtures.SimpleUsers();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Id"] = 1 };

        module.Delete(data, table, UserContextWithId("user-1"), model);

        Assert.Single(data);
        Assert.Equal(1, data["Id"]);
    }

    #endregion

    #region Timestamp Consistency

    [Fact]
    public void Insert_CreatedOnAndUpdatedOnUseSameTimestamp()
    {
        var model = CreateAuditModel();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Alice" };

        module.Insert(data, table, EmptyUserContext(), model);

        var createdAt = (DateTime)data["created_at"]!;
        var updatedAt = (DateTime)data["updated_at"]!;
        Assert.Equal(createdAt, updatedAt);
    }

    [Fact]
    public void Delete_UpdatedOnAndDeletedOnUseSameTimestamp()
    {
        var model = CreateFullAuditModel();
        var module = new BasicAuditModule();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Id"] = 1 };

        module.Delete(data, table, EmptyUserContext(), model);

        var updatedAt = (DateTime)data["updated_at"]!;
        var deletedAt = (DateTime)data["deleted_at"]!;
        Assert.Equal(updatedAt, deletedAt);
    }

    #endregion

    #region ModulesWrap Integration

    [Fact]
    public void ModulesWrap_Insert_DelegatestoBasicAuditModule()
    {
        var model = CreateAuditModel();
        var wrap = new ModulesWrap
        {
            Modules = new IMutationModule[] { new BasicAuditModule() }
        };
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Alice" };

        var errors = wrap.Insert(data, table, UserContextWithId("user-1"), model);

        Assert.Empty(errors);
        Assert.True(data.ContainsKey("created_at"));
        Assert.Equal("user-1", data["created_by_user_id"]);
    }

    [Fact]
    public void ModulesWrap_Update_DelegatesToBasicAuditModule()
    {
        var model = CreateAuditModel();
        var wrap = new ModulesWrap
        {
            Modules = new IMutationModule[] { new BasicAuditModule() }
        };
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Name"] = "Bob" };

        var errors = wrap.Update(data, table, UserContextWithId("user-2"), model);

        Assert.Empty(errors);
        Assert.True(data.ContainsKey("updated_at"));
        Assert.Equal("user-2", data["updated_by_user_id"]);
    }

    [Fact]
    public void ModulesWrap_Delete_DelegatesToBasicAuditModule()
    {
        var model = CreateFullAuditModel();
        var wrap = new ModulesWrap
        {
            Modules = new IMutationModule[] { new BasicAuditModule() }
        };
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?> { ["Id"] = 1 };

        var errors = wrap.Delete(data, table, UserContextWithId("admin-1"), model);

        Assert.Empty(errors);
        Assert.True(data.ContainsKey("deleted_at"));
        Assert.Equal("admin-1", data["deleted_by_user_id"]);
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

    [Fact]
    public void AuditColumns_ReadOnlyFlagsMatchPopulateMetadata()
    {
        var model = CreateAuditModel();
        var table = model.GetTableFromDbName("Users");

        foreach (var column in table.Columns)
        {
            var isAuditColumn =
                column.CompareMetadata("populate", "created-on") ||
                column.CompareMetadata("populate", "created-by") ||
                column.CompareMetadata("populate", "updated-on") ||
                column.CompareMetadata("populate", "updated-by");

            if (isAuditColumn)
            {
                Assert.True(column.Metadata.ContainsKey("populate"),
                    $"Column '{column.ColumnName}' should have 'populate' metadata");
            }
        }
    }

    #endregion

    #region Helper Methods

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
