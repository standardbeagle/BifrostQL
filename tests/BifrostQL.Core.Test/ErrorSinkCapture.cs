using BifrostQL.Core.Resolvers;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Core.Test;

/// <summary>
/// Installs itself as the <see cref="BifrostErrorSink"/> logger for its lifetime and records
/// every entry. Overrides nest: the innermost live capture receives the entries. A class
/// using it joins <see cref="ErrorSinkLoggerCollection"/>.
/// </summary>
internal sealed class ErrorSinkCapture : ILogger, IDisposable
{
    internal readonly record struct Entry(LogLevel Level, string Message, Exception? Exception);

    private readonly System.Collections.Concurrent.ConcurrentQueue<Entry> _entries = new();
    private readonly IDisposable _override;

    public ErrorSinkCapture() => _override = BifrostErrorSink.OverrideForTesting(this);

    public IReadOnlyList<Entry> Entries => _entries.ToArray();

    public IReadOnlyList<string> Messages => _entries.Select(e => e.Message).ToList();

    public IReadOnlyList<string> Warnings => _entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();

    public void Dispose() => _override.Dispose();

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    bool ILogger.IsEnabled(LogLevel logLevel) => true;

    void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _entries.Enqueue(new Entry(logLevel, formatter(state, exception), exception));
}
