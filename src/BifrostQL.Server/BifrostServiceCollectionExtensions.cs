using GraphQL.Types;
using BifrostQL.Model;
using BifrostQL;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using GraphQL;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using BifrostQL.Server;
using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using BifrostQL.Core.Model;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.Modules.Validation;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Core.Storage;
using BifrostQL.Core.Workflows;
using BifrostQL.Server.Logging;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server
{
    public static class BifrostServiceCollectionExtensions
    {
        /// <summary>
        /// Adds the BifrostQL info endpoint and server header middleware.
        /// Call before UseBifrostQL/UseBifrostEndpoints in the pipeline.
        /// </summary>
        public static IApplicationBuilder UseBifrostInfo(this IApplicationBuilder app, Action<BifrostInfoOptions>? configure = null)
        {
            var options = new BifrostInfoOptions();
            configure?.Invoke(options);

            // Register the server clock singleton if not already registered
            var clock = app.ApplicationServices.GetService<BifrostServerClock>();
            if (clock == null)
            {
                throw new InvalidOperationException(
                    "BifrostServerClock not registered. Call AddBifrostInfo() in service configuration.");
            }

            app.UseMiddleware<BifrostHeaderMiddleware>();

            if (options.Enabled)
            {
                app.UseMiddleware<BifrostInfoMiddleware>(options);
            }

            return app;
        }

        /// <summary>
        /// Registers required services for the BifrostQL info endpoint.
        /// Call during service configuration (before Build).
        /// </summary>
        public static IServiceCollection AddBifrostInfo(this IServiceCollection services)
        {
            services.AddSingleton<BifrostServerClock>();
            return services;
        }

        /// <summary>
        /// Maps the app-metadata overlay endpoint, which serves the loaded
        /// <see cref="BifrostQL.Core.AppMetadata.AppMetadataModel"/> as the stable
        /// camelCase JSON contract for SPA and React Native clients. The endpoint
        /// returns an empty overlay when none has been registered via
        /// <c>AddBifrostAppMetadata</c>, so it always serves the stable contract.
        /// </summary>
        public static IApplicationBuilder UseBifrostAppMetadata(
            this IApplicationBuilder app,
            Action<BifrostAppMetadataOptions>? configure = null)
        {
            var options = new BifrostAppMetadataOptions();
            configure?.Invoke(options);

            if (options.Enabled)
                app.UseMiddleware<BifrostAppMetadataMiddleware>(options);

            return app;
        }

        /// <summary>
        /// Registers local DB-backed user login for self-hosted deployments. The app-user
        /// table lives in the same database BifrostQL serves, reached through a server-side
        /// <see cref="IDbConnFactory"/> built from <paramref name="connectionString"/> — the
        /// database credentials never reach the client. Adds cookie authentication and the
        /// <see cref="BifrostQL.Server.Auth.LocalUserStore"/>; pair with
        /// <c>UseBifrostLocalAuth()</c> to map the login/logout endpoints.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="connectionString">
        /// Connection string for the database holding the app-user rows. Typically the same
        /// <c>bifrost</c> connection string the GraphQL endpoint uses.
        /// </param>
        /// <param name="configure">Optional table/column and endpoint-path configuration.</param>
        public static IServiceCollection AddBifrostLocalAuth(
            this IServiceCollection services,
            string connectionString,
            Action<BifrostQL.Server.Auth.LocalAuthOptions>? configure = null)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("A connection string is required for local auth.", nameof(connectionString));

            var options = new BifrostQL.Server.Auth.LocalAuthOptions();
            configure?.Invoke(options);

            var connFactory = DbConnFactoryResolver.Create(connectionString);

            services.AddSingleton(options);
            services.AddSingleton(connFactory);
            services.AddSingleton(sp => new BifrostQL.Server.Auth.LocalUserStore(
                sp.GetRequiredService<IDbConnFactory>(),
                sp.GetRequiredService<BifrostQL.Server.Auth.LocalAuthOptions>()));

            services
                .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(cookieOptions =>
                {
                    cookieOptions.LoginPath = options.LoginPath;
                    cookieOptions.LogoutPath = options.LogoutPath;
                });

            return services;
        }

        /// <summary>
        /// Registers OIDC claim mappers so authenticated Microsoft 365 and Google principals
        /// normalize to the same <see cref="Core.Auth.AppIdentity"/> contract local auth
        /// produces. Each mapper is keyed by the issuer URL of the OIDC provider it maps;
        /// <c>UseUiAuth()</c> selects a mapper by the principal's <c>iss</c> claim and
        /// re-issues the cookie in the shared local-auth claim shape. Pair with the
        /// <c>AddOpenIdConnect("oauth2", ...)</c> wiring already configured by AddBifrostQL.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">
        /// Registers issuer-to-mapper pairs (e.g. the Entra tenant authority to a
        /// <see cref="BifrostQL.Server.Auth.Microsoft365ClaimMapper"/>, Google's issuer to a
        /// <see cref="BifrostQL.Server.Auth.GoogleClaimMapper"/>).
        /// </param>
        public static IServiceCollection AddBifrostOidcClaimMappers(
            this IServiceCollection services,
            Action<BifrostQL.Server.Auth.OidcClaimMapperBuilder> configure)
        {
            if (configure == null)
                throw new ArgumentException("A mapper configuration callback is required.", nameof(configure));

            var builder = new BifrostQL.Server.Auth.OidcClaimMapperBuilder();
            configure(builder);

            services.AddSingleton(new BifrostQL.Server.Auth.OidcClaimMapperRegistry(builder.Build()));
            return services;
        }

        public static IServiceCollection AddBifrostQL(this IServiceCollection services, Action<BifrostSetupOptions> optionSetter)
        {
            var options = new BifrostSetupOptions();
            optionSetter(options);
            options.ConfigureServices(services);
            return services;
        }

        public static IApplicationBuilder UseBifrostQL(this IApplicationBuilder app)
        {
            var options = app.ApplicationServices.GetService<BifrostSetupOptions>();
            if (options == null) throw new InvalidOperationException("BifrostSetupOptions not configured. Call AddBifrostQL before UseBifrostQL");
            var useAuth = options.IsUsingAuth;
            var endpointPath = options.EndpointPath;
            var playgroundPath = options.PlaygroundPath;


            app.IfFluent(useAuth, a => a.UseAuthentication().UseCookiePolicy());
            app.IfFluent(useAuth, a => a.UseUiAuth());
            app.Map(endpointPath, branch => branch.UseMiddleware<BifrostHttpMiddleware>());
            app.UseGraphQLGraphiQL(playgroundPath,
                new GraphQL.Server.Ui.GraphiQL.GraphiQLOptions
                {
                    GraphQLEndPoint = endpointPath,
                    SubscriptionsEndPoint = endpointPath,
                    RequestCredentials = GraphQL.Server.Ui.GraphiQL.RequestCredentials.SameOrigin,
                });
            return app;
        }

        /// <summary>
        /// Registers multiple BifrostQL database endpoints. Each endpoint serves a different
        /// database at its own GraphQL path with independent configuration.
        /// </summary>
        public static IServiceCollection AddBifrostEndpoints(this IServiceCollection services, Action<BifrostMultiDbOptions> optionSetter)
        {
            var options = new BifrostMultiDbOptions();
            optionSetter(options);
            options.ConfigureServices(services);
            return services;
        }

        /// <summary>
        /// Maps all registered BifrostQL database endpoints to their configured paths.
        /// Call after AddBifrostEndpoints in the service configuration.
        /// </summary>
        public static IApplicationBuilder UseBifrostEndpoints(this IApplicationBuilder app)
        {
            var options = app.ApplicationServices.GetService<BifrostMultiDbOptions>();
            if (options == null) throw new InvalidOperationException("BifrostMultiDbOptions not configured. Call AddBifrostEndpoints before UseBifrostEndpoints");

            if (options.IsUsingAuth)
            {
                app.UseAuthentication().UseCookiePolicy();
                app.UseUiAuth();
            }

            foreach (var endpoint in options.Endpoints)
            {
                app.Map(endpoint.Path, branch => branch.UseMiddleware<BifrostHttpMiddleware>());
                app.UseGraphQLGraphiQL(endpoint.PlaygroundPath,
                    new GraphQL.Server.Ui.GraphiQL.GraphiQLOptions
                    {
                        GraphQLEndPoint = endpoint.Path,
                        SubscriptionsEndPoint = endpoint.Path,
                        RequestCredentials = GraphQL.Server.Ui.GraphiQL.RequestCredentials.SameOrigin,
                    });
            }
            return app;
        }

        /// <summary>
        /// Maps the BifrostQL binary WebSocket endpoint at the specified path.
        /// Clients connect via WebSocket and exchange protobuf-encoded binary frames.
        /// Large responses are automatically chunked with CRC32 integrity checksums
        /// and backpressure via ACK windowing.
        /// Requires AddBifrostEngine() in service configuration and UseWebSockets() before this call.
        /// </summary>
        /// <param name="app">The application builder.</param>
        /// <param name="path">The WebSocket endpoint path (e.g., "/bifrost-ws").</param>
        /// <param name="chunkThreshold">Payload size threshold for chunking (default 64 KB).</param>
        /// <param name="ackWindow">Maximum unacknowledged chunks before backpressure pauses sending (default 8).</param>
        /// <returns>The application builder for chaining.</returns>
        public static IApplicationBuilder UseBifrostBinary(
            this IApplicationBuilder app,
            string path = "/bifrost-ws",
            int chunkThreshold = ChunkSender.DefaultChunkThreshold,
            int ackWindow = ChunkSender.DefaultAckWindow)
        {
            var engine = app.ApplicationServices.GetRequiredService<IBifrostEngine>();
            app.Map(path, branch =>
                branch.UseMiddleware<BifrostBinaryMiddleware>(engine, path, chunkThreshold, ackWindow));
            return app;
        }

        public static FluentT IfFluent<FluentT, ResultT>(this FluentT fluent, bool check, Func<FluentT, ResultT> doConfig)
            where ResultT : FluentT
        {
            if (!check) return fluent;
            return doConfig(fluent);
        }

        /// <summary>
        /// Prepends the built-in filter transformers to the caller-supplied set so
        /// that filtering metadata is enforced on the query path without explicit
        /// opt-in. <see cref="PolicyFilterTransformer"/>, <see cref="TenantFilterTransformer"/>,
        /// <see cref="SoftDeleteFilterTransformer"/>, and <see cref="AutoFilterTransformer"/>
        /// are each metadata-driven and a no-op for tables lacking their respective
        /// metadata key, so always registering them is safe. This closes a security
        /// footgun where <c>tenant-filter</c> metadata silently did nothing unless the
        /// host also registered the matching transformer by hand.
        ///
        /// A caller-supplied instance of the same type (e.g. a
        /// <see cref="PolicyFilterTransformer"/> configured with a non-default admin
        /// role) takes precedence and suppresses the built-in for that type. Per-profile
        /// opt-out is handled downstream by <see cref="BifrostProfileRegistry.FilterBy(IFilterTransformers, BifrostProfile)"/>
        /// since each built-in implements <see cref="IModuleNamed"/>.
        /// </summary>
        /// <summary>
        /// Appends instances of the supplied <paramref name="types"/>, resolved from
        /// <paramref name="sp"/>, to the caller-configured collection. Backs the generic
        /// <c>AddFilterTransformer&lt;T&gt;</c>/<c>AddMutationTransformer&lt;T&gt;</c>/<c>AddQueryObserver&lt;T&gt;</c>
        /// overloads, which are additive over the collection/factory overloads. Returns the
        /// original collection unchanged when no generic types were registered.
        /// </summary>
        internal static IReadOnlyCollection<T> ResolveTransformers<T>(
            IReadOnlyCollection<T> configured,
            IReadOnlyList<Type> types,
            IServiceProvider sp)
        {
            if (types.Count == 0)
                return configured;

            var combined = new List<T>(configured);
            foreach (var type in types)
                combined.Add((T)sp.GetRequiredService(type));
            return combined;
        }

        internal static IReadOnlyCollection<IFilterTransformer> WithBuiltInFilterTransformers(
            IReadOnlyCollection<IFilterTransformer> configured)
        {
            var combined = new List<IFilterTransformer>();

            if (!configured.Any(t => t is PolicyFilterTransformer))
                combined.Add(new PolicyFilterTransformer());

            if (!configured.Any(t => t is TenantFilterTransformer))
                combined.Add(new TenantFilterTransformer());

            if (!configured.Any(t => t is SoftDeleteFilterTransformer))
                combined.Add(new SoftDeleteFilterTransformer());

            if (!configured.Any(t => t is AutoFilterTransformer))
                combined.Add(new AutoFilterTransformer());

            combined.AddRange(configured);
            return combined;
        }

        /// <summary>
        /// Prepends the built-in security mutation transformers to the
        /// caller-supplied set. <see cref="PolicyMutationTransformer"/> is
        /// always active so authorization-policy metadata is enforced on the
        /// create/update/delete path without explicit opt-in; it is opt-in per
        /// table via the <c>policy-*</c> metadata keys and a no-op for tables
        /// without them. A caller-supplied <see cref="PolicyMutationTransformer"/>
        /// (e.g. one configured with a non-default admin role) takes precedence.
        /// </summary>
        internal static IReadOnlyCollection<IMutationTransformer> WithBuiltInMutationTransformers(
            IReadOnlyCollection<IMutationTransformer> configured,
            IServiceProvider? services = null)
        {
            var combined = new List<IMutationTransformer>();

            if (!configured.Any(t => t is PolicyMutationTransformer))
                combined.Add(new PolicyMutationTransformer());

            if (!configured.Any(t => t is StateMachineMutationTransformer))
                combined.Add(new StateMachineMutationTransformer());

            if (!configured.Any(t => t is EnumValueMutationTransformer))
                combined.Add(new EnumValueMutationTransformer());

            if (!configured.Any(t => t is ExtendedServerValidationTransformer))
                combined.Add(new ExtendedServerValidationTransformer(
                    services?.GetServices<IServerValidationProvider>() ?? Array.Empty<IServerValidationProvider>()));

            // Soft delete is one feature split across two transformers: the filter
            // hides soft-deleted rows, this mutation rewrites DELETE into an UPDATE of
            // the soft-delete column. Auto-registering only the filter (see
            // WithBuiltInFilterTransformers) would leave DELETEs hard — incoherent. A
            // no-op for tables without the soft-delete metadata key, so always safe.
            if (!configured.Any(t => t is SoftDeleteMutationTransformer))
                combined.Add(new SoftDeleteMutationTransformer());

            // Audit-column population (created/updated/deleted on/by). Keys off
            // per-column "populate" metadata plus the model-level user-audit-key, so
            // it is a no-op for tables without audit columns and always safe to
            // auto-register. A caller-supplied instance takes precedence.
            if (!configured.Any(t => t is AuditMutationTransformer))
                combined.Add(new AuditMutationTransformer());

            combined.AddRange(configured);
            return combined;
        }
    }

    /// <summary>
    /// Configuration for a single BifrostQL database endpoint.
    /// </summary>
    public sealed class BifrostEndpointConfig
    {
        /// <summary>
        /// Database connection string for this endpoint.
        /// </summary>
        public string ConnectionString { get; set; } = null!;

        /// <summary>
        /// GraphQL endpoint path (e.g., "/graphql/sales").
        /// </summary>
        public string Path { get; set; } = "/graphql";

        /// <summary>
        /// GraphiQL playground path (e.g., "/graphiql/sales").
        /// </summary>
        public string PlaygroundPath { get; set; } = "/";

        /// <summary>
        /// Metadata rules for this endpoint's database (e.g., tenant filters, soft-delete).
        /// </summary>
        public IReadOnlyCollection<string> Metadata { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Whether authentication is disabled for this endpoint.
        /// </summary>
        public bool DisableAuth { get; set; } = true;

        /// <summary>
        /// Database provider/dialect for this endpoint (e.g., "sqlserver", "postgresql", "mysql", "sqlite").
        /// When null, the provider is auto-detected from the connection string.
        /// </summary>
        public string? Provider { get; set; }

        /// <summary>
        /// Additional metadata sources for this endpoint, merged in priority order.
        /// File metadata (from Metadata property) has lowest priority; later sources override.
        /// </summary>
        public IReadOnlyList<IMetadataSource> MetadataSources { get; set; } = Array.Empty<IMetadataSource>();
    }

    /// <summary>
    /// Options for registering multiple BifrostQL database endpoints.
    /// Each endpoint serves a separate database at its own GraphQL path.
    /// </summary>
    public sealed class BifrostMultiDbOptions
    {
        private readonly List<BifrostEndpointConfig> _endpoints = new();
        private IConfigurationSection? _jwtConfig;
        private IReadOnlyCollection<IFilterTransformer> _filterTransformers = Array.Empty<IFilterTransformer>();
        private Func<IServiceProvider, IReadOnlyCollection<IFilterTransformer>>? _filterTransformerLoader;
        private IReadOnlyCollection<IMutationTransformer> _mutationTransformers = Array.Empty<IMutationTransformer>();
        private Func<IServiceProvider, IReadOnlyCollection<IMutationTransformer>>? _mutationTransformerLoader;
        private IReadOnlyCollection<IQueryObserver> _queryObservers = Array.Empty<IQueryObserver>();
        private Func<IServiceProvider, IReadOnlyCollection<IQueryObserver>>? _queryObserverLoader;
        // Types registered via the generic AddXTransformer<T>()/AddQueryObserver<T>() overloads;
        // resolved from DI at build time and appended to the collection/factory overloads.
        private readonly List<Type> _filterTransformerTypes = new();
        private readonly List<Type> _mutationTransformerTypes = new();
        private readonly List<Type> _queryObserverTypes = new();
        private IConfigurationSection? _loggingConfig;
        private readonly BifrostProfileRegistry _profileRegistry = new();

        /// <summary>
        /// The configured endpoints. Available after configuration is complete.
        /// </summary>
        public IReadOnlyList<BifrostEndpointConfig> Endpoints => _endpoints;

        /// <summary>
        /// Whether any endpoint has authentication enabled.
        /// </summary>
        public bool IsUsingAuth => _endpoints.Any(e => !e.DisableAuth);

        /// <summary>
        /// Adds a database endpoint with the specified configuration.
        /// </summary>
        public BifrostMultiDbOptions AddEndpoint(Action<BifrostEndpointConfig> configure)
        {
            var config = new BifrostEndpointConfig();
            configure(config);

            if (string.IsNullOrWhiteSpace(config.ConnectionString))
                throw new ArgumentException("ConnectionString is required for each endpoint.");
            if (string.IsNullOrWhiteSpace(config.Path))
                throw new ArgumentException("Path is required for each endpoint.");

            if (_endpoints.Any(e => string.Equals(e.Path, config.Path, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"An endpoint is already registered at path '{config.Path}'.");

            _endpoints.Add(config);
            return this;
        }

        /// <summary>
        /// Configures JWT authentication settings shared across all authenticated endpoints.
        /// </summary>
        public BifrostMultiDbOptions BindJwtSettings(IConfigurationSection? section)
        {
            if (section != null && section.Exists())
                _jwtConfig = section;
            return this;
        }

        /// <summary>
        /// Configures logging settings shared across all endpoints.
        /// </summary>
        public BifrostMultiDbOptions BindLogging(IConfigurationSection? section)
        {
            _loggingConfig = section;
            return this;
        }

        public BifrostMultiDbOptions AddFilterTransformers(IReadOnlyCollection<IFilterTransformer> transformers)
        {
            _filterTransformers = transformers;
            return this;
        }

        public BifrostMultiDbOptions AddFilterTransformers(Func<IServiceProvider, IReadOnlyCollection<IFilterTransformer>>? loader)
        {
            _filterTransformerLoader = loader;
            return this;
        }

        /// <summary>
        /// Registers a single filter transformer of type <typeparamref name="T"/>, resolved
        /// from DI at build time. Additive over the collection/factory overloads.
        /// </summary>
        public BifrostMultiDbOptions AddFilterTransformer<T>() where T : class, IFilterTransformer
        {
            if (!_filterTransformerTypes.Contains(typeof(T)))
                _filterTransformerTypes.Add(typeof(T));
            return this;
        }

        public BifrostMultiDbOptions AddMutationTransformers(IReadOnlyCollection<IMutationTransformer> transformers)
        {
            _mutationTransformers = transformers;
            return this;
        }

        public BifrostMultiDbOptions AddMutationTransformers(Func<IServiceProvider, IReadOnlyCollection<IMutationTransformer>>? loader)
        {
            _mutationTransformerLoader = loader;
            return this;
        }

        /// <summary>
        /// Registers a single mutation transformer of type <typeparamref name="T"/>, resolved
        /// from DI at build time. Additive over the collection/factory overloads.
        /// </summary>
        public BifrostMultiDbOptions AddMutationTransformer<T>() where T : class, IMutationTransformer
        {
            if (!_mutationTransformerTypes.Contains(typeof(T)))
                _mutationTransformerTypes.Add(typeof(T));
            return this;
        }

        public BifrostMultiDbOptions AddQueryObservers(IReadOnlyCollection<IQueryObserver> observers)
        {
            _queryObservers = observers;
            return this;
        }

        public BifrostMultiDbOptions AddQueryObservers(Func<IServiceProvider, IReadOnlyCollection<IQueryObserver>>? loader)
        {
            _queryObserverLoader = loader;
            return this;
        }

        /// <summary>
        /// Registers a single query observer of type <typeparamref name="T"/>, resolved from
        /// DI at build time. Additive over the collection/factory overloads.
        /// </summary>
        public BifrostMultiDbOptions AddQueryObserver<T>() where T : class, IQueryObserver
        {
            if (!_queryObserverTypes.Contains(typeof(T)))
                _queryObserverTypes.Add(typeof(T));
            return this;
        }

        /// <summary>
        /// Adds a named configuration profile that controls which modules are active.
        /// </summary>
        public BifrostMultiDbOptions AddProfile(BifrostProfile profile)
        {
            _profileRegistry.Add(profile);
            return this;
        }

        /// <summary>
        /// Binds profiles from a configuration section (e.g., "BifrostQL:Profiles").
        /// Each child section is a profile name with "modules" array and optional "requireRole".
        /// </summary>
        public BifrostMultiDbOptions BindProfiles(IConfigurationSection? section)
        {
            if (section == null || !section.Exists())
                return this;

            foreach (var child in section.GetChildren())
            {
                var modules = child.GetSection("modules").Get<string[]>();
                _profileRegistry.Add(new BifrostProfile
                {
                    Name = child.Key,
                    Modules = modules,
                    RequireRole = child.GetValue<string>("requireRole"),
                });
            }
            return this;
        }

        internal void ConfigureServices(IServiceCollection services)
        {
            if (_endpoints.Count == 0)
                throw new InvalidOperationException("At least one endpoint must be configured. Call AddEndpoint.");

            var extensionsLoader = new PathCache<Inputs>();
            // Always pass the registry so a runtime ReplaceAll is honored on the next
            // schema rebuild even when it starts empty (resolves to the raw default).
            var registry = _profileRegistry;
            foreach (var endpoint in _endpoints)
            {
                var connStr = endpoint.ConnectionString;
                var providerName = endpoint.Provider;
                var endpointMetadataRules = endpoint.Metadata as IReadOnlyList<string>
                    ?? endpoint.Metadata.ToArray();
                var metadataSources = endpoint.MetadataSources;
                extensionsLoader.AddLoader(endpoint.Path, async () =>
                {
                    IDictionary<string, IDictionary<string, object?>>? additionalMetadata = null;
                    if (metadataSources.Count > 0)
                    {
                        var composite = new CompositeMetadataSource(metadataSources);
                        additionalMetadata = await composite.LoadTableMetadataAsync();
                    }
                    var provider = string.IsNullOrWhiteSpace(providerName)
                        ? (BifrostDbProvider?)null
                        : DbConnFactoryResolver.ParseProviderName(providerName);
                    var connFactory = DbConnFactoryResolver.Create(connStr, provider);
                    // Read once, then build a model+schema per profile from the shared read.
                    var loader = new DbModelLoader(connFactory, new MetadataLoader(endpointMetadataRules));
                    var read = await loader.ReadAsync();
                    // Pre-load enum lookup values once (async DB read) so the
                    // synchronous per-profile cache can attach the enum map.
                    var baseModel = loader.BuildModel(read, new MetadataLoader(endpointMetadataRules), additionalMetadata);
                    var enumValues = await loader.LoadEnumValuesAsync(baseModel);
                    var profileCache = new ProfileModelCache(
                        loader, read, endpointMetadataRules, additionalMetadata, registry, enumValues);
                    var (model, schema) = profileCache.GetFor(null);
                    return new Inputs(new Dictionary<string, object?>
                    {
                        { "model", model },
                        { "connFactory", connFactory },
                        { "dbSchema", schema },
                        { "profileModelCache", profileCache },
                    });
                });
            }

            services.AddSingleton(this);
            services.AddSingleton(extensionsLoader);
            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();

            // The workflow executor runs sidecar workflow endpoints through the
            // same GraphQL pipeline as a direct /graphql request, so policy and
            // tenant-filter still apply. See the Workflow Mutations guide.
            services.AddSingleton<IBifrostWorkflowExecutor>(sp => new BifrostWorkflowExecutor(
                sp.GetRequiredService<IDocumentExecuter>(),
                sp.GetRequiredService<PathCache<Inputs>>(),
                sp));
            services.AddSingleton<IReadOnlyDictionary<string, WorkflowDefinition>>(
                new Dictionary<string, WorkflowDefinition>(StringComparer.OrdinalIgnoreCase));
            services.AddSingleton<IWorkflowDataExecutor>(sp => sp.GetRequiredService<IBifrostWorkflowExecutor>());
            services.AddSingleton<IWorkflowRunner>(sp => new WorkflowRunner(
                sp.GetRequiredService<IReadOnlyDictionary<string, WorkflowDefinition>>(),
                sp.GetRequiredService<IWorkflowDataExecutor>()));
            services.AddSingleton<WorkflowTriggerHost>();
            services.AddSingleton<WorkflowScheduler>();
            services.AddSingleton<MutationObservers>(sp => new MutationObservers(
                new IMutationObserver[] { sp.GetRequiredService<WorkflowTriggerHost>() }));
            // Before-commit veto hooks: built from every registered
            // IBeforeCommitMutationHook so a host/test can register one. There are
            // no built-in hooks, so the collection is empty unless the host adds one.
            services.AddSingleton<BeforeCommitMutationHooks>(sp => new BeforeCommitMutationHooks(
                sp.GetServices<IBeforeCommitMutationHook>().ToArray()));
            services.AddSingleton<StateTransitionObservers>(sp => new StateTransitionObservers(
                new IStateTransitionObserver[]
                {
                    new StateTransitionAuditObserver(sp.GetRequiredService<IBifrostWorkflowExecutor>()),
                    sp.GetRequiredService<WorkflowTriggerHost>(),
                }));

            // Register unconditionally so a runtime ReplaceAll on this same instance
            // is visible even if it starts empty.
            services.AddSingleton(_profileRegistry);

            foreach (var t in _filterTransformerTypes) services.TryAddSingleton(t);
            services.AddSingleton<IFilterTransformers>(sp => new FilterTransformersWrap
            {
                Transformers = BifrostServiceCollectionExtensions.WithBuiltInFilterTransformers(
                    BifrostServiceCollectionExtensions.ResolveTransformers(
                        _filterTransformerLoader != null ? _filterTransformerLoader(sp) : _filterTransformers,
                        _filterTransformerTypes, sp))
            });

            foreach (var t in _mutationTransformerTypes) services.TryAddSingleton(t);
            services.AddSingleton<IMutationTransformers>(sp => new MutationTransformersWrap
            {
                Transformers = BifrostServiceCollectionExtensions.WithBuiltInMutationTransformers(
                    BifrostServiceCollectionExtensions.ResolveTransformers(
                        _mutationTransformerLoader != null ? _mutationTransformerLoader(sp) : _mutationTransformers,
                        _mutationTransformerTypes, sp), sp)
            });

            foreach (var t in _queryObserverTypes) services.TryAddSingleton(t);
            services.AddSingleton<IQueryObservers>(sp =>
            {
                var userObservers = BifrostServiceCollectionExtensions.ResolveTransformers(
                    _queryObserverLoader != null ? _queryObserverLoader(sp) : _queryObservers,
                    _queryObserverTypes, sp);
                var loggingConfig = sp.GetRequiredService<BifrostLoggingConfiguration>();
                var allObservers = new List<IQueryObserver>(userObservers);
                if (loggingConfig.EnableQueryLogging)
                {
                    allObservers.Insert(0, new QueryLoggingObserver(
                        sp.GetRequiredService<ILogger<QueryLoggingObserver>>(), loggingConfig));
                }
                return new QueryObserversWrap
                {
                    Observers = allObservers,
                    OnError = (ex, observer, phase) =>
                        sp.GetRequiredService<ILogger<QueryObserversWrap>>()
                          .LogError(ex, "Observer {Observer} failed at {Phase}", observer.GetType().Name, phase),
                };
            });

            services.AddSingleton<IQueryTransformerService, QueryTransformerService>();
            services.AddSingleton<IComputedColumnProvider, LocalFileFolderComputedColumnProvider>();
            services.AddSingleton<IComputedColumnProvider, S3FileFolderComputedColumnProvider>();
            services.AddSingleton<IComputedColumnProvider>(_ => new StateMachineTransitionsProvider());
            services.AddSingleton<IComputedColumnProviders>(sp => new ComputedColumnProviders(sp.GetServices<IComputedColumnProvider>()));

            var isAuthEnabled = IsUsingAuth;

            services.AddGraphQL(b => b
                    .AddSystemTextJson()
                    .AddBifrostErrorLogging(options =>
                    {
                        options.EnableConsole = _loggingConfig?.GetValue("EnableConsole", true) ?? true;
                        options.EnableFile = _loggingConfig?.GetValue("EnableFile", true) ?? true;
                        options.MinimumLevel = _loggingConfig?.GetValue("MinimumLevel", LogLevel.Information) ?? LogLevel.Information;
                        options.LogFilePath = _loggingConfig?.GetValue<string>("FilePath");
                        options.EnableQueryLogging = _loggingConfig?.GetValue("EnableQueryLogging", true) ?? true;
                        options.SlowQueryThresholdMs = _loggingConfig?.GetValue("SlowQueryThresholdMs", 1000) ?? 1000;
                        options.LogSql = _loggingConfig?.GetValue("LogSql", false) ?? false;
                    })
            );

            if (isAuthEnabled && _jwtConfig is not null)
            {
                var scopes = new HashSet<string>() { "openid" };
                foreach (var scope in (_jwtConfig["Scopes"] ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    scopes.Add(scope);
                }
                services
                    .AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                        options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                        options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                    })
                    .AddCookie()
                    .AddOpenIdConnect("oauth2", options =>
                    {
                        options.Authority = _jwtConfig["Authority"];
                        options.ClientId = _jwtConfig["ClientId"];
                        options.ClientSecret = _jwtConfig["ClientSecret"];
                        options.ResponseType = OpenIdConnectResponseType.Code;
                        options.Scope.Clear();
                        foreach (var scope in scopes)
                        {
                            options.Scope.Add(scope);
                        }
                        if (!string.IsNullOrWhiteSpace(_jwtConfig["Callback"]))
                            options.CallbackPath = new PathString(_jwtConfig["Callback"]);
                        if (!string.IsNullOrWhiteSpace(_jwtConfig["ClaimsIssuer"]))
                            options.ClaimsIssuer = _jwtConfig["ClaimsIssuer"];
                        options.GetClaimsFromUserInfoEndpoint = true;
                        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
                        {
                            NameClaimType = ClaimTypes.NameIdentifier,
                        };
                    });
            }
        }
    }

}
