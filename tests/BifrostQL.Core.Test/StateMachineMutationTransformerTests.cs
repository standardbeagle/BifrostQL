using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Core.Test;

public class StateMachineMutationTransformerTests
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

    private static IDbTable StateMachineTable() =>
        TableWithMetadata(
            (MetadataKeys.StateMachine.StateColumn, "status"),
            (MetadataKeys.StateMachine.InitialState, "pending"),
            (MetadataKeys.StateMachine.States, "pending, active, inactive"),
            (MetadataKeys.StateMachine.Transitions, "pending->active[officer]; active->inactive; inactive->active[officer]"));

    private static MutationTransformContext Context(
        string currentState = "pending",
        params string[] roles)
    {
        return new MutationTransformContext
        {
            Model = Substitute.For<IDbModel>(),
            UserContext = new Dictionary<string, object?>
            {
                [MetadataKeys.Auth.DefaultUserIdContextKey] = "user-1",
                [MetadataKeys.Auth.DefaultRolesContextKey] = roles,
            },
            CurrentRow = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["status"] = currentState,
            },
        };
    }

    [Fact]
    public void AppliesTo_ReturnsFalse_WhenTableHasNoStateMachineMetadata()
    {
        var transformer = new StateMachineMutationTransformer();

        var applies = transformer.AppliesTo(
            TableWithMetadata(),
            MutationType.Update,
            Context());

        applies.Should().BeFalse();
    }

    [Fact]
    public void Transform_AllowsValidTransition_WhenCallerHasRequiredRole()
    {
        var transformer = new StateMachineMutationTransformer();
        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = 1,
            ["status"] = "active",
        };

        var result = transformer.Transform(StateMachineTable(), MutationType.Update, data, Context("pending", "officer"));

        result.Errors.Should().BeEmpty();
        result.Data.Should().BeSameAs(data);
        result.StateTransition.Should().BeEquivalentTo(new
        {
            Entity = "members",
            EntityId = (object?)null,
            From = "pending",
            To = "active",
            Actor = "user-1",
            EventName = "StateTransitioned",
        });
    }

    [Fact]
    public void Transform_RejectsInvalidTransition_WithGenericError()
    {
        var transformer = new StateMachineMutationTransformer();
        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = 1,
            ["status"] = "inactive",
        };

        var result = transformer.Transform(StateMachineTable(), MutationType.Update, data, Context("pending", "officer"));

        result.Errors.Should().Equal("State transition is not permitted.");
        result.StateTransition.Should().BeNull();
    }

    [Fact]
    public void Transform_RejectsRoleGatedTransition_WhenCallerLacksRole()
    {
        var transformer = new StateMachineMutationTransformer();
        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = 1,
            ["status"] = "active",
        };

        var result = transformer.Transform(StateMachineTable(), MutationType.Update, data, Context("pending", "member"));

        result.Errors.Should().Equal("State transition is not permitted.");
    }

    [Fact]
    public void Transform_AllowsRoleGatedTransition_ForAdmin()
    {
        var transformer = new StateMachineMutationTransformer();
        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = 1,
            ["status"] = "active",
        };

        var result = transformer.Transform(StateMachineTable(), MutationType.Update, data, Context("pending", "admin"));

        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Transform_DoesNothing_WhenUpdateDoesNotTouchStateColumn()
    {
        var transformer = new StateMachineMutationTransformer();
        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = 1,
            ["name"] = "Ada",
        };

        var result = transformer.Transform(StateMachineTable(), MutationType.Update, data, Context("pending", "member"));

        result.Errors.Should().BeEmpty();
        result.Data.Should().BeSameAs(data);
    }

    [Fact]
    public void Transform_AllowsInsertWithInitialState()
    {
        var transformer = new StateMachineMutationTransformer();
        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["status"] = "pending",
        };

        var result = transformer.Transform(StateMachineTable(), MutationType.Insert, data, Context("pending", "member"));

        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Transform_RejectsInsertWithNonInitialState()
    {
        var transformer = new StateMachineMutationTransformer();
        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["status"] = "active",
        };

        var result = transformer.Transform(StateMachineTable(), MutationType.Insert, data, Context("pending", "officer"));

        result.Errors.Should().Equal("State transition is not permitted.");
    }

    [Fact]
    public void Transform_RejectsUpdate_WhenCurrentRowStateIsMissing()
    {
        var transformer = new StateMachineMutationTransformer();
        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = 1,
            ["status"] = "active",
        };
        var context = new MutationTransformContext
        {
            Model = Substitute.For<IDbModel>(),
            UserContext = new Dictionary<string, object?>(),
        };

        var result = transformer.Transform(StateMachineTable(), MutationType.Update, data, context);

        result.Errors.Should().Equal("State transition is not permitted.");
    }
}
