using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// Unit coverage for <see cref="ConcurrencyMutationTransformer"/>: it guards only
/// UPDATE, ANDs a <c>token = @clientVersion</c> predicate into the WHERE, bumps the
/// token in the SET, flags <see cref="MutationTransformResult.ConflictOnNoRows"/>, and
/// rejects a missing token or an unsupported token type.
/// </summary>
public sealed class ConcurrencyMutationTransformerTests
{
    private static IDbModel IntTokenModel() =>
        DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("version", "int")
                .WithMetadata(MetadataKeys.Concurrency.Token, "version"))
            .Build();

    private static MutationTransformContext Context(IDbModel model) => new()
    {
        Model = model,
        UserContext = new Dictionary<string, object?>(),
    };

    private static Dictionary<string, object?> UpdateData(int version) =>
        new() { ["Id"] = 1, ["Name"] = "changed", ["version"] = version };

    [Fact]
    public void Priority_IsSixty()
        => new ConcurrencyMutationTransformer().Priority.Should().Be(60);

    [Fact]
    public void AppliesTo_UpdateWithMetadata_True_ButNotInsertOrDelete()
    {
        var model = IntTokenModel();
        var table = model.GetTableFromDbName("Orders");
        var transformer = new ConcurrencyMutationTransformer();
        var ctx = Context(model);

        transformer.AppliesTo(table, MutationType.Update, ctx).Should().BeTrue();
        transformer.AppliesTo(table, MutationType.Insert, ctx).Should().BeFalse();
        transformer.AppliesTo(table, MutationType.Delete, ctx).Should().BeFalse();
    }

    [Fact]
    public async Task Update_GuardsWhereOnClientVersion_BumpsSet_AndFlagsConflict()
    {
        var model = IntTokenModel();
        var table = model.GetTableFromDbName("Orders");

        var result = await new ConcurrencyMutationTransformer()
            .TransformAsync(table, MutationType.Update, UpdateData(3), Context(model));

        result.Errors.Should().BeEmpty();
        result.ConflictOnNoRows.Should().BeTrue();

        // WHERE: version = 3 (the client's read version).
        result.AdditionalFilter.Should().NotBeNull();
        result.AdditionalFilter!.ColumnName.Should().Be("version");
        result.AdditionalFilter.Next!.Value.Should().Be(3);

        // SET: version bumped to 4.
        result.Data["version"].Should().Be(4L);
    }

    [Fact]
    public async Task Update_MissingToken_IsRejected()
    {
        var model = IntTokenModel();
        var table = model.GetTableFromDbName("Orders");
        var data = new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "changed" };

        var result = await new ConcurrencyMutationTransformer()
            .TransformAsync(table, MutationType.Update, data, Context(model));

        result.Errors.Should().ContainSingle().Which.Should().Contain("must include the concurrency token");
    }

    [Fact]
    public async Task Update_DatetimeToken_RestampsToNow()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("updated_at", "datetime2")
                .WithMetadata(MetadataKeys.Concurrency.Token, "updated_at"))
            .Build();
        var table = model.GetTableFromDbName("Orders");
        var data = new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "x", ["updated_at"] = "2020-01-01T00:00:00Z" };

        var result = await new ConcurrencyMutationTransformer()
            .TransformAsync(table, MutationType.Update, data, Context(model));

        result.Errors.Should().BeEmpty();
        result.Data["updated_at"].Should().BeOfType<DateTimeOffset>();
        result.ConflictOnNoRows.Should().BeTrue();
    }

    [Fact]
    public async Task Update_TokenValueAtMax_IsRejectedCleanly_NotThrown()
    {
        // A Decimal token at decimal.MaxValue cannot be advanced; the transformer must
        // return a clean error result, not throw an unhandled OverflowException.
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("version", "decimal")
                .WithMetadata(MetadataKeys.Concurrency.Token, "version"))
            .Build();
        var table = model.GetTableFromDbName("Orders");
        var data = new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "x", ["version"] = decimal.MaxValue };

        var result = await new ConcurrencyMutationTransformer()
            .TransformAsync(table, MutationType.Update, data, Context(model));

        result.Errors.Should().ContainSingle().Which.Should().Contain("representable range");
    }

    [Fact]
    public async Task Update_UnsupportedTokenType_IsRejected()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("row_version", "nvarchar")
                .WithMetadata(MetadataKeys.Concurrency.Token, "row_version"))
            .Build();
        var table = model.GetTableFromDbName("Orders");
        var data = new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "x", ["row_version"] = "abc" };

        var result = await new ConcurrencyMutationTransformer()
            .TransformAsync(table, MutationType.Update, data, Context(model));

        result.Errors.Should().ContainSingle().Which.Should().Contain("unsupported type");
    }
}
