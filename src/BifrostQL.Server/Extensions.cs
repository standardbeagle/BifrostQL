using GraphQL.Types;
using BifrostQL.Model;
using BifrostQL;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using GraphQL;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using BifrostQL.Server;
using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Schema;
using BifrostQL.Server.Logging;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server
{
    public static class Extensions
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

        public static FluentT IfFluent<FluentT, ResultT>(this FluentT fluent, bool check, Func<FluentT, ResultT> doConfig)
            where ResultT : FluentT
        {
            if (!check) return fluent;
            return doConfig(fluent);
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
        private IReadOnlyCollection<IMutationModule> _modules = Array.Empty<IMutationModule>();
        private Func<IServiceProvider, IReadOnlyCollection<IMutationModule>>? _moduleLoader;
        private IReadOnlyCollection<IFilterTransformer> _filterTransformers = Array.Empty<IFilterTransformer>();
        private Func<IServiceProvider, IReadOnlyCollection<IFilterTransformer>>? _filterTransformerLoader;
        private IReadOnlyCollection<IMutationTransformer> _mutationTransformers = Array.Empty<IMutationTransformer>();
        private Func<IServiceProvider, IReadOnlyCollection<IMutationTransformer>>? _mutationTransformerLoader;
        private IReadOnlyCollection<IQueryObserver> _queryObservers = Array.Empty<IQueryObserver>();
        private Func<IServiceProvider, IReadOnlyCollection<IQueryObserver>>? _queryObserverLoader;
        private IConfigurationSection? _loggingConfig;

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

        public BifrostMultiDbOptions AddModules(IReadOnlyCollection<IMutationModule> modules)
        {
            _modules = modules;
            return this;
        }

        public BifrostMultiDbOptions AddModules(Func<IServiceProvider, IReadOnlyCollection<IMutationModule>>? moduleLoader)
        {
            _moduleLoader = moduleLoader;
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

        internal void ConfigureServices(IServiceCollection services)
        {
            if (_endpoints.Count == 0)
                throw new InvalidOperationException("At least one endpoint must be configured. Call AddEndpoint.");

            var extensionsLoader = new PathCache<Inputs>();
            foreach (var endpoint in _endpoints)
            {
                var connStr = endpoint.ConnectionString;
                var metadataLoader = new MetadataLoader(endpoint.Metadata);
                var metadataSources = endpoint.MetadataSources;
                extensionsLoader.AddLoader(endpoint.Path, () =>
                {
                    IDictionary<string, IDictionary<string, object?>>? additionalMetadata = null;
                    if (metadataSources.Count > 0)
                    {
                        var composite = new CompositeMetadataSource(metadataSources);
                        additionalMetadata = composite.LoadTableMetadataAsync().Result;
                    }
                    var loader = new DbModelLoader(connStr, metadataLoader);
                    var model = loader.LoadAsync(additionalMetadata).Result;
                    var connFactory = new DbConnFactory(connStr);
                    var schema = DbSchema.FromModel(model);
                    return new Inputs(new Dictionary<string, object?>
                    {
                        { "model", model },
                        { "connFactory", connFactory },
                        { "dbSchema", schema },
                    });
                });
            }

            services.AddSingleton(this);
            services.AddSingleton(extensionsLoader);
            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();

            if (_moduleLoader != null)
                services.AddSingleton<IMutationModules>(sp => new ModulesWrap { Modules = _moduleLoader(sp) });
            else
                services.AddSingleton<IMutationModules>(new ModulesWrap { Modules = _modules });

            if (_filterTransformerLoader != null)
                services.AddSingleton<IFilterTransformers>(sp => new FilterTransformersWrap { Transformers = _filterTransformerLoader(sp) });
            else
                services.AddSingleton<IFilterTransformers>(new FilterTransformersWrap { Transformers = _filterTransformers });

            if (_mutationTransformerLoader != null)
                services.AddSingleton<IMutationTransformers>(sp => new MutationTransformersWrap { Transformers = _mutationTransformerLoader(sp) });
            else
                services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap { Transformers = _mutationTransformers });

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

            services.AddSingleton<IQueryTransformerService, QueryTransformerService>();

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

    public class BifrostSetupOptions
    {
        private IConfigurationSection? _bifrostConfig;
        private IConfigurationSection? _jwtConfig;
        private string? _connectionString;
        private IReadOnlyCollection<IMutationModule> _modules = Array.Empty<IMutationModule>();
        private Func<IServiceProvider, IReadOnlyCollection<IMutationModule>>? _moduleLoader = null;
        private IReadOnlyCollection<IFilterTransformer> _filterTransformers = Array.Empty<IFilterTransformer>();
        private Func<IServiceProvider, IReadOnlyCollection<IFilterTransformer>>? _filterTransformerLoader = null;
        private IReadOnlyCollection<IMutationTransformer> _mutationTransformers = Array.Empty<IMutationTransformer>();
        private Func<IServiceProvider, IReadOnlyCollection<IMutationTransformer>>? _mutationTransformerLoader = null;
        private IReadOnlyCollection<IQueryObserver> _queryObservers = Array.Empty<IQueryObserver>();
        private Func<IServiceProvider, IReadOnlyCollection<IQueryObserver>>? _queryObserverLoader = null;
        private IReadOnlyList<IMetadataSource> _metadataSources = Array.Empty<IMetadataSource>();

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
                    .BindConnectionString(config.GetConnectionString("bifrost"), !string.IsNullOrWhiteSpace(_connectionString));
        }

        public BifrostSetupOptions BindConnectionString(string? connectionString, bool skip = false)
        {
            if (skip) return this;

            _connectionString = connectionString;
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

        public bool IsUsingAuth => _bifrostConfig is not null && !_bifrostConfig.GetValue<bool>("DisableAuth", true);
        public string EndpointPath => _bifrostConfig?.GetValue<string>("Path", "/graphql") ?? "/graphql";
        public string PlaygroundPath => _bifrostConfig?.GetValue<string>("Playground", "/") ?? "/";

        public void ConfigureServices(IServiceCollection services)
        {
            if (_bifrostConfig == null) throw new InvalidOperationException("bifrostConfig not specified");
            if (_connectionString == null) throw new InvalidOperationException("connectionString is empty");

            var path = EndpointPath;
            var metadataLoader = new MetadataLoader(_bifrostConfig, "Metadata");
            var metadataSources = _metadataSources;
            var extensionsLoader = new PathCache<Inputs>();
            extensionsLoader.AddLoader(path, () =>
            {
                IDictionary<string, IDictionary<string, object?>>? additionalMetadata = null;
                if (metadataSources.Count > 0)
                {
                    var composite = new CompositeMetadataSource(metadataSources);
                    additionalMetadata = composite.LoadTableMetadataAsync().Result;
                }
                var loader = new DbModelLoader(_connectionString, metadataLoader);
                var model = loader.LoadAsync(additionalMetadata).Result;
                var connFactory = new DbConnFactory(_connectionString);
                var schema = DbSchema.FromModel(model);
                return new Inputs(new Dictionary<string, object?>
                {
                    { "model", model},
                    { "connFactory", connFactory },
                    { "dbSchema", schema },
                });
            });

            services.AddSingleton(this);
            services.AddSingleton(extensionsLoader);
            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            services.AddSingleton<IMutationModules>(new ModulesWrap { Modules = _modules });
            if (_moduleLoader != null)
                services.AddSingleton<IMutationModules>((sp => new ModulesWrap { Modules = _moduleLoader(sp) }));

            // Register filter transformers
            if (_filterTransformerLoader != null)
                services.AddSingleton<IFilterTransformers>(sp => new FilterTransformersWrap { Transformers = _filterTransformerLoader(sp) });
            else
                services.AddSingleton<IFilterTransformers>(new FilterTransformersWrap { Transformers = _filterTransformers });

            // Register mutation transformers
            if (_mutationTransformerLoader != null)
                services.AddSingleton<IMutationTransformers>(sp => new MutationTransformersWrap { Transformers = _mutationTransformerLoader(sp) });
            else
                services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap { Transformers = _mutationTransformers });

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