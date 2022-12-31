using GraphQL.Types;
using GraphQLProxy.Model;
using GraphQLProxy.Schema;
using GraphQLProxy;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using GraphQL;

namespace HandyQL.Core
{
    public static class Extensions
    {
        public static  WebApplicationBuilder AddHandyQL(this WebApplicationBuilder builder)
        {
            var loader = new DbModelLoader(builder.Configuration);
            var model = loader.LoadAsync().Result;
            var connFactory = new DbConnFactory(builder.Configuration.GetConnectionString("ConnStr"));

            builder.Services.AddScoped<ITableReaderFactory, TableReaderFactory>();
            builder.Services.AddSingleton<IDbModel>(model);
            builder.Services.AddSingleton<IDbConnFactory>(connFactory);
            builder.Services.AddSingleton<DbDatabaseQuery>();
            builder.Services.AddSingleton<DbDatabaseMutation>();
            builder.Services.AddSingleton<ISchema, DbSchema>();

            builder.Services.AddGraphQL(b => b
                .AddSchema<DbSchema>()
                .AddSystemTextJson()
                .AddDataLoader());

            return builder;
        }

        public static WebApplication UseHandyQL(this WebApplication app, string endpointPath = "/graphql", string playgroundPath = "/")
        {
            app.UseGraphQL(endpointPath);
            app.UseGraphQLPlayground(playgroundPath,
                new GraphQL.Server.Ui.Playground.PlaygroundOptions
                {
                    GraphQLEndPoint = endpointPath,
                    SubscriptionsEndPoint = endpointPath,
                });
            return app;
        }
    }
}