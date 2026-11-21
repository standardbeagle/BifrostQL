using BifrostQL.Core.Resolvers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace BifrostQL.Core.Test;

/// <summary>
/// Installs a <see cref="FakeLogger"/> as the <see cref="BifrostErrorSink"/> logger for its
/// lifetime. Overrides nest: the innermost live capture receives the records. A class using it
/// joins <see cref="ErrorSinkLoggerCollection"/>.
/// </summary>
internal sealed class ErrorSinkCapture : IDisposable
{
    private readonly FakeLogger _logger = new();
    private readonly IDisposable _override;

    public ErrorSinkCapture() => _override = BifrostErrorSink.OverrideForTesting(_logger);

    public IReadOnlyList<FakeLogRecord> Entries => _logger.Collector.GetSnapshot();

    public IReadOnlyList<string> Messages => Entries.Select(e => e.Message).ToList();

    public IReadOnlyList<string> Warnings => Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();

    public void Dispose() => _override.Dispose();
}
