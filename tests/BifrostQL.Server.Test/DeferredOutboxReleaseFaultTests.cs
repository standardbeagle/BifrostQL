using System.Reflection;
using BifrostQL.Core.Schema;
using BifrostQL.Server;
using FluentAssertions;
using GraphQL;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// LOW bundle item 1 (RED): a loop fault must be logged and observed at shutdown. The
    /// current <see cref="DeferredOutboxReleaseHostedService"/> swallows the body exception in
    /// a bare <c>catch {}</c> and retries after a 5 s delay; when shutdown cancels that delay,
    /// the <see cref="OperationCanceledException"/> escapes the catch, the detached loop task
    /// faults, and <c>StopAsync</c> never observes it.
    /// </summary>
    public sealed class DeferredOutboxReleaseFaultTests
    {
        [Fact]
        public async Task Faulting_loop_then_shutdown_leaves_no_faulted_unobserved_task()
        {
            var loaderRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var paths = new PathCache<Inputs>();
            paths.AddLoader("p", () =>
            {
                loaderRan.TrySetResult();
                throw new InvalidOperationException("boom");
            });

            var service = new DeferredOutboxReleaseHostedService(paths);
            await service.StartAsync(CancellationToken.None);
            // The body has thrown once and is now inside the catch's retry delay; shutdown
            // cancels that delay, which is the escape path under test.
            await loaderRan.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await service.StopAsync(CancellationToken.None);

            var loop = (Task?)typeof(DeferredOutboxReleaseHostedService)
                .GetField("_loop", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(service);
            loop.Should().NotBeNull();
            loop!.IsCompleted.Should().BeTrue("StopAsync must observe the loop to completion");
            loop.Status.Should().Be(TaskStatus.RanToCompletion,
                "a faulting loop (or a cancellation escaping the catch) must not leave the "
                + "detached loop task faulted and unobserved");
        }
    }
}
