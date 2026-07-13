using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// The PostgreSQL wire-protocol front door as an <see cref="IProtocolAdapter"/>.
    /// Per-connection work is done by <see cref="PgConnectionHandler"/> bound onto a
    /// Kestrel TCP listener (see <c>AddBifrostPgwire</c>); this adapter owns the
    /// front-door lifecycle and the startup-time configuration guard.
    ///
    /// <para><b>Fail fast.</b> <see cref="StartAsync"/> throws when no server
    /// certificate is configured, aborting host startup rather than bringing the port
    /// up in a state where it would answer 'N' to every SSLRequest and invite
    /// credentials across the wire in the clear. The credential store is a hard DI
    /// dependency for the same reason — there is no anonymous default.</para>
    /// </summary>
    public sealed class PgWireAdapter : IProtocolAdapter
    {
        private readonly PgWireOptions _options;
        private readonly IPgCredentialStore _credentials;
        private readonly ILogger<PgWireAdapter> _logger;

        public PgWireAdapter(
            PgWireOptions options,
            IPgCredentialStore credentials,
            ILogger<PgWireAdapter>? logger = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
            _logger = logger ?? NullLogger<PgWireAdapter>.Instance;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_options.ServerCertificate is null)
                throw new InvalidOperationException(
                    "PgWireOptions.ServerCertificate is required: the pgwire front door refuses to " +
                    "start without TLS, so credentials are never invited across the wire in the clear.");

            _logger.LogInformation(
                "pgwire front door ready on port {Port} (auth: {AuthMethod}, endpoint: {Endpoint}).",
                _options.Port, _options.AuthMethod, _options.Endpoint ?? "(default)");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
