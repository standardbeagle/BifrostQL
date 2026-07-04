using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using BifrostQL.Core.Model;

namespace BifrostQL.Server
{
    public static partial class BifrostServiceCollectionExtensions
    {
        /// <summary>
        /// Registers local DB-backed user login for self-hosted deployments. The app-user
        /// table lives in the same database BifrostQL serves, reached through a server-side
        /// <see cref="IDbConnFactory"/> built from <paramref name="connectionString"/> — the
        /// database credentials never reach the client. Adds cookie authentication and the
        /// <see cref="BifrostQL.Server.Auth.LocalUserStore"/>; pair with
        /// <c>UseBifrostLocalAuth()</c> to map the login/logout endpoints.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="connectionString">
        /// Connection string for the database holding the app-user rows. Typically the same
        /// <c>bifrost</c> connection string the GraphQL endpoint uses.
        /// </param>
        /// <param name="configure">Optional table/column and endpoint-path configuration.</param>
        public static IServiceCollection AddBifrostLocalAuth(
            this IServiceCollection services,
            string connectionString,
            Action<BifrostQL.Server.Auth.LocalAuthOptions>? configure = null)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("A connection string is required for local auth.", nameof(connectionString));

            var options = new BifrostQL.Server.Auth.LocalAuthOptions();
            configure?.Invoke(options);

            var connFactory = DbConnFactoryResolver.Create(connectionString);

            services.AddSingleton(options);
            services.AddSingleton(connFactory);
            services.AddSingleton(sp => new BifrostQL.Server.Auth.LocalUserStore(
                sp.GetRequiredService<IDbConnFactory>(),
                sp.GetRequiredService<BifrostQL.Server.Auth.LocalAuthOptions>()));

            services
                .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(cookieOptions =>
                {
                    cookieOptions.LoginPath = options.LoginPath;
                    cookieOptions.LogoutPath = options.LogoutPath;
                });

            return services;
        }

        /// <summary>
        /// Registers OIDC claim mappers so authenticated Microsoft 365 and Google principals
        /// normalize to the same <see cref="Core.Auth.AppIdentity"/> contract local auth
        /// produces. Each mapper is keyed by the issuer URL of the OIDC provider it maps;
        /// <c>UseUiAuth()</c> selects a mapper by the principal's <c>iss</c> claim and
        /// re-issues the cookie in the shared local-auth claim shape. Pair with the
        /// <c>AddOpenIdConnect("oauth2", ...)</c> wiring already configured by AddBifrostQL.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">
        /// Registers issuer-to-mapper pairs (e.g. the Entra tenant authority to a
        /// <see cref="BifrostQL.Server.Auth.Microsoft365ClaimMapper"/>, Google's issuer to a
        /// <see cref="BifrostQL.Server.Auth.GoogleClaimMapper"/>).
        /// </param>
        public static IServiceCollection AddBifrostOidcClaimMappers(
            this IServiceCollection services,
            Action<BifrostQL.Server.Auth.OidcClaimMapperBuilder> configure)
        {
            if (configure == null)
                throw new ArgumentException("A mapper configuration callback is required.", nameof(configure));

            var builder = new BifrostQL.Server.Auth.OidcClaimMapperBuilder();
            configure(builder);

            services.AddSingleton(new BifrostQL.Server.Auth.OidcClaimMapperRegistry(builder.Build()));
            return services;
        }
    }
}
