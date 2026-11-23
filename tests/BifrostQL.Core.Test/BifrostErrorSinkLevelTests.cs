using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// The sink's level encodes who caused the refusal. A server fault (database down, plugin
/// unregistered) logs at Error so the operator sees it at default levels; a client-caused
/// refusal (bad name, stale token) stays at Debug, so a client cannot flood the log at will.
/// </summary>
[Collection(ErrorSinkLoggerCollection.Name)]
public sealed class BifrostErrorSinkLevelTests
{
    [Fact]
    public void ServerFault_LogsAtError_WithTheException_AndSanitizesTheWire()
    {
        using var sink = new ErrorSinkCapture();
        var cause = new InvalidOperationException("driver detail");

        var error = BifrostErrorSink.ServerFault("Unavailable.", "model load failed", "Site.A", cause);

        error.Message.Should().Be("Unavailable.");
        var entry = sink.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Error);
        entry.Exception.Should().BeSameAs(cause);
        entry.Message.Should().Contain("Site.A").And.Contain("model load failed");
    }

    [Fact]
    public void ClientCausedRefusals_StayAtDebug()
    {
        using var sink = new ErrorSinkCapture();

        _ = BifrostErrorSink.Sanitized("Conflict.", "lost update on main.orders", "Site.B", "CONFLICT");
        _ = BifrostErrorSink.LookupMiss("Not found.", "no table main.nope", "Site.C");

        sink.Entries.Should().HaveCount(2).And.OnlyContain(e => e.Level == LogLevel.Debug);
    }
}
