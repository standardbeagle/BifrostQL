using GraphQL.Types;
using BifrostQL.Model;
using BifrostQL.Schema;
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
using BifrostQL.Core.Modules;
using BifrostQL.Resolvers;
using BifrostQL.Core.Schema;

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

        public static IApplicationBuilder UseBifrostQL(this IApplicationBuilder app, string endpointPath = "/graphql", string playgroundPath = "/", bool useAuth = false)
        {
            app.IfFluent(useAuth, a => a.UseAuthentication().UseCookiePolicy());
            app.IfFluent(useAuth, a => a.UseUiAuth());
            app.UseGraphQL<BifrostHttpMiddleware>(endpointPath);
            app.UseGraphQLPlayground(playgroundPath,
                new GraphQL.Server.Ui.Playground.PlaygroundOptions
                {
                    GraphQLEndPoint = endpointPath,
                    SubscriptionsEndPoint = endpointPath,
                    RequestCredentials = GraphQL.Server.Ui.Playground.RequestCredentials.SameOrigin,
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

    public class BifrostUseOptions
    {

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
            return BindConfiguration(config.GetRequiredSection("BifrostQL"))
                    .BindJwtSettings(config.GetSection("JwtSettings"))
                    .BindConnectionString(config.GetConnectionString("bifrost"));
        }

        public BifrostSetupOptions BindConnectionString(string connectionString)
        {
            _connectionString = connectionString;
            return this;
        }

        public BifrostSetupOptions BindConfiguration(IConfigurationSection section)
        {
            _bifrostConfig = section;
            return this;
        }

        public BifrostSetupOptions BindJwtSettings(IConfigurationSection section)
        {
            _jwtConfig = section;
            return this;
        }
        public BifrostSetupOptions AddModules(IReadOnlyCollection<IMutationModule> modules)
        {
            _modules = modules;
            return this;
        }
        public BifrostSetupOptions AddModules(Func<IServiceProvider, IReadOnlyCollection<IMutationModule>> moduleLoader)
        {
            _moduleLoader = moduleLoader;
            return this;
        }

        public void ConfigureServices(IServiceCollection services)
        {
            if (_bifrostConfig == null) throw new ArgumentNullException("bifrostConfig");
            if (_connectionString == null) throw new ArgumentNullException("connectionString");

            var path = _bifrostConfig.GetValue<string>("Path", "/graphql");

            var extensionsLoader = new PathCache<Inputs>();
            extensionsLoader.AddLoader(path, () =>
            {
                var loader = new DbModelLoader(_bifrostConfig, _connectionString);
                var model = loader.LoadAsync().Result;
                var connFactory = new DbConnFactory(_connectionString);
                var schema = DbSchema.SchemaFromModel(model);
                return new Inputs(new Dictionary<string, object?>
                {
                    { "model", model}, 
                    { "connFactory", connFactory },
                    { "dbSchema", schema },
                });
            });


            services.AddSingleton(extensionsLoader);
            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            if (_modules != null)
                services.AddSingleton<IMutationModules>(new ModulesWrap { Modules = _modules });
            if (_moduleLoader != null)
                services.AddSingleton<IMutationModules>((sp => new ModulesWrap { Modules = _moduleLoader(sp) }));

            JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

            var grapQLBuilder = services.AddGraphQL(b => b
            .AddSystemTextJson()
            .IfFluent(_jwtConfig.Exists(), b => b.AddUserContextBuilder(context => new BifrostContext(context))));

            if (_jwtConfig?.Exists() ?? false)
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