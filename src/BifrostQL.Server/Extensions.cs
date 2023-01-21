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

namespace BifrostQL.Core
{
    public static class Extensions
    {
        private static IConfigurationSection? _jwtConfig;

        public static WebApplicationBuilder AddBifrostQL(this WebApplicationBuilder builder)
        {
            var loader = new DbModelLoader(builder.Configuration);
            var model = loader.LoadAsync().Result;
            var connFactory = new DbConnFactory(builder.Configuration.GetConnectionString("ConnStr"));

            builder.Services.AddScoped<ITableReaderFactory, TableReaderFactory>();
            builder.Services.AddSingleton(model);
            builder.Services.AddSingleton<IDbConnFactory>(connFactory);
            builder.Services.AddSingleton<DbDatabaseQuery>();
            builder.Services.AddSingleton<DbDatabaseMutation>();
            builder.Services.AddSingleton<ISchema, DbSchema>();


            //var serverConfig = builder.Configuration.GetSection("BifrostQL.Server");
            //if (serverConfig != null)
            //{
            //    var authConfig = serverConfig.GetSection("Authentication");
            //    if (authConfig != null)
            //    {
            //        var jwtConfig = authConfig.GetSection("JwtBearer");
            //        var authority = jwtConfig["Authorization"];
            //        var audience = jwtConfig["Audience"];

            //        builder.Services.AddAuthentication(options =>
            //        {
            //            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            //        });


            //    }
            //}

            _jwtConfig = builder.Configuration.GetSection("JwtSettings");
            var grapQLBuilder = builder.Services.AddGraphQL(b => b
            .AddSchema<DbSchema>()
            .AddSystemTextJson()
            .IfFluent(_jwtConfig.Exists(), b => b.AddUserContextBuilder(context => new BifrostContext(context)))
            );

            if (_jwtConfig.Exists() )
            {
                builder.Services
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
                        options.ClientId= _jwtConfig["ClientId"];
                        options.ClientSecret = _jwtConfig["ClientSecret"];
                        options.ResponseType = OpenIdConnectResponseType.Code;
                        options.Scope.Clear();
                        options.Scope.Add("openid");
                        options.CallbackPath = new PathString(_jwtConfig["Callback"]);
                        options.ClaimsIssuer = _jwtConfig["ClaimsIssuer"];
                    });
            }
;

            return builder;
        }

        public static IApplicationBuilder UseBifrostQL(this IApplicationBuilder app, string endpointPath = "/graphql", string playgroundPath = "/")
        {
            app.IfFluent(_jwtConfig!.Exists(), a => a.UseAuthentication().UseCookiePolicy());
            app.UseGraphQL(endpointPath);
            //app.UseStaticFiles();
            app.UseGraphQLPlayground(playgroundPath,
                new GraphQL.Server.Ui.Playground.PlaygroundOptions
                {
                    GraphQLEndPoint = endpointPath,
                    SubscriptionsEndPoint = endpointPath,
                    RequestCredentials = GraphQL.Server.Ui.Playground.RequestCredentials.SameOrigin,
                });
            //app.MapFallbackToFile("index.html");
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