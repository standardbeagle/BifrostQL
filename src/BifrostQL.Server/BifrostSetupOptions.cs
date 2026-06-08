using BifrostQL.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
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
        private IReadOnlyCollection<IMutationModule> _modules = Array.Empty<IMutationModule>();
        private Func<IServiceProvider, IReadOnlyCollection<IMutationModule>>? _moduleLoader = null;
        private IReadOnlyCollection<IFilterTransformer> _filterTransformers = Array.Empty<IFilterTransformer>();
        private Func<IServiceProvider, IReadOnlyCollection<IFilterTransformer>>? _filterTransformerLoader = null;
        private IReadOnlyCollection<IMutationTransformer> _mutationTransformers = Array.Empty<IMutationTransformer>();
        private Func<IServiceProvider, IReadOnlyCollection<IMutationTransformer>>? _mutationTransformerLoader = null;
        private IReadOnlyCollection<IQueryObserver> _queryObservers = Array.Empty<IQueryObserver>();
        private Func<IServiceProvider, IReadOnlyCollection<IQueryObserver>>? _queryObserverLoader = null;
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
        public BifrostSetupOptions AddModules(IReadOnlyCollection<IMutationModule> modules)
        {
            _modules = modules;
            return this;
        }
        public BifrostSetupOptions AddModules(Func<IServiceProvider, IReadOnlyCollection<IMutationModule>>? moduleLoader)
        {
            _moduleLoader = moduleLoader;
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

        public bool IsUsingAuth => _bifrostConfig is not null && !_bifrostConfig.GetValue<bool>("DisableAuth", true);
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
            extensionsLoader.AddLoader(path, async () =>
            {
                IDictionary<string, IDictionary<string, object?>>? additionalMetadata = null;
                if (metadataSources.Count > 0)
                {
                    var composite = new CompositeMetadataSource(metadataSources);
                    additionalMetadata = await composite.LoadTableMetadataAsync();
                }
                var provider = string.IsNullOrWhiteSpace(_provider)
                    ? (BifrostDbProvider?)null
                    : DbConnFactoryResolver.ParseProviderName(_provider);
                var connFactory = DbConnFactoryResolver.Create(
                    _connectionString ?? throw new InvalidOperationException("Connection string has not been configured."),
                    provider);
                // Read the DB schema once; ProfileModelCache builds a model+schema per profile
                // from this shared read, varying only the metadata (CPU-only, memoized).
                var loader = new DbModelLoader(connFactory, new MetadataLoader(configMetadataRules));
                var read = await loader.ReadAsync();
                // Pre-load enum lookup values once (async DB read) so the
                // synchronous per-profile cache can attach the enum map.
                var baseModel = loader.BuildModel(read, new MetadataLoader(configMetadataRules), additionalMetadata);
                var enumValues = await loader.LoadEnumValuesAsync(baseModel);
                var profileCache = new ProfileModelCache(
                    loader, read, configMetadataRules, additionalMetadata, profileRegistry, enumValues);
                // Default/base build (null → empty default profile) for back-compat extensions.
                var (model, schema) = profileCache.GetFor(null);
                return new Inputs(new Dictionary<string, object?>
                {
                    { "model", model},
                    { "connFactory", connFactory },
                    { "dbSchema", schema },
                    { "profileModelCache", profileCache },
                });
            });

            services.AddSingleton(this);
            services.AddSingleton(extensionsLoader);
            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();

            // The workflow executor runs sidecar workflow endpoints through the
            // same GraphQL pipeline as a direct /graphql request, so policy and
            // tenant-filter still apply. See the Workflow Mutations guide.
            // Registered here as well as in the multi-database path so the
            // single-database AddBifrostQL host can map workflow endpoints.
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
            services.AddSingleton<StateTransitionObservers>(sp => new StateTransitionObservers(
                new IStateTransitionObserver[]
                {
                    new StateTransitionAuditObserver(sp.GetRequiredService<IBifrostWorkflowExecutor>()),
                    sp.GetRequiredService<WorkflowTriggerHost>(),
                }));

            // Register unconditionally so a runtime ReplaceAll on this same instance
            // is visible to /api/profiles and the schema rebuild even if it starts empty.
            services.AddSingleton(_profileRegistry);

            services.AddSingleton<IMutationModules>(new ModulesWrap { Modules = _modules });
            if (_moduleLoader != null)
                services.AddSingleton<IMutationModules>((sp => new ModulesWrap { Modules = _moduleLoader(sp) }));

            // Register filter transformers
            if (_filterTransformerLoader != null)
                services.AddSingleton<IFilterTransformers>(sp => new FilterTransformersWrap { Transformers = BifrostServiceCollectionExtensions.WithBuiltInFilterTransformers(_filterTransformerLoader(sp)) });
            else
                services.AddSingleton<IFilterTransformers>(new FilterTransformersWrap { Transformers = BifrostServiceCollectionExtensions.WithBuiltInFilterTransformers(_filterTransformers) });

            // Register mutation transformers
            if (_mutationTransformerLoader != null)
                services.AddSingleton<IMutationTransformers>(sp => new MutationTransformersWrap { Transformers = BifrostServiceCollectionExtensions.WithBuiltInMutationTransformers(_mutationTransformerLoader(sp), sp) });
            else
                services.AddSingleton<IMutationTransformers>(sp => new MutationTransformersWrap { Transformers = BifrostServiceCollectionExtensions.WithBuiltInMutationTransformers(_mutationTransformers, sp) });

            // Register query observers with built-in logging observer and error callback
            services.AddSingleton<IQueryObservers>(sp =>
            {
                var userObservers = _queryObserverLoader != null
                    ? _queryObserverLoader(sp)
                    : _queryObservers;
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

            // Register query transformer service
            services.AddSingleton<IQueryTransformerService, QueryTransformerService>();
            services.AddSingleton<IComputedColumnProvider, LocalFileFolderComputedColumnProvider>();
            services.AddSingleton<IComputedColumnProvider, S3FileFolderComputedColumnProvider>();
            services.AddSingleton<IComputedColumnProviders>(sp => new ComputedColumnProviders(sp.GetServices<IComputedColumnProvider>()));

            var isAuthEnabled = !_bifrostConfig.GetValue<bool>("DisableAuth", true);

            //JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

            services.AddGraphQL(b => b
                    .AddSystemTextJson()
                    .AddBifrostErrorLogging(options =>
                    {
                        // Get logging configuration from BifrostQL section if it exists
                        var loggingConfig = _bifrostConfig?.GetSection("Logging");
                        options.EnableConsole = loggingConfig?.GetValue("EnableConsole", true) ?? true;
                        options.EnableFile = loggingConfig?.GetValue("EnableFile", true) ?? true;
                        options.MinimumLevel = loggingConfig?.GetValue("MinimumLevel", LogLevel.Information) ?? LogLevel.Information;
                        options.LogFilePath = loggingConfig?.GetValue<string>("FilePath");
                        options.EnableQueryLogging = loggingConfig?.GetValue("EnableQueryLogging", true) ?? true;
                        options.SlowQueryThresholdMs = loggingConfig?.GetValue("SlowQueryThresholdMs", 1000) ?? 1000;
                        options.LogSql = loggingConfig?.GetValue("LogSql", false) ?? false;
                    })
            );

            if (isAuthEnabled && _jwtConfig is not null)
            {
                var scopes = new HashSet<string>() { "openid" };
                foreach (var scope in (_jwtConfig["Scopes"] ?? "").Split(" "))
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
