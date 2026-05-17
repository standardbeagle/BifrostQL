using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Core.Test;

public class MutationObserverTests
{
    [Fact]
    public async Task NotifyAsync_PublishesContextToEveryObserver()
    {
        var observer = new CapturingObserver();
        var observers = new MutationObservers(new IMutationObserver[] { observer });
        var ctx = BuildContext();

        await observers.NotifyAsync(ctx);

        observer.Contexts.Should().ContainSingle().Which.Should().BeSameAs(ctx);
    }

    [Fact]
    public async Task NotifyAsync_ThrowingObserverDoesNotAbortRemainingObservers()
    {
        var failing = new ThrowingObserver();
        var trailing = new CapturingObserver();
        var observers = new MutationObservers(new IMutationObserver[] { failing, trailing });

        var act = async () => await observers.NotifyAsync(BuildContext());

        await act.Should().NotThrowAsync();
        trailing.Contexts.Should().ContainSingle();
    }

    private static MutationObserverContext BuildContext()
    {
        var table = Substitute.For<IDbTable>();
        table.DbName.Returns("Members");
        return new MutationObserverContext
        {
            Table = table,
            MutationType = MutationType.Update,
            Data = new Dictionary<string, object?> { ["id"] = 1 },
            Result = 1,
            UserContext = new Dictionary<string, object?>(),
        };
    }

    private sealed class CapturingObserver : IMutationObserver
    {
        public List<MutationObserverContext> Contexts { get; } = new();
        public ValueTask OnMutationAsync(MutationObserverContext context)
        {
            Contexts.Add(context);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingObserver : IMutationObserver
    {
        public ValueTask OnMutationAsync(MutationObserverContext context)
            => throw new InvalidOperationException("observer boom");
    }
}
