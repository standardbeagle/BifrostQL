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
    /// <para><b>Full contract vs. reflection projection.</b> <see cref="EnsureBuiltAsync"/> reconciles the
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
        private readonly Lazy<Task<Built>> _build;
        private volatile Built? _built;

        public GrpcContractProvider(IQueryIntentExecutor executor, GrpcWireOptions options)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _build = new Lazy<Task<Built>>(BuildAsync, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        private sealed record Built(IDbModel Model, GrpcFieldNumberManifest FullManifest, GrpcContract FullContract);

        /// <summary>
        /// Resolves the endpoint model, reconciles the full manifest, and builds the full dispatch
        /// contract. Idempotent: every caller awaits the one shared build. Throws
        /// <see cref="GrpcSchemaException"/> on any schema fault (e.g. a table with no primary key),
        /// so startup fails fast rather than serving a broken contract. The token abandons only this
        /// caller's wait, never the shared build.
        /// </summary>
        public Task EnsureBuiltAsync(CancellationToken cancellationToken)
            => _build.Value.WaitAsync(cancellationToken);

        private async Task<Built> BuildAsync()
        {
            var model = await _executor.GetModelAsync(_options.Endpoint);
            var fullVisible = GrpcSchemaVisibility.ProjectAll(model);
            var fullManifest = _options.Manifest.Reconcile(fullVisible);
            // Include the write RPCs (for allow-listed tables) in the full dispatch contract only
            // when writes are globally enabled — otherwise the mutation surface is absent entirely.
            var fullContract = GrpcSchemaGenerator.BuildContract(fullVisible, fullManifest, _options.EnableWrites);
            var built = new Built(model, fullManifest, fullContract);
            _built = built;
            return built;
        }

        /// <summary>
        /// The built contract. <c>MapBifrostGrpcAsync</c> awaits the build before gRPC asks for the
        /// method set, so every reader runs after it. Reaching here first is a host wiring defect,
        /// reported as one rather than covered by a blocking model load.
        /// </summary>
        private Built RequireBuilt() => _built ?? throw new InvalidOperationException(
            "The gRPC contract has not been built. Map the gRPC routes with "
            + "'await app.MapBifrostGrpcAsync()', which loads the model before mapping.");

        public IDbModel Model => RequireBuilt().Model;

        /// <summary>The full dispatch contract (all tables/columns), keyed by field numbers the whole surface shares.</summary>
        public GrpcContract FullContract => RequireBuilt().FullContract;

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
            var built = RequireBuilt();
            // Reflection mirrors dispatch: the mutation RPCs appear in a caller's reflected surface
            // only when writes are globally enabled and the table is allow-listed (and visible).
            return GrpcSchemaGenerator.Generate(built.Model, built.FullManifest, userContext, _options.EnableWrites);
        }
    }
}
