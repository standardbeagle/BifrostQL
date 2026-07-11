using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using GraphQL;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;
using BifrostQL.Model;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.Modules.Eav;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Schema;
using BifrostQL.Core.Storage;
using BifrostQL.Core.Workflows;
using BifrostQL.Server.Logging;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server
{
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
        private readonly List<Type> _protocolAdapterTypes = new();
        private IConfigurationSection? _loggingConfig;
        private IConfigurationSection? _queryLimitsConfig;
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

        /// <summary>
        /// Configures GraphQL query depth/complexity limits shared across all endpoints.
        /// Reads <c>MaxQueryDepth</c> and <c>MaxQueryComplexity</c> from the section. When
        /// not bound, the secure defaults from <see cref="GraphQlComplexityLimits"/> apply.
        /// </summary>
        public BifrostMultiDbOptions BindQueryLimits(IConfigurationSection? section)
        {
            _queryLimitsConfig = section;
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
        /// Registers a protocol adapter (see <see cref="IProtocolAdapter"/>), resolved from
        /// DI and wrapped in its own hosted service so the host starts and stops it. The
        /// adapter owns wire listening and its codec; it executes reads exclusively through
        /// <see cref="BifrostQL.Core.Resolvers.IQueryIntentExecutor"/> and resolves identity
        /// through <see cref="IBifrostAuthContextFactory"/>, both injected from DI.
        /// </summary>
        public BifrostMultiDbOptions AddProtocolAdapter<T>() where T : class, IProtocolAdapter
        {
            if (!_protocolAdapterTypes.Contains(typeof(T)))
                _protocolAdapterTypes.Add(typeof(T));
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

            services.AddSingleton(this);
            services.AddSingleton(BuildPathCache());
            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();

            BifrostServiceRegistrar.RegisterWorkflowServices(services);

            // Register unconditionally so a runtime ReplaceAll on this same instance
            // is visible even if it starts empty.
            services.AddSingleton(_profileRegistry);

            BifrostServiceRegistrar.RegisterTransformerServices(
                services,
                _filterTransformers, _filterTransformerLoader, _filterTransformerTypes,
                _mutationTransformers, _mutationTransformerLoader, _mutationTransformerTypes,
                _queryObservers, _queryObserverLoader, _queryObserverTypes);

            BifrostServiceRegistrar.RegisterComputedColumnServices(services);
            BifrostServiceRegistrar.RegisterQueryIntentServices(services);
            BifrostServiceRegistrar.RegisterProtocolAdapterServices(services, _protocolAdapterTypes);

            // Same bounded depth/complexity guard as the single-database path; applied to
            // every endpoint's shared executor (which the binary transport also uses).
            var (maxDepth, maxComplexity) = GraphQlComplexityLimits.Read(_queryLimitsConfig);

            BifrostServiceRegistrar.RegisterGraphQlAndAuth(
                services, IsUsingAuth, _jwtConfig, maxDepth, maxComplexity, _loggingConfig);
        }

        /// <summary>
        /// Builds the PathCache that lazily bootstraps each endpoint's connection, DbModel,
        /// schema, and ProfileModelCache via <see cref="ProfileCacheBootstrapper"/> on first
        /// request to that endpoint's path.
        /// </summary>
        private PathCache<Inputs> BuildPathCache()
        {
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
                // Case-normalized key: app.Map routes case-insensitively while the PathCache
                // is ordinal-keyed, so registration and lookup both normalize to lowercase.
                extensionsLoader.AddLoader(endpoint.Path.ToLowerInvariant(), () => ProfileCacheBootstrapper.BuildInputsAsync(
                    connStr, providerName, endpointMetadataRules, metadataSources, registry));
            }
            return extensionsLoader;
        }
    }
}
