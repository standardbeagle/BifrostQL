using BifrostQL.Core.Auth;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test;

public class StateTransitionObserverTests
{
    [Fact]
    public async Task NotifyAsync_PublishesTransitionToEveryObserver()
    {
        var transition = new StateTransitionInfo(
            "members",
            42,
            "pending",
            "active",
            "user-1",
            "StateTransitioned");
        var observer = new CapturingObserver();
        var observers = new StateTransitionObservers(new IStateTransitionObserver[] { observer });
        var userContext = new Dictionary<string, object?> { ["user_id"] = "user-1" };

        await observers.NotifyAsync(transition, userContext);

        observer.Transitions.Should().ContainSingle().Which.Should().Be(transition);
        observer.UserContexts.Should().ContainSingle().Which.Should().BeSameAs(userContext);
    }

    private sealed class CapturingObserver : IStateTransitionObserver
    {
        public List<StateTransitionInfo> Transitions { get; } = new();
        public List<IDictionary<string, object?>> UserContexts { get; } = new();

        public ValueTask OnTransitionAsync(
            StateTransitionInfo transition,
            IDictionary<string, object?> userContext)
        {
            Transitions.Add(transition);
            UserContexts.Add(userContext);
            return ValueTask.CompletedTask;
        }
    }
}
