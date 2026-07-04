using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Server
{
    /// <summary>
    /// Core AddBifrostQL/UseBifrostQL registration skeleton for a single-database host.
    /// Related extension groups live in sibling partial files:
    /// <see cref="BifrostServiceCollectionExtensions"/>.Info.cs (info/app-metadata endpoints),
    /// .Auth.cs (local auth, OIDC claim mappers), .Endpoints.cs (multi-database endpoints,
    /// binary transport), and .Transformers.cs (built-in transformer composition helpers).
    /// </summary>
    public static partial class BifrostServiceCollectionExtensions
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

        public static FluentT IfFluent<FluentT, ResultT>(this FluentT fluent, bool check, Func<FluentT, ResultT> doConfig)
            where ResultT : FluentT
        {
            if (!check) return fluent;
            return doConfig(fluent);
        }
    }
}
