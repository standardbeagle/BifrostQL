using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Core.Test;

public class StateMachineConfigCollectorTests
{
    private static IDbTable TableWithMetadata(params (string key, object? value)[] metadata)
    {
        var table = Substitute.For<IDbTable>();
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in metadata)
            dict[key] = value;

        table.DbName.Returns("members");
        table.TableSchema.Returns("main");
        table.Metadata.Returns(dict);
        table.GetMetadataValue(Arg.Any<string>())
            .Returns(ci => dict.TryGetValue((string)ci[0], out var v) ? v?.ToString() : null);

        return table;
    }

    [Fact]
    public void FromTable_ReturnsNull_WhenNoStateMachineMetadata()
    {
        var table = TableWithMetadata();

        var definition = StateMachineConfigCollector.FromTable(table);

        definition.Should().BeNull();
    }

    [Fact]
    public void FromTable_ReturnsNull_WhenStateMachineMetadataIsBlank()
    {
        var table = TableWithMetadata(
            (MetadataKeys.StateMachine.StateColumn, ""),
            (MetadataKeys.StateMachine.InitialState, " "),
            (MetadataKeys.StateMachine.States, null),
            (MetadataKeys.StateMachine.Transitions, ""));

        var definition = StateMachineConfigCollector.FromTable(table);

        definition.Should().BeNull();
    }

    [Fact]
    public void FromTable_ParsesWellFormedStateMachineMetadata()
    {
        var table = TableWithMetadata(
            (MetadataKeys.StateMachine.StateColumn, "status"),
            (MetadataKeys.StateMachine.InitialState, "pending"),
            (MetadataKeys.StateMachine.States, "pending, active, inactive"),
            (MetadataKeys.StateMachine.Transitions,
                "pending->active[officer,admin]@member.activated; active->inactive[officer]; inactive->active"));

        var definition = StateMachineConfigCollector.FromTable(table);

        definition.Should().NotBeNull();
        definition!.StateColumn.Should().Be("status");
        definition.InitialState.Should().Be("pending");
        definition.States.Should().BeEquivalentTo("pending", "active", "inactive");
        definition.Transitions.Should().BeEquivalentTo(new[]
        {
            new StateMachineTransition("pending", "active", new[] { "officer", "admin" }, "member.activated"),
            new StateMachineTransition("active", "inactive", new[] { "officer" }, null),
            new StateMachineTransition("inactive", "active", Array.Empty<string>(), null),
        });
    }

    [Fact]
    public void FromTable_ParsesPipeSeparatedTransitions_ForMetadataLoaderRules()
    {
        var table = TableWithMetadata(
            (MetadataKeys.StateMachine.StateColumn, "status"),
            (MetadataKeys.StateMachine.InitialState, "pending"),
            (MetadataKeys.StateMachine.States, "pending, active, inactive"),
            (MetadataKeys.StateMachine.Transitions, "pending->active[officer]|active->inactive"));

        var definition = StateMachineConfigCollector.FromTable(table);

        definition.Should().NotBeNull();
        definition!.Transitions.Should().HaveCount(2);
    }

    [Fact]
    public void FromTable_ThrowsNonLeakingError_WhenRequiredMetadataIsMissing()
    {
        var table = TableWithMetadata(
            (MetadataKeys.StateMachine.StateColumn, "status"),
            (MetadataKeys.StateMachine.States, "pending, active"),
            (MetadataKeys.StateMachine.Transitions, "pending->active"));

        var act = () => StateMachineConfigCollector.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Invalid state-machine metadata.");
    }

    [Theory]
    [InlineData("pending-active")]
    [InlineData("pending->")]
    [InlineData("pending->archived")]
    [InlineData("pending->active[")]
    public void FromTable_ThrowsNonLeakingError_WhenTransitionMetadataIsMalformed(string transitions)
    {
        var table = TableWithMetadata(
            (MetadataKeys.StateMachine.StateColumn, "status"),
            (MetadataKeys.StateMachine.InitialState, "pending"),
            (MetadataKeys.StateMachine.States, "pending, active"),
            (MetadataKeys.StateMachine.Transitions, transitions));

        var act = () => StateMachineConfigCollector.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Invalid state-machine metadata.");
    }
}
