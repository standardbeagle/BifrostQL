using BifrostQL.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using GraphQL;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;
using BifrostQL.Core.Model;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.Modules.Eav;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Core.Storage;
using BifrostQL.Core.Workflows;
using BifrostQL.Server.Logging;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server
{
    public class BifrostSetupOptions
    {
        private IConfigurationSection? _bifrostConfig;
        private IConfigurationSection? _jwtConfig;
        private string? _connectionString;
        private string? _provider;
        private IReadOnlyCollection<IFilterTransformer> _filterTransformers = Array.Empty<IFilterTransformer>();
        private Func<IServiceProvider, IReadOnlyCollection<IFilterTransformer>>? _filterTransformerLoader = null;
        private IReadOnlyCollection<IMutationTransformer> _mutationTransformers = Array.Empty<IMutationTransformer>();
        private Func<IServiceProvider, IReadOnlyCollection<IMutationTransformer>>? _mutationTransformerLoader = null;
        private IReadOnlyCollection<IQueryObserver> _queryObservers = Array.Empty<IQueryObserver>();
        private Func<IServiceProvider, IReadOnlyCollection<IQueryObserver>>? _queryObserverLoader = null;
        // Types registered via the generic AddXTransformer<T>()/AddQueryObserver<T>() overloads.
        // Each is registered in DI and resolved at build time, then appended to whatever the
        // collection/loader overloads supplied — the generic form is additive, not a replacement.
        private readonly List<Type> _filterTransformerTypes = new();
        private readonly List<Type> _mutationTransformerTypes = new();
        private readonly List<Type> _queryObserverTypes = new();
        private readonly List<Type> _protocolAdapterTypes = new();
        private readonly List<Type> _chatConnectorTypes = new();
        private IReadOnlyList<IMetadataSource> _metadataSources = Array.Empty<IMetadataSource>();
        private readonly BifrostProfileRegistry _profileRegistry = new();

        /// <summary>
        /// Adds additional metadata sources that are merged in priority order on top of file-based metadata.
        /// Higher-priority sources override lower-priority ones.
        /// </summary>
        public BifrostSetupOptions AddMetadataSources(IReadOnlyList<IMetadataSource> sources)
        {
            _metadataSources = sources;
            return this;
        }

        public BifrostSetupOptions BindStandardConfig(IConfiguration config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (config.GetSection("BifrostQL") == null) throw new ArgumentOutOfRangeException(nameof(config), "Config is missing BifrostQL entry");
            if (string.IsNullOrWhiteSpace(_connectionString) && config.GetConnectionString("bifrost") == null) throw new ArgumentOutOfRangeException(nameof(config), "Connection string has not been explicitly set and config is missing bifrost connection string");
            if (config.GetValue<bool>("BifrostQL:DisableAuth") == false && config.GetSection("JwtSettings").Exists() == false) throw new ArgumentOutOfRangeException(nameof(config), "GraphQL auth is enabled and JwtSettings is missing from config");
            return BindConfiguration(config.GetRequiredSection("BifrostQL"))
                    .BindJwtSettings(config.GetSection("JwtSettings"))
                    .BindConnectionString(config.GetConnectionString("bifrost"), !string.IsNullOrWhiteSpace(_connectionString))
                    .BindProvider(config.GetValue<string>("BifrostQL:Provider"), !string.IsNullOrWhiteSpace(_provider))
                    .BindProfiles(config.GetSection("BifrostQL:Profiles"));
        }

        public BifrostSetupOptions BindConnectionString(string? connectionString, bool skip = false)
        {
            if (skip) return this;

            _connectionString = connectionString;
            return this;
        }

        /// <summary>
        /// Sets the database provider/dialect (e.g., "sqlserver", "postgresql", "mysql", "sqlite").
        /// When not set, the provider is auto-detected from the connection string.
        /// </summary>
        public BifrostSetupOptions BindProvider(string? provider, bool skip = false)
        {
            if (skip) return this;
            _provider = provider;
            return this;
        }

        public BifrostSetupOptions BindConfiguration(IConfigurationSection? section)
        {
            _bifrostConfig = section;
            return this;
        }

        public BifrostSetupOptions BindJwtSettings(IConfigurationSection? section)
        {
            if (!section.Exists()) return this;
            _jwtConfig = section;
            return this;
        }

        /// <summary>
        /// Registers filter transformers for query-time filter injection (tenant isolation, soft-delete, etc.)
        /// </summary>
        public BifrostSetupOptions AddFilterTransformers(IReadOnlyCollection<IFilterTransformer> transformers)
        {
            _filterTransformers = transformers;
            return this;
        }

        /// <summary>
        /// Registers filter transformers using a factory that can resolve from DI.
        /// </summary>
        public BifrostSetupOptions AddFilterTransformers(Func<IServiceProvider, IReadOnlyCollection<IFilterTransformer>>? loader)
        {
            _filterTransformerLoader = loader;
            return this;
        }

        /// <summary>
        /// Registers a single filter transformer of type <typeparamref name="T"/>, resolved
        /// from the service provider at build time. <typeparamref name="T"/> is added to DI,
        /// so its own dependencies are injected. Additive: composes with the collection and
        /// factory overloads rather than replacing them.
        /// </summary>
        public BifrostSetupOptions AddFilterTransformer<T>() where T : class, IFilterTransformer
        {
            if (!_filterTransformerTypes.Contains(typeof(T)))
                _filterTransformerTypes.Add(typeof(T));
            return this;
        }

        /// <summary>
        /// Registers mutation transformers (e.g., for converting DELETE to UPDATE for soft-delete).
        /// </summary>
        public BifrostSetupOptions AddMutationTransformers(IReadOnlyCollection<IMutationTransformer> transformers)
        {
            _mutationTransformers = transformers;
            return this;
        }

        /// <summary>
        /// Registers mutation transformers using a factory that can resolve from DI.
        /// </summary>
        public BifrostSetupOptions AddMutationTransformers(Func<IServiceProvider, IReadOnlyCollection<IMutationTransformer>>? loader)
        {
            _mutationTransformerLoader = loader;
            return this;
        }

        /// <summary>
        /// Registers a single mutation transformer of type <typeparamref name="T"/>, resolved
        /// from the service provider at build time. Additive: composes with the collection and
        /// factory overloads.
        /// </summary>
        public BifrostSetupOptions AddMutationTransformer<T>() where T : class, IMutationTransformer
        {
            if (!_mutationTransformerTypes.Contains(typeof(T)))
                _mutationTransformerTypes.Add(typeof(T));
            return this;
        }

        /// <summary>
        /// Registers query observers for side effects (auditing, metrics, etc.)
        /// </summary>
        public BifrostSetupOptions AddQueryObservers(IReadOnlyCollection<IQueryObserver> observers)
        {
            _queryObservers = observers;
            return this;
        }

        /// <summary>
        /// Registers query observers using a factory that can resolve from DI.
        /// </summary>
        public BifrostSetupOptions AddQueryObservers(Func<IServiceProvider, IReadOnlyCollection<IQueryObserver>>? loader)
        {
            _queryObserverLoader = loader;
            return this;
        }

        /// <summary>
        /// Registers a single query observer of type <typeparamref name="T"/>, resolved from
        /// the service provider at build time. Additive: composes with the collection and
        /// factory overloads.
        /// </summary>
        public BifrostSetupOptions AddQueryObserver<T>() where T : class, IQueryObserver
        {
            if (!_queryObserverTypes.Contains(typeof(T)))
                _queryObserverTypes.Add(typeof(T));
            return this;
        }

        /// <summary>
        /// Registers a protocol adapter (see <see cref="IProtocolAdapter"/>), resolved from
        /// DI and wrapped in its own hosted service so the host starts and stops it. The
        /// adapter owns wire listening and its codec; it executes reads exclusively through
        /// <see cref="IQueryIntentExecutor"/> and resolves identity through
        /// <see cref="IBifrostAuthContextFactory"/>, both injected from DI.
        /// </summary>
        public BifrostSetupOptions AddProtocolAdapter<T>() where T : class, IProtocolAdapter
        {
            if (!_protocolAdapterTypes.Contains(typeof(T)))
                _protocolAdapterTypes.Add(typeof(T));
            return this;
        }

        /// <summary>
        /// Registers a chat connector (see <see cref="BifrostQL.Core.Modules.Chat.IChatConnector"/>),
        /// resolved from DI and collected by the chat tool loop's connector registry.
        /// Additive, mirroring <see cref="AddFilterTransformer{T}"/>.
        /// </summary>
        public BifrostSetupOptions AddChatConnector<T>() where T : class, BifrostQL.Core.Modules.Chat.IChatConnector
        {
            if (!_chatConnectorTypes.Contains(typeof(T)))
                _chatConnectorTypes.Add(typeof(T));
            return this;
        }

        /// <summary>
        /// Adds a named configuration profile that controls which modules are active.
        /// </summary>
        public BifrostSetupOptions AddProfile(BifrostProfile profile)
        {
            _profileRegistry.Add(profile);
            return this;
        }

        /// <summary>
        /// Binds profiles from a configuration section (e.g., "BifrostQL:Profiles").
        /// Each child section is a profile name with "modules" array and optional "requireRole".
        /// </summary>
        public BifrostSetupOptions BindProfiles(IConfigurationSection? section)
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

        // Default missing DisableAuth to false (auth ON), matching the BindStandardConfig
        // startup guard which requires JwtSettings whenever DisableAuth is not explicitly true.
        // Defaulting to true here would fail open: JwtSettings present + DisableAuth unset would
        // pass startup validation yet serve /graphql unauthenticated.
        public bool IsUsingAuth => _bifrostConfig is not null && !_bifrostConfig.GetValue<bool>("DisableAuth", false);
        public string EndpointPath => _bifrostConfig?.GetValue<string>("Path", "/graphql") ?? "/graphql";
        public string PlaygroundPath => _bifrostConfig?.GetValue<string>("Playground", "/") ?? "/";

        /// <summary>
        /// Resets the cached PathCache so the next GraphQL request reloads the schema
        /// using the current connection string. Used for dynamic connection switching in UI mode.
        /// </summary>
        public void ResetSchema(IServiceProvider services)
        {
            // ResetAll drops the cached Inputs (which own the ProfileModelCache), so the
            // next request re-runs the loader and builds a fresh ProfileModelCache. That
            // cache reads the singleton BifrostProfileRegistry — the same instance a
            // prior ReplaceAll/Clear mutated in place — so rebound profiles are picked up
            // without any extra cache invalidation here.
            var pathCache = services.GetService<PathCache<Inputs>>();
            pathCache?.ResetAll();
        }

        /// <summary>
        /// Whether a connection string has been configured.
        /// When false, the PathCache loader will throw on first request (deferred connection mode).
        /// </summary>
        public bool HasConnectionString => !string.IsNullOrEmpty(_connectionString);

        public void ConfigureServices(IServiceCollection services)
        {
            if (_bifrostConfig == null) throw new InvalidOperationException("bifrostConfig not specified");

            var path = EndpointPath;
            var metadataSources = _metadataSources;
            // Base metadata rule strings — the same rules MetadataLoader(_bifrostConfig, "Metadata")
            // would consume. Captured once so the ProfileModelCache can compose them with each
            // profile's own metadata without re-reading config.
            var configMetadataRules = _bifrostConfig.GetSection("Metadata").GetChildren()
                .Where(c => c.Value != null)
                .Select(c => c.Value!)
                .ToArray();
            // Always pass the registry to the profile cache so a runtime ReplaceAll
            // (desktop per-connection rebind) is picked up on the next schema rebuild,
            // even when the registry started empty. An empty registry resolves to the
            // raw default profile, preserving existing behavior.
            var profileRegistry = _profileRegistry;
            var extensionsLoader = new PathCache<Inputs>();
            // Register under a case-normalized key. app.Map matches paths case-insensitively,
            // so a request to /GraphQL reaches this middleware with that casing; the PathCache
            // is ordinal-keyed, so the lookup side normalizes to the same lowercase form.
            extensionsLoader.AddLoader(path.ToLowerInvariant(), () => ProfileCacheBootstrapper.BuildInputsAsync(
                _connectionString ?? throw new InvalidOperationException("Connection string has not been configured."),
                _provider,
                configMetadataRules,
                metadataSources,
                profileRegistry));

            services.AddSingleton(this);
            services.AddSingleton(extensionsLoader);
            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();

            // The workflow executor runs sidecar workflow endpoints through the
            // same GraphQL pipeline as a direct /graphql request, so policy and
            // tenant-filter still apply. See the Workflow Mutations guide.
            // Registered here as well as in the multi-database path so the
            // single-database AddBifrostQL host can map workflow endpoints.
            BifrostServiceRegistrar.RegisterWorkflowServices(services);

            // Register unconditionally so a runtime ReplaceAll on this same instance
            // is visible to /api/profiles and the schema rebuild even if it starts empty.
            services.AddSingleton(_profileRegistry);

            BifrostServiceRegistrar.RegisterTransformerServices(
                services,
                _filterTransformers, _filterTransformerLoader, _filterTransformerTypes,
                _mutationTransformers, _mutationTransformerLoader, _mutationTransformerTypes,
                _queryObservers, _queryObserverLoader, _queryObserverTypes);

            BifrostServiceRegistrar.RegisterComputedColumnServices(services);
            BifrostServiceRegistrar.RegisterQueryIntentServices(services);
            BifrostServiceRegistrar.RegisterProtocolAdapterServices(services, _protocolAdapterTypes);
            BifrostServiceRegistrar.RegisterChatConnectorServices(services, _chatConnectorTypes);

            // Fail-secure default: missing DisableAuth means auth ON, consistent with
            // IsUsingAuth and the BindStandardConfig startup guard.
            var isAuthEnabled = !_bifrostConfig.GetValue<bool>("DisableAuth", false);

            // Bound query depth/complexity guard against unauthenticated DoS: nested
            // joins/aggregates fan out to correlated subqueries, so an unbounded query can
            // amplify a single request into a very large SQL workload. Defaults are applied
            // even when unconfigured; a host can tune or lift them via config.
            var (maxDepth, maxComplexity) = GraphQlComplexityLimits.Read(_bifrostConfig);

            BifrostServiceRegistrar.RegisterGraphQlAndAuth(
                services, isAuthEnabled, _jwtConfig, maxDepth, maxComplexity,
                _bifrostConfig.GetSection("Logging"));
        }
    }
}
