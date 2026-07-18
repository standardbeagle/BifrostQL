using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BifrostQL.Server.Grpc
{
    /// <summary>
    /// The gRPC HTTP/2 front door as an <see cref="IProtocolAdapter"/> — OPT-IN (only present when
    /// the host calls <c>AddBifrostGrpc</c>) and validated FAIL-FAST at startup. The per-call work is
    /// done by the dynamically-registered service methods and the reflection service mapped via
    /// <c>MapBifrostGrpc</c>; this adapter owns the front-door lifecycle and the startup guard.
    ///
    /// <para><b>Fail fast.</b> <see cref="StartAsync"/> rejects a misconfigured port/TLS/stream bound
    /// and forces the descriptor set to build (surfacing a PK-less table or any schema fault as a
    /// startup abort). It does not swallow these: a thrown exception aborts host startup, so the
    /// adapter can never come up half-configured. A bind failure on the configured HTTP/2 port is
    /// surfaced by Kestrel itself (the listener is configured in <c>AddBifrostGrpc</c>) and likewise
    /// aborts startup.</para>
    /// </summary>
    internal sealed class GrpcWireAdapter : IProtocolAdapter
    {
        private readonly GrpcWireOptions _options;
        private readonly GrpcContractProvider _contracts;
        private readonly ILogger<GrpcWireAdapter> _logger;

        public GrpcWireAdapter(
            GrpcWireOptions options,
            GrpcContractProvider contracts,
            ILogger<GrpcWireAdapter>? logger = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _contracts = contracts ?? throw new ArgumentNullException(nameof(contracts));
            _logger = logger ?? NullLogger<GrpcWireAdapter>.Instance;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            ValidateConfiguration();

            // Force the descriptor build now so a schema fault (e.g. a table with no primary key)
            // aborts startup with a precise GrpcSchemaException rather than failing the first RPC.
            _contracts.EnsureBuilt();

            _logger.LogWarning(
                "gRPC front door on port {Port} started WITHOUT bearer identity extraction (slice 4) — an " +
                "unauthenticated call resolves to an empty, fail-closed identity (scoped to nothing), never " +
                "unfiltered data. Writes and auth remain unavailable until later slices.",
                _options.Port);

            _logger.LogInformation(
                "gRPC front door ready on port {Port} (endpoint: {Endpoint}, TLS: {Tls}, max stream rows: {MaxRows}).",
                _options.Port, _options.Endpoint ?? "(default)", _options.RequireTls, _options.MaxStreamRows);

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private void ValidateConfiguration()
        {
            if (_options.Port is < 1 or > 65535)
                throw new GrpcConfigurationException(
                    $"gRPC front door port {_options.Port} is out of range; expected 1..65535.");

            if (_options.MaxStreamRows < 1)
                throw new GrpcConfigurationException(
                    $"gRPC MaxStreamRows must be positive; got {_options.MaxStreamRows}.");

            if (_options.ListPageSize < 1)
                throw new GrpcConfigurationException(
                    $"gRPC ListPageSize must be positive; got {_options.ListPageSize}.");

            if (_options.RequireTls)
            {
                if (string.IsNullOrWhiteSpace(_options.TlsCertificatePath))
                    throw new GrpcConfigurationException(
                        "gRPC RequireTls is set but no TlsCertificatePath was configured.");
                if (!File.Exists(_options.TlsCertificatePath))
                    throw new GrpcConfigurationException(
                        $"gRPC TLS certificate file not found: {_options.TlsCertificatePath}.");
            }
        }
    }
}
