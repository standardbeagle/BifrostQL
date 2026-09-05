using BifrostQL.Core.Modules;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Finding 12: profile selection is limited to the explicit header and query channels;
    /// the "any trailing path segment is a profile name" fallback is removed, and unknown-
    /// profile errors never echo raw attacker-controlled input verbatim.
    /// </summary>
    public class BifrostProfileResolverTests
    {
        [Fact]
        public void PathSegment_IsNoLongerTreatedAsProfileName()
        {
            var context = new DefaultHttpContext();
            // After app.Map("/graphql"), Path holds the remainder, e.g. "/v1".
            context.Request.Path = "/v1";

            BifrostProfileResolver.ResolveProfileName(context).Should().BeNull(
                "a trailing path segment must not be interpreted as a profile selector");
        }

        [Fact]
        public void HeaderChannel_StillResolvesProfileName()
        {
            var context = new DefaultHttpContext();
            context.Request.Headers["X-BifrostQL-Profile"] = "reporting";

            BifrostProfileResolver.ResolveProfileName(context).Should().Be("reporting");
        }

        [Fact]
        public void QueryChannel_StillResolvesProfileName()
        {
            var context = new DefaultHttpContext();
            context.Request.QueryString = new QueryString("?profile=reporting");

            BifrostProfileResolver.ResolveProfileName(context).Should().Be("reporting");
        }

        [Fact]
        public void UnknownProfile_WithUnsafeName_DoesNotEchoRawInput()
        {
            var registry = new BifrostProfileRegistry();
            var context = new DefaultHttpContext();
            var attack = "<script>alert(1)</script>";
            context.Request.Headers["X-BifrostQL-Profile"] = attack;

            var result = BifrostProfileResolver.Resolve(registry, context);

            result.HasError.Should().BeTrue();
            result.ErrorMessage.Should().NotContain(attack,
                "attacker-controlled profile text must not be reflected into the error verbatim");
            result.ErrorMessage.Should().Be("Unknown profile.");
        }

        [Fact]
        public void UnknownProfile_WithSafeName_MayEchoTheName()
        {
            var registry = new BifrostProfileRegistry();
            var context = new DefaultHttpContext();
            context.Request.Headers["X-BifrostQL-Profile"] = "reporting";

            var result = BifrostProfileResolver.Resolve(registry, context);

            result.HasError.Should().BeTrue();
            result.ErrorMessage.Should().Be("Unknown profile 'reporting'.");
        }

        /// <summary>
        /// Criterion 2: the literal name "default" is no longer special-cased. A profile
        /// registered under the name "default" must resolve THROUGH the registry so its
        /// curated module list is applied — proving the synthetic fallback no longer shadows
        /// a registry-registered "default".
        /// </summary>
        [Fact]
        public void RegisteredDefaultProfile_ResolvesThroughRegistry_WithCuratedModules()
        {
            var registry = new BifrostProfileRegistry();
            var registered = new BifrostProfile { Name = "default", Modules = new[] { "app-report" } };
            registry.Add(registered);

            var context = new DefaultHttpContext();
            context.Request.QueryString = new QueryString("?profile=default");

            var result = BifrostProfileResolver.Resolve(registry, context);

            result.HasError.Should().BeFalse();
            result.Profile.Should().BeSameAs(registered,
                "a registered 'default' must resolve through the registry, not the synthetic fallback");
            result.ActiveProfile.Modules.Should().BeEquivalentTo(new[] { "app-report" },
                "the registered profile's curated module list must be applied");
        }

        /// <summary>
        /// Criterion 5: an explicit request for the reserved <see cref="ProfileNames.System.Default"/>
        /// (".default") is rejected as an unknown profile (fail-closed) — it is never in the
        /// registry because the reserved namespace is not registrable, so the lookup misses.
        /// </summary>
        [Fact]
        public void SystemDefaultProfile_RequestedExplicitly_RejectedAsUnknown()
        {
            var registry = new BifrostProfileRegistry();
            var context = new DefaultHttpContext();
            context.Request.QueryString = new QueryString($"?profile={ProfileNames.System.Default}");

            var result = BifrostProfileResolver.Resolve(registry, context);

            result.HasError.Should().BeTrue("the reserved system profile cannot be selected by a request");
            result.Profile.Should().BeNull();
            result.ErrorMessage.Should().Be($"Unknown profile '{ProfileNames.System.Default}'.");
        }

        /// <summary>
        /// An unspecified profile resolves to the system default, which carries an explicit
        /// EMPTY (non-null) module list — the fail-closed shape, not the looser null.
        /// </summary>
        [Fact]
        public void NoProfileRequested_ResolvesToSystemDefault_WithEmptyModules()
        {
            var registry = new BifrostProfileRegistry();
            var context = new DefaultHttpContext();

            var result = BifrostProfileResolver.Resolve(registry, context);

            result.HasError.Should().BeFalse();
            result.ProfileName.Should().BeNull();
            result.ActiveProfile.Name.Should().Be(ProfileNames.System.Default);
            result.ActiveProfile.Modules.Should().NotBeNull("empty-array, not null, drives the fail-closed filter");
            result.ActiveProfile.Modules.Should().BeEmpty();
        }

        /// <summary>
        /// LOW bundle item 2: the role-gate failure must be a CONSTANT message carrying
        /// neither the profile name nor the required role — two different role-gated
        /// profiles produce byte-identical errors to an unauthenticated caller, and the
        /// role is logged server-side only.
        /// </summary>
        [Fact]
        public void RoleGatedProfile_Unauthenticated_ErrorIsConstant_AndLoggedServerSide()
        {
            var registry = new BifrostProfileRegistry();
            registry.Add(new BifrostProfile { Name = "alpha", RequireRole = "admin" });
            registry.Add(new BifrostProfile { Name = "beta", RequireRole = "superuser" });

            var alpha = ResolveRoleGated(registry, "alpha", authenticated: false, roles: Array.Empty<string>());
            var beta = ResolveRoleGated(registry, "beta", authenticated: false, roles: Array.Empty<string>());

            alpha.result.HasError.Should().BeTrue();
            alpha.result.ErrorMessage.Should().Be(beta.result.ErrorMessage,
                "the same condition must produce the same wire shape across profiles");
            alpha.result.ErrorMessage.Should().NotContain("alpha").And.NotContain("admin")
                .And.NotContain("beta").And.NotContain("superuser");
            alpha.logger.Messages.Should().Contain(m => m.Contains("admin"),
                "the required role is diagnostic detail: logged server-side, never on the wire");
        }

        /// <summary>
        /// Same invariant for an authenticated caller lacking the required role: constant
        /// message, no profile/role names on the wire, role logged server-side.
        /// </summary>
        [Fact]
        public void RoleGatedProfile_WrongRole_ErrorIsConstant_AndLoggedServerSide()
        {
            var registry = new BifrostProfileRegistry();
            registry.Add(new BifrostProfile { Name = "alpha", RequireRole = "admin" });
            registry.Add(new BifrostProfile { Name = "beta", RequireRole = "superuser" });

            var alpha = ResolveRoleGated(registry, "alpha", authenticated: true, roles: new[] { "viewer" });
            var beta = ResolveRoleGated(registry, "beta", authenticated: true, roles: new[] { "viewer" });

            alpha.result.HasError.Should().BeTrue();
            alpha.result.ErrorMessage.Should().Be(beta.result.ErrorMessage);
            alpha.result.ErrorMessage.Should().NotContain("alpha").And.NotContain("admin")
                .And.NotContain("beta").And.NotContain("superuser");
            alpha.logger.Messages.Should().Contain(m => m.Contains("admin"));
        }

        private static (BifrostProfileResolution result, ListLogger logger) ResolveRoleGated(
            BifrostProfileRegistry registry, string profile, bool authenticated, string[] roles)
        {
            var context = new DefaultHttpContext();
            context.Request.Headers["X-BifrostQL-Profile"] = profile;
            if (authenticated)
            {
                var identity = new ClaimsIdentity(authenticationType: "test");
                foreach (var role in roles)
                    identity.AddClaim(new Claim(ClaimTypes.Role, role));
                context.User = new ClaimsPrincipal(identity);
            }

            var logger = new ListLogger();
            return (BifrostProfileResolver.Resolve(registry, context, logger), logger);
        }

        private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
        {
            public List<string> Messages { get; } = new();

            IDisposable? Microsoft.Extensions.Logging.ILogger.BeginScope<TState>(TState state) => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(
                Microsoft.Extensions.Logging.LogLevel logLevel,
                Microsoft.Extensions.Logging.EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => Messages.Add(formatter(state, exception));
        }
    }
}
