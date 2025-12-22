using GraphQL.Types;
using BifrostQL.Model;
using BifrostQL;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using GraphQL;
using GraphQL.Server;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using BifrostQL.Server;
using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Schema;
using BifrostQL.Server.Logging;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server
{
    public static class Extensions
    {
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
            app.UseGraphQL<BifrostHttpMiddleware>(endpointPath);
            app.UseGraphQLGraphiQL(playgroundPath,
                new GraphQL.Server.Ui.GraphiQL.GraphiQLOptions
                {
                    GraphQLEndPoint = endpointPath,
                    SubscriptionsEndPoint = endpointPath,
                    RequestCredentials = GraphQL.Server.Ui.GraphiQL.RequestCredentials.SameOrigin,
                });
            return app;
        }
        public static FluentT IfFluent<FluentT, ResultT>(this FluentT fluent, bool check, Func<FluentT, ResultT> doConfig)
            where ResultT : FluentT
        {
            if (!check) return fluent;
            return doConfig(fluent);
        }
    }

    public class BifrostSetupOptions
    {
        private IConfigurationSection? _bifrostConfig;
        private IConfigurationSection? _jwtConfig;
        private string? _connectionString;
        private IReadOnlyCollection<IMutationModule> _modules = Array.Empty<IMutationModule>();
        private Func<IServiceProvider, IReadOnlyCollection<IMutationModule>>? _moduleLoader = null;
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

        public bool IsUsingAuth => _bifrostConfig is not null && !_bifrostConfig.GetValue<bool>("DisableAuth", true);
        public string EndpointPath => _bifrostConfig?.GetValue<string>("Path", "/graphql") ?? "/graphql";
        public string PlaygroundPath => _bifrostConfig?.GetValue<string>("Playground", "/") ?? "/";

        public void ConfigureServices(IServiceCollection services)
        {
            if (_bifrostConfig == null) throw new InvalidOperationException("bifrostConfig not specified");
            if (_connectionString == null) throw new InvalidOperationException("connectionString is empty");

            var path = EndpointPath;
            var metadataLoader = new MetadataLoader(_bifrostConfig, "Metadata");
            var extensionsLoader = new PathCache<Inputs>();
            extensionsLoader.AddLoader(path, () =>
            {
                var loader = new DbModelLoader(_connectionString, metadataLoader);
                var model = loader.LoadAsync().Result;
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

            var isAuthEnabled = !_bifrostConfig.GetValue<bool>("DisableAuth", true);

            //JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

            var grapQlBuilder = services.AddGraphQL(b => b
                    .AddSystemTextJson()
                    .AddBifrostErrorLogging(options => {
                        // Get logging configuration from BifrostQL section if it exists
                        var loggingConfig = _bifrostConfig?.GetSection("Logging");
                        options.EnableConsole = loggingConfig?.GetValue("EnableConsole", true) ?? true;
                        options.EnableFile = loggingConfig?.GetValue("EnableFile", true) ?? true;
                        options.MinimumLevel = loggingConfig?.GetValue("MinimumLevel", LogLevel.Information) ?? LogLevel.Information;
                        options.LogFilePath = loggingConfig?.GetValue<string>("FilePath");
                    })
                    .IfFluent(isAuthEnabled, b => b.AddUserContextBuilder(context => new BifrostContext(context)))
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