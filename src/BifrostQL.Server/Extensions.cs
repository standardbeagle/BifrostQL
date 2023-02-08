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

namespace BifrostQL.Server
{
    public static class Extensions
    {
        private static IConfigurationSection? _jwtConfig;

        public static IServiceCollection AddBifrostQL(this IServiceCollection services, IConfiguration configuration, Func<IServiceProvider, IReadOnlyCollection<IMutationModule>>? getModules = null)
        {
            var loader = new DbModelLoader(configuration);
            var model = loader.LoadAsync().Result;
            var connFactory = new DbConnFactory(configuration.GetConnectionString("ConnStr"));

            services.AddScoped<ITableReaderFactory, TableReaderFactory>();
            services.AddSingleton(model);
            services.AddSingleton((IDbConnFactory)connFactory);
            services.AddSingleton<DbDatabaseQuery>();
            services.AddSingleton<DbDatabaseMutation>();
            services.AddSingleton<ISchema, DbSchema>();
            if (getModules != null)
                services.AddSingleton((Func<IServiceProvider, IMutationModules>)(sp => new ModulesWrap { Modules = getModules(sp) }));

            JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

            _jwtConfig = configuration.GetSection("JwtSettings");
            var grapQLBuilder = services.AddGraphQL(b => b
            .AddSchema<DbSchema>()
            .AddSystemTextJson()
            .IfFluent(_jwtConfig.Exists(), b => b.AddUserContextBuilder(context => new BifrostContext(context))));

            if (_jwtConfig.Exists())
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
;

            return services;
        }

        public static IApplicationBuilder UseBifrostQL(this IApplicationBuilder app, string endpointPath = "/graphql", string playgroundPath = "/")
        {
            app.IfFluent(_jwtConfig!.Exists(), a => a.UseAuthentication().UseCookiePolicy());
            app.IfFluent(_jwtConfig!.Exists(), a => a.UseUiAuth());
            app.UseGraphQL(endpointPath);
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
}