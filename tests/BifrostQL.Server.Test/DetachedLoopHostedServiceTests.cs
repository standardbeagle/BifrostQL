using BifrostQL.Server;
using FluentAssertions;
using Microsoft.Extensions.Logging;
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
            var logger = new ListLogger();
            var host = new DetachedLoopHostedService(
                "test-loop",
                _ => throw new InvalidOperationException("boom"),
                logger);

            await host.StartAsync(CancellationToken.None);
            await host.StopAsync(CancellationToken.None);

            logger.Errors.Should().ContainSingle(m => m.Contains("test-loop") && m.Contains("faulted"),
                "a faulting loop must be logged with the loop name — never a bare catch {}");
        }

        [Fact]
        public async Task Cancellation_escaping_the_loop_body_during_shutdown_is_a_clean_exit()
        {
            var logger = new ListLogger();
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

            logger.Errors.Should().BeEmpty("a shutdown cancellation is a clean exit, not a fault");
        }

        private sealed class ListLogger : ILogger
        {
            public List<string> Errors { get; } = new();

            IDisposable? ILogger.BeginScope<TState>(TState state) => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Error)
                    Errors.Add(formatter(state, exception));
            }
        }
    }
}
