using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.Deferred;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Core.Test;

public class DeferredConfigTests
{
    private static IDbTable TableWithMetadata(params (string key, object? value)[] metadata)
    {
        var table = Substitute.For<IDbTable>();
        var dictionary = metadata.ToDictionary(entry => entry.key, entry => entry.value);
        table.DbName.Returns("orders");
        table.TableSchema.Returns("dbo");
        table.Metadata.Returns(dictionary);
        table.GetMetadataValue(Arg.Any<string>())
            .Returns(call => dictionary.TryGetValue((string)call[0], out var value) ? value?.ToString() : null);
        return table;
    }

    [Theory]
    [InlineData("90d", 90)]
    [InlineData("12h", 0.5)]
    public void FromTable_ValidUndoWindow_ParsesDuration(string undoWindow, double expectedDays)
    {
        var table = TableWithMetadata(
            (MetadataKeys.Deferred.Deferrable, "enabled"),
            (MetadataKeys.Deferred.UndoWindow, undoWindow));

        var config = DeferredConfig.FromTable(table);

        config.IsDeferrable.Should().BeTrue();
        config.UndoWindow.Should().Be(TimeSpan.FromDays(expectedDays));
    }

    [Fact]
    public void FromTable_WithoutDeferrable_ReturnsNoneWithoutApplyingPartialConfig()
    {
        var table = TableWithMetadata((MetadataKeys.Deferred.UndoWindow, "90d"));

        var config = DeferredConfig.FromTable(table);

        config.Should().BeSameAs(DeferredConfig.None);
        config.IsDeferrable.Should().BeFalse();
        config.UndoWindow.Should().BeNull();
    }

    [Fact]
    public void ChangeSetColumnContracts_AreCentralized()
    {
        MetadataKeys.Deferred.ChangeSet.Columns.Should().Equal(
            "id", "state", "undo_window_expires_at", "requester", "tenant", "tables", "created_at", "applied_at", "reversed_at");
        MetadataKeys.Deferred.ChangeSetDelta.Columns.Should().Equal(
            "id", "change_set_id", "table", "pk", "op", "inverse_op", "before_image", "after_image", "created_at");
    }

    [Fact]
    public void Validate_MiscasedDeferrable_ThrowsHardError()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", table => table
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("version", "int")
                .WithMetadata(MetadataKeys.Concurrency.Token, "version")
                .WithMetadata(MetadataKeys.History.Enabled, "enabled")
                .WithMetadata("Deferrable", "enabled")
                .WithMetadata(MetadataKeys.Deferred.UndoWindow, "90d"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("Deferrable").And.Contain("casing");
    }

    [Fact]
    public void Validate_DeferrableWithoutConcurrencyAndHistory_RejectsConfiguration()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", table => table
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithMetadata(MetadataKeys.Deferred.Deferrable, "enabled")
                .WithMetadata(MetadataKeys.Deferred.UndoWindow, "90d"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MetadataKeys.Deferred.Deferrable)
            .And.Contain(MetadataKeys.Concurrency.Token)
            .And.Contain(MetadataKeys.History.Enabled);
    }
}
