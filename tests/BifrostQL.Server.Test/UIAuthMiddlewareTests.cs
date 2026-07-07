using System.Security.Claims;
using System.Text.Encodings.Web;
using BifrostQL.Core.Auth;
using BifrostQL.Server;
using BifrostQL.Server.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Covers the OIDC issuer gate in <see cref="UIAuthMiddleware"/>: an
    /// authenticated token from an issuer this deployment has not mapped must be
    /// rejected, not silently read through the local claim path (which would drop
    /// its tenant/role claims). A local-auth principal (no issuer) passes through.
    /// </summary>
    public class UIAuthMiddlewareTests
    {
        private const string GoogleIssuer = "https://accounts.google.com";

        private static (RequestDelegate pipeline, Func<bool> nextCalled, IServiceProvider services) BuildPipeline()
        {
            var services = new ServiceCollection();
            services.AddSingleton(new OidcClaimMapperRegistry(
                new OidcClaimMapperBuilder().AddGoogle(GoogleIssuer).Build()));
            var sp = services.BuildServiceProvider();

            var called = new bool[1];
            var app = new ApplicationBuilder(sp);
            app.UseUiAuth();
            app.Run(_ => { called[0] = true; return Task.CompletedTask; });

            return (app.Build(), () => called[0], sp);
        }

        [Fact]
        public async Task UnmappedIssuer_IsRejectedAndDoesNotProceed()
        {
            var (pipeline, nextCalled, services) = BuildPipeline();
            var context = new DefaultHttpContext { RequestServices = services };
            context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("iss", "https://unknown-provider.test"),
                new Claim("sub", "attacker"),
            }, "oauth2"));

            await pipeline(context);

            context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
            nextCalled().Should().BeFalse("an unmapped OIDC issuer must not proceed with a stripped identity");
        }

        [Fact]
        public async Task LocalPrincipalWithoutIssuer_Proceeds()
        {
            var (pipeline, nextCalled, services) = BuildPipeline();
            var context = new DefaultHttpContext { RequestServices = services };
            context.User = LocalAuthEndpoint.BuildPrincipal(
                new AppIdentity(id: "local-1", provider: "local"));

            await pipeline(context);

            nextCalled().Should().BeTrue("a local-auth principal carries no issuer and is read locally");
        }

        // ---- Finding 7: Bearer/API clients get 401, not an interactive OIDC 302 redirect ----

        private static (RequestDelegate pipeline, Func<bool> nextCalled, IServiceProvider services) BuildChallengePipeline()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, StubOidcHandler>("oauth2", _ => { });
            var sp = services.BuildServiceProvider();

            var called = new bool[1];
            var app = new ApplicationBuilder(sp);
            app.UseUiAuth();
            app.Run(_ => { called[0] = true; return Task.CompletedTask; });
            return (app.Build(), () => called[0], sp);
        }

        [Fact]
        public async Task UnauthenticatedBearerRequest_Gets401_NotRedirect()
        {
            var (pipeline, nextCalled, services) = BuildChallengePipeline();
            var context = new DefaultHttpContext { RequestServices = services };
            context.User = new ClaimsPrincipal(new ClaimsIdentity()); // unauthenticated
            context.Request.Headers.Authorization = "Bearer some.invalid.token";

            await pipeline(context);

            context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized,
                "a Bearer/API client must get a 401, not a 302 login redirect");
            nextCalled().Should().BeFalse();
        }

        [Fact]
        public async Task UnauthenticatedJsonApiRequest_Gets401_NotRedirect()
        {
            var (pipeline, nextCalled, services) = BuildChallengePipeline();
            var context = new DefaultHttpContext { RequestServices = services };
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
            context.Request.Headers.Accept = "application/json";

            await pipeline(context);

            context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
            nextCalled().Should().BeFalse();
        }

        [Fact]
        public async Task UnauthenticatedBrowserRequest_GetsOidcChallenge_Not401()
        {
            var (pipeline, nextCalled, services) = BuildChallengePipeline();
            var context = new DefaultHttpContext { RequestServices = services };
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
            context.Request.Headers.Accept = "text/html,application/xhtml+xml";

            await pipeline(context);

            // The stub OIDC handler emits 302 on challenge; a browser navigation keeps the
            // interactive login redirect rather than a bare 401.
            context.Response.StatusCode.Should().Be(StatusCodes.Status302Found,
                "an interactive browser request keeps the OIDC login redirect");
            nextCalled().Should().BeFalse();
        }

        /// <summary>Minimal auth handler whose Challenge emits a 302, standing in for OIDC.</summary>
        private sealed class StubOidcHandler : AuthenticationHandler<AuthenticationSchemeOptions>
        {
            public StubOidcHandler(
                IOptionsMonitor<AuthenticationSchemeOptions> options,
                ILoggerFactory logger,
                UrlEncoder encoder)
                : base(options, logger, encoder) { }

            protected override Task<AuthenticateResult> HandleAuthenticateAsync()
                => Task.FromResult(AuthenticateResult.NoResult());

            protected override Task HandleChallengeAsync(AuthenticationProperties properties)
            {
                Response.StatusCode = StatusCodes.Status302Found;
                Response.Headers.Location = properties.RedirectUri ?? "/";
                return Task.CompletedTask;
            }
        }
    }
}
