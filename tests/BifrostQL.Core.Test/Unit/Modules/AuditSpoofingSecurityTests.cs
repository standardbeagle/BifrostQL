using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// Anti-spoofing coverage for <see cref="AuditMutationTransformer"/>: a client must
/// never be able to supply created-by/updated-by/deleted-by values. When no
/// user-audit-key is configured the transformer previously let a client-supplied
/// value through (spoofable). The fix strips the client value regardless of whether
/// a user-audit-key is configured.
/// </summary>
public class AuditSpoofingSecurityTests
{
    private static IDbModel ModelWithoutAuditKey() =>
        DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("created_by_user_id", "nvarchar")
                .WithColumnMetadata("created_by_user_id", "populate", "created-by")
                .WithColumn("updated_by_user_id", "nvarchar")
                .WithColumnMetadata("updated_by_user_id", "populate", "updated-by"))
            .Build();

    private static MutationTransformContext Context(IDbModel model, IDictionary<string, object?> userContext) =>
        new() { Model = model, UserContext = userContext };

    [Fact]
    public async Task Insert_StripsClientCreatedBy_WhenNoAuditKeyConfigured()
    {
        var model = ModelWithoutAuditKey();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?>
        {
            ["Name"] = "Alice",
            ["created_by_user_id"] = "spoofed-user",
        };

        await transformer.TransformAsync(table, MutationType.Insert, data,
            Context(model, new Dictionary<string, object?>()));

        // The spoofed value must NOT survive: without a trustworthy audit key the
        // key is removed entirely so the DB default stands, never the client value.
        data.ContainsKey("created_by_user_id").Should().BeFalse();
    }

    [Fact]
    public async Task Update_StripsClientUpdatedBy_WhenNoAuditKeyConfigured()
    {
        var model = ModelWithoutAuditKey();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?>
        {
            ["Name"] = "Alice",
            ["updated_by_user_id"] = "spoofed-user",
        };

        await transformer.TransformAsync(table, MutationType.Update, data,
            Context(model, new Dictionary<string, object?>()));

        data.ContainsKey("updated_by_user_id").Should().BeFalse();
    }

    [Fact]
    public async Task Insert_OverridesClientCreatedBy_WhenAuditKeyConfigured()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("user-audit-key", "id")
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("created_by_user_id", "nvarchar")
                .WithColumnMetadata("created_by_user_id", "populate", "created-by"))
            .Build();
        var transformer = new AuditMutationTransformer();
        var table = model.GetTableFromDbName("Users");
        var data = new Dictionary<string, object?>
        {
            ["Name"] = "Alice",
            ["created_by_user_id"] = "spoofed-user",
        };

        await transformer.TransformAsync(table, MutationType.Insert, data,
            Context(model, new Dictionary<string, object?> { ["id"] = "real-user" }));

        // With an audit key the resolved claim wins over the client value.
        data["created_by_user_id"].Should().Be("real-user");
    }
}
