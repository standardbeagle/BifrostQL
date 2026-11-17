using Microsoft.Extensions.Hosting;

namespace BifrostQL.Server.Auth
{
    /// <summary>
    /// Provisions the <see cref="ILocalAuthStateStore"/> when the host starts, so its tables exist
    /// before the first request and a store that cannot create them stops the start.
    /// </summary>
    internal sealed class LocalAuthStateProvisioner : IHostedService
    {
        private readonly ILocalAuthStateStore _store;

        public LocalAuthStateProvisioner(ILocalAuthStateStore store) => _store = store;

        public Task StartAsync(CancellationToken cancellationToken) => _store.EnsureProvisionedAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
