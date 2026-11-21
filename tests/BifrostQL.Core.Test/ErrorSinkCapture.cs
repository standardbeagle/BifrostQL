using BifrostQL.Core.Resolvers;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Core.Test;

/// <summary>
/// Installs itself as the <see cref="BifrostErrorSink"/> logger for its lifetime and records
/// every entry as "Level: message". A class using it joins <see cref="ErrorSinkLoggerCollection"/>.
/// </summary>
internal sealed class ErrorSinkCapture : ILogger, IDisposable
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();
    private readonly IDisposable _override;

    public ErrorSinkCapture() => _override = BifrostErrorSink.OverrideForTesting(this);

    public IReadOnlyList<string> Lines => _lines.ToArray();

    public IReadOnlyList<string> Warnings => Lines.Where(l => l.StartsWith("Warning: ", StringComparison.Ordinal)).ToList();

    public void Dispose() => _override.Dispose();

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    bool ILogger.IsEnabled(LogLevel logLevel) => true;

    void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _lines.Enqueue($"{logLevel}: {formatter(state, exception)}");
}
