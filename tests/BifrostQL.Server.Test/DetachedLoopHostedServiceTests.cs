using BifrostQL.Server;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// LOW bundle item 1: <see cref="DetachedLoopHostedService"/> is the one generic host for
    /// detached background loops — a faulting loop is LOGGED (never a bare silent catch), and
    /// shutdown observes the loop task, so a fault can never die unobserved. A cancellation
    /// escaping the loop body during shutdown is a clean exit, not a fault.
    /// </summary>
    public sealed class DetachedLoopHostedServiceTests
    {
        [Fact]
        public async Task Faulting_loop_is_logged_and_shutdown_completes()
        {
            var logger = new FakeLogger<DetachedLoopHostedService>();
            var host = new DetachedLoopHostedService(
                "test-loop",
                _ => throw new InvalidOperationException("boom"),
                logger);

            await host.StartAsync(CancellationToken.None);
            await host.StopAsync(CancellationToken.None);

            logger.Collector.GetSnapshot().Should().ContainSingle(r => r.Level >= LogLevel.Error && r.Message.Contains("test-loop") && r.Message.Contains("faulted"),
                "a faulting loop must be logged with the loop name — never a bare catch {}");
        }

        [Fact]
        public async Task Cancellation_escaping_the_loop_body_during_shutdown_is_a_clean_exit()
        {
            var logger = new FakeLogger<DetachedLoopHostedService>();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var host = new DetachedLoopHostedService(
                "test-loop",
                async ct =>
                {
                    entered.TrySetResult();
                    // Cancellation escapes the body from a delay — the exact path that used to
                    // fault DeferredOutboxReleaseHostedService's detached task.
                    await Task.Delay(Timeout.Infinite, ct);
                },
                logger);

            await host.StartAsync(CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await host.StopAsync(CancellationToken.None);

            logger.Collector.GetSnapshot().Should().NotContain(r => r.Level >= LogLevel.Error,"a shutdown cancellation is a clean exit, not a fault");
        }
    }
}
