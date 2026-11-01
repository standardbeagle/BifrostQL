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
            => app.UseBifrostQL(BifrostAuthGateScope.AllRoutes);

        /// <summary>
        /// Mounts the GraphQL endpoint and playground. With auth enabled,
        /// <paramref name="gateScope"/> decides which requests the login gate
        /// (<see cref="UIAuthMiddleware.UseUiAuth"/>) covers:
        /// <see cref="BifrostAuthGateScope.AllRoutes"/> (the default) gates every request that
        /// reaches this point in the pipeline, the host's own routes included;
        /// <see cref="BifrostAuthGateScope.BifrostRoutes"/> gates only the GraphQL endpoint and
        /// the playground, so the host's other routes keep whatever authorization they declare.
        /// Choosing <c>BifrostRoutes</c> widens what an anonymous caller reaches: every host route
        /// must then carry its own authorization.
        /// </summary>
        public static IApplicationBuilder UseBifrostQL(this IApplicationBuilder app, BifrostAuthGateScope gateScope)
        {
            var options = app.ApplicationServices.GetService<BifrostSetupOptions>();
            if (options == null) throw new InvalidOperationException("BifrostSetupOptions not configured. Call AddBifrostQL before UseBifrostQL");
            var useAuth = options.IsUsingAuth;
            var endpointPath = options.EndpointPath;
            var playgroundPath = options.PlaygroundPath;
            var gateAllRoutes = gateScope == BifrostAuthGateScope.AllRoutes;

            app.IfFluent(useAuth, a => a.UseAuthentication().UseCookiePolicy());
            app.IfFluent(useAuth && gateAllRoutes, a => a.UseUiAuth());
            // BifrostRoutes: the playground keeps the interactive login redirect, matched on its
            // exact path (it defaults to "/", which a prefix match would turn into every route).
            app.IfFluent(useAuth && !gateAllRoutes, a => a.UseWhen(
                context => string.Equals(context.Request.Path.Value?.TrimEnd('/'), playgroundPath.TrimEnd('/'),
                    StringComparison.OrdinalIgnoreCase),
                branch => branch.UseUiAuth()));
            app.Map(endpointPath, branch =>
            {
                if (useAuth && !gateAllRoutes)
                    branch.UseUiAuth();
                branch.UseMiddleware<BifrostHttpMiddleware>();
            });
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

namespace BifrostQL.Server
{
    /// <summary>Which requests <c>UseBifrostQL</c>'s login gate covers when auth is enabled.</summary>
    public enum BifrostAuthGateScope
    {
        /// <summary>Every request that reaches <c>UseBifrostQL</c> in the pipeline (the default).</summary>
        AllRoutes,

        /// <summary>Only the GraphQL endpoint and the playground.</summary>
        BifrostRoutes,
    }
}
