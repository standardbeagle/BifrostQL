using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BifrostQL.Server.Resp
{
    /// <summary>
    /// The Redis RESP-protocol front door as an <see cref="IProtocolAdapter"/>. Per-connection
    /// work is done by <see cref="RespConnectionHandler"/> bound onto a Kestrel TCP listener
    /// (see <c>AddBifrostResp</c>); this adapter owns the front-door lifecycle and the
    /// startup-time configuration guard.
    ///
    /// <para><b>Fail fast / fail closed.</b> The credential store is a hard DI dependency, so
    /// a front door with <see cref="RespWireOptions.RequireAuthentication"/> set cannot come
    /// up without an identity source — there is no anonymous default. Clearing that flag is
    /// the deliberate anonymous opt-in and is logged as a warning at startup. Note RESP has no
    /// STARTTLS: AUTH crosses the wire in the clear unless the deployment terminates TLS at the
    /// listener or a proxy — the same operational contract as a real Redis.</para>
    /// </summary>
    public sealed class RespWireAdapter : IProtocolAdapter
    {
        private readonly RespWireOptions _options;
        private readonly IRespCredentialStore _credentials;
        private readonly ILogger<RespWireAdapter> _logger;

        public RespWireAdapter(
            RespWireOptions options,
            IRespCredentialStore credentials,
            ILogger<RespWireAdapter>? logger = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
            _logger = logger ?? NullLogger<RespWireAdapter>.Instance;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (!_options.RequireAuthentication)
                _logger.LogWarning(
                    "resp front door on port {Port} started with authentication DISABLED — commands run " +
                    "without an established identity. Enable RequireAuthentication unless this is deliberate.",
                    _options.Port);

            _logger.LogInformation(
                "resp front door ready on port {Port} (auth required: {RequireAuth}, endpoint: {Endpoint}).",
                _options.Port, _options.RequireAuthentication, _options.Endpoint ?? "(default)");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
