using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BifrostQL.Core.Modules.Cdc;
using BifrostQL.Core.Schema;
using FluentAssertions;
using GraphQL;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// A deferred-connection host (started without a connection string, e.g. the desktop UI
/// before the user connects) makes the PathCache loader throw
/// <see cref="ConnectionNotConfiguredException"/>. The dispatcher must treat that as
/// "idle until connected" — never as a drain failure spammed at error level every pass.
/// Any other loader exception must still surface as an error (fail-safe logging intact).
/// </summary>
public sealed class OutboxDeferredConnectionTests
{
    [Fact]
    public async Task ConnectionNotConfigured_IdlesWithoutErrorLog()
    {
        // Arrange: a loader that reports deferred-connection mode, and cancels after the
        // first pass so the loop exits deterministically.
        var cts = new CancellationTokenSource();
        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader("/graphql", () =>
        {
            cts.Cancel();
            throw new ConnectionNotConfiguredException();
        });
        var logger = new CaptureLogger();
        var dispatcher = new OutboxDispatcher(pathCache, sink: null, jitter: () => 0.0, logger: logger);

        // Act
        await dispatcher.RunAsync(cts.Token);

        // Assert: quiet — at most debug chatter, no error-level "drain pass failed" spam.
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task OtherLoaderFailure_StillLogsError()
    {
        // Arrange: any other resolution failure must keep the fail-safe error log.
        var cts = new CancellationTokenSource();
        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader("/graphql", () =>
        {
            cts.Cancel();
            throw new InvalidOperationException("boom");
        });
        var logger = new CaptureLogger();
        var dispatcher = new OutboxDispatcher(pathCache, sink: null, jitter: () => 0.0, logger: logger);

        // Act
        await dispatcher.RunAsync(cts.Token);

        // Assert
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task MultiEndpointHost_WarnsThatOnlyTheFirstEndpointIsDrained()
    {
        // CDC is single-endpoint (dispatcher and webhook-secret resolution both read the
        // FIRST registered endpoint). In a multi-database host the other outboxes are
        // silently never drained — the same silent-first hazard the HTTP and binary
        // transports refuse — so the dispatcher must at least surface it to the operator.
        var cts = new CancellationTokenSource();
        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader("/a", () =>
        {
            cts.Cancel();
            throw new ConnectionNotConfiguredException();
        });
        pathCache.AddLoader("/b", () => throw new ConnectionNotConfiguredException());
        var logger = new CaptureLogger();
        var dispatcher = new OutboxDispatcher(pathCache, sink: null, jitter: () => 0.0, logger: logger);

        await dispatcher.RunAsync(cts.Token);

        logger.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("single endpoint"))
            .Should().ContainSingle("a multi-DB host must be told CDC drains only the first endpoint");
    }

    private sealed class CaptureLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception) +
                (exception is null ? "" : $" [{exception.GetType().FullName}: {exception.Message}]"), exception));
    }
}
