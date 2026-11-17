using BifrostQL.Core.Model;
using BifrostQL.UI.Web;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.UI.Tests;

/// <summary>
/// The active connection is one immutable (connection string, provider) snapshot. Bridge
/// handlers used to read the two as separate properties while HTTP connects wrote them
/// one after the other, so a read during a switch could pair the new string with the old
/// provider and build the wrong driver.
/// </summary>
public class ConnectionStateTests
{
    [Fact]
    public void RequireActive_WithNothingBound_ThrowsTheConnectFirstError()
    {
        var state = new ConnectionState();
        state.Invoking(s => s.RequireActive())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("No active database connection. Connect to a database first.");
    }

    [Fact]
    public async Task ConcurrentActivations_NeverExposeAStringPairedWithAnotherProvider()
    {
        var state = new ConnectionState();
        var services = new ServiceCollection().BuildServiceProvider();
        var sqlite = new ActiveConnection("Data Source=a.db", BifrostDbProvider.Sqlite);
        var postgres = new ActiveConnection("Host=h;Database=d", BifrostDbProvider.PostgreSql);
        state.SetInitial(sqlite.ConnectionString, sqlite.Provider);

        using var stop = new CancellationTokenSource();
        var torn = 0;
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var active = state.Active!;
                if (active != sqlite && active != postgres) Interlocked.Increment(ref torn);
            }
        });

        var writers = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            for (var n = 0; n < 500; n++)
                await state.ActivateAsync(services, (n + i) % 2 == 0 ? sqlite : postgres, profileSchema: null, CancellationToken.None);
        })).ToArray();
        await Task.WhenAll(writers);
        stop.Cancel();
        await reader;

        torn.Should().Be(0);
        state.Active.Should().Match<ActiveConnection>(a => a == sqlite || a == postgres);
    }
}
