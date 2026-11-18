using Microsoft.Extensions.Hosting;

namespace BifrostQL.Server.Auth
{
    /// <summary>
    /// Provisions the <see cref="ILocalAuthStateStore"/> when the host starts, so its tables exist
    /// before the first request and a store that cannot create them stops the start. Also resolves
    /// the MFA key when <see cref="LocalAuthOptions.MfaKeyRef"/> is set.
    /// </summary>
    internal sealed class LocalAuthStateProvisioner : IHostedService
    {
        private readonly ILocalAuthStateStore _store;
        private readonly LocalMfaService _mfa;

        public LocalAuthStateProvisioner(ILocalAuthStateStore store, LocalMfaService mfa)
        {
            _store = store;
            _mfa = mfa;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _store.EnsureProvisionedAsync(cancellationToken).ConfigureAwait(false);
            // A configured MFA key that cannot be resolved stops the start, not the first enrollment.
            await _mfa.EnsureKeyAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
