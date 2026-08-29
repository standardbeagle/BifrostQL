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
    /// the deliberate anonymous opt-in and is logged as a warning at startup. Credentials are
    /// never accepted over a cleartext transport: AUTH requires TLS
    /// (<see cref="RespWireOptions.ServerCertificate"/>) or the explicit development override
    /// (<see cref="RespWireOptions.AllowCleartextAuth"/>, default off, startup warning). An
    /// auth-required front door with neither fails to START — it cannot authenticate anyone,
    /// so coming up would only invite credentials across the wire in the clear.</para>
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

            // The transport gate refuses every credentialed AUTH without TLS, so this
            // configuration could not authenticate anyone — fail fast instead of bringing
            // up a door that invites cleartext passwords it will only refuse.
            if (_options.RequireAuthentication && _options.ServerCertificate is null && !_options.AllowCleartextAuth)
                throw new InvalidOperationException(
                    "RespWireOptions: an auth-required RESP front door needs a confidential transport. " +
                    "Set ServerCertificate to terminate TLS on the listener, or set AllowCleartextAuth " +
                    "to explicitly accept cleartext credentials (development only, behind a " +
                    "TLS-terminating proxy). Refusing to start with credentials neither protected nor accepted.");

            if (_options.AllowCleartextAuth)
                _logger.LogWarning(
                    "resp: AllowCleartextAuth is ON — passwords may cross the wire in the clear. This is a " +
                    "development-only override for loopback use behind a TLS-terminating proxy; turn it off " +
                    "(and configure ServerCertificate) for any real deployment.");

            if (_options.EnableWrites)
                _logger.LogWarning(
                    "resp front door on port {Port} started with WRITE commands ENABLED (SET/HSET/DEL) — " +
                    "row mutations route through the mutation pipeline under the session identity. This is a " +
                    "deliberate opt-in; leave EnableWrites off unless writes are intended.",
                    _options.Port);

            _logger.LogInformation(
                "resp front door ready on port {Port} (auth required: {RequireAuth}, writes enabled: {Writes}, endpoint: {Endpoint}).",
                _options.Port, _options.RequireAuthentication, _options.EnableWrites, _options.Endpoint ?? "(default)");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
