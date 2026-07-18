using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Server.Grpc
{
    /// <summary>
    /// Resolves — once — the schema artifacts the runtime gRPC front door dispatches against, and
    /// serves per-identity reflection projections. Both the dynamic dispatch method set and the
    /// identity-filtered reflection service must agree on every field number; this provider is the
    /// single place the shared full field-number manifest is reconciled, so they cannot drift.
    ///
    /// <para><b>Full contract vs. reflection projection.</b> <see cref="EnsureBuilt"/> reconciles the
    /// configured manifest against the WHOLE model (<see cref="GrpcSchemaVisibility.ProjectAll"/>) to
    /// pin every column's number and to enumerate the Get/List/Stream routes. Because the manifest is
    /// full, a later per-identity reflection projection (<see cref="Generate"/>, which filters via the
    /// authoritative read policy) keeps the identical numbers for every column it still shows. This is
    /// also the fail-fast descriptor gate: a PK-less table throws <see cref="GrpcSchemaException"/>
    /// here at startup, aborting host start.</para>
    /// </summary>
    internal sealed class GrpcContractProvider
    {
        private readonly IQueryIntentExecutor _executor;
        private readonly GrpcWireOptions _options;
        private readonly object _gate = new();
        private volatile Built? _built;

        public GrpcContractProvider(IQueryIntentExecutor executor, GrpcWireOptions options)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        private sealed record Built(IDbModel Model, GrpcFieldNumberManifest FullManifest, GrpcContract FullContract);

        /// <summary>
        /// Resolves the endpoint model, reconciles the full manifest, and builds the full dispatch
        /// contract — idempotent and cached. Throws <see cref="GrpcSchemaException"/> on any schema
        /// fault (e.g. a table with no primary key), so startup fails fast rather than serving a
        /// broken contract.
        /// </summary>
        public void EnsureBuilt()
        {
            if (_built is not null) return;
            lock (_gate)
            {
                if (_built is not null) return;

                // Resolving the cached model is synchronous work behind an async API; blocking here at
                // startup/first-build is acceptable and keeps the method-provider discovery hook sync.
                var model = _executor.GetModelAsync(_options.Endpoint).GetAwaiter().GetResult();
                var fullVisible = GrpcSchemaVisibility.ProjectAll(model);
                var fullManifest = _options.Manifest.Reconcile(fullVisible);
                var fullContract = GrpcSchemaGenerator.BuildContract(fullVisible, fullManifest);
                _built = new Built(model, fullManifest, fullContract);
            }
        }

        public IDbModel Model { get { EnsureBuilt(); return _built!.Model; } }

        /// <summary>The full dispatch contract (all tables/columns), keyed by field numbers the whole surface shares.</summary>
        public GrpcContract FullContract { get { EnsureBuilt(); return _built!.FullContract; } }

        /// <summary>The service name every generated RPC lives under (e.g. <c>bifrostql.BifrostQuery</c>).</summary>
        public string ServiceFullName => $"{GrpcSchemaGenerator.Package}.{GrpcSchemaGenerator.ServiceName}";

        /// <summary>
        /// The schema artifacts VISIBLE to <paramref name="userContext"/> — filtered by the same read
        /// policy the query path enforces, numbered from the shared full manifest. This is what
        /// reflection serves, so a denied table/column is absent from what a caller can discover
        /// (invariant 4) while its field numbers stay stable for callers who can see it.
        /// </summary>
        public GrpcSchemaArtifacts Generate(IDictionary<string, object?> userContext)
        {
            EnsureBuilt();
            return GrpcSchemaGenerator.Generate(_built!.Model, _built!.FullManifest, userContext);
        }
    }
}
