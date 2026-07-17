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

    // LOAD-BEARING for FileObjectSeam.PutAsync's compensating-delete correctness.
    //
    // FileObjectSeam (src/BifrostQL.Core/Storage/FileObjectSeam.cs) uploads a blob,
    // then writes the row's file pointer through the mutation pipeline inside a
    // try/catch. Its catch treats ANY exception as "nothing committed" and
    // compensates by DELETING the just-uploaded blob. That inference is only sound
    // because a post-commit mutation observer cannot surface as an exception on the
    // write path: MutationObservers.NotifyAsync swallows a throwing observer (logs
    // it, does not propagate). If this contract ever flips to propagation, an
    // already-committed write would throw into FileObjectSeam's catch and the
    // compensating delete would destroy LIVE, committed content — silent data loss,
    // not a rollback. This test pins the swallow so that regression is caught here
    // (and points the reader at FileObjectSeam's dependency) rather than manifesting
    // as data loss over the S3 write path. If NotifyAsync ever propagates, this test
    // fails.
    [Fact]
    public async Task NotifyAsync_SwallowsThrowingObserver_FileObjectSeamCompensationDependsOnThis()
    {
        var failing = new ThrowingObserver();
        var observers = new MutationObservers(new IMutationObserver[] { failing });

        var act = async () => await observers.NotifyAsync(BuildContext());

        // Must NOT propagate: FileObjectSeam relies on a post-commit observer
        // failure never reaching its compensating-delete catch as an exception.
        await act.Should().NotThrowAsync();
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
            MutationState = MutationObserverContext.NewMutationState(),
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
