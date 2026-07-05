using System.Security.Claims;
using BifrostQL.Core.Auth;
using BifrostQL.Server;
using BifrostQL.Server.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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
    }
}
