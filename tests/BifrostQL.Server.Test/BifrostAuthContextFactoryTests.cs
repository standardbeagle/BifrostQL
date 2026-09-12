using System.Security.Claims;
using BifrostQL.Server.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Covers the shared <see cref="IBifrostAuthContextFactory"/> that every transport
    /// gate (HTTP, binary WebSocket, protocol frontend, workflow endpoints) uses to build
    /// the user context. The three security-relevant paths must hold at the factory level
    /// so no gate can drift: authenticated → full claim projection, unauthenticated →
    /// empty context, unmapped OIDC issuer → fail closed by throwing.
    /// </summary>
    public sealed class BifrostAuthContextFactoryTests
    {
        private static readonly BifrostAuthContextFactory Factory = BifrostAuthContextFactory.Instance;

        [Fact]
        public void CreateUserContext_AuthenticatedPrincipal_ProjectsIdentityIntoBifrostContext()
        {
            // Arrange: a local-auth principal (no issuer claim → local claim path).
            var context = new DefaultHttpContext { User = AuthenticatedLocalPrincipal() };

            // Act
            var userContext = Factory.CreateUserContext(context);

            // Assert: full BifrostContext projection — raw principal preserved plus
            // the legacy per-claim-type arrays.
            userContext.Should().BeOfType<BifrostContext>();
            userContext["user"].Should().BeSameAs(context.User);
            userContext.Should().ContainKey(ClaimTypes.NameIdentifier);
        }

        [Fact]
        public void CreateUserContext_UnauthenticatedRequest_YieldsEmptyMutableContext()
        {
            // Arrange: anonymous principal (no authentication type → IsAuthenticated false).
            var context = new DefaultHttpContext();

            // Act
            var userContext = Factory.CreateUserContext(context);

            // Assert: empty and NOT a BifrostContext — no identity keys may appear.
            userContext.Should().NotBeOfType<BifrostContext>();
            userContext.Should().BeEmpty();
            // Downstream (correlation id, profile key) writes into it; it must be mutable.
            userContext["probe"] = 1;
            userContext.Should().ContainKey("probe");
        }

        [Fact]
        public void CreateUserContext_UnmappedOidcIssuer_ThrowsFailClosed()
        {
            // Arrange: an authenticated principal carrying an issuer no mapper is
            // registered for. Reading it through the local claim path would strip its
            // tenant/role claims, so the factory must throw instead.
            var context = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "user-1"),
                    new Claim("iss", "https://idp.example.test"),
                }, authenticationType: "oidc")),
                RequestServices = new ServiceCollection()
                    .AddSingleton(new OidcClaimMapperRegistry(
                        Enumerable.Empty<KeyValuePair<string, IOidcClaimMapper>>()))
                    .BuildServiceProvider(),
            };

            // Act
            var act = () => Factory.CreateUserContext(context);

            // Assert
            act.Should().Throw<UnmappedOidcIssuerException>()
                .WithMessage("*https://idp.example.test*");
        }

        // The merge overload was removed: frontend-parsed wire context now rides
        // BifrostRequest.WireContext and is merged model-aware by WireContextMerger
        // (see WireContextMergerTests in BifrostQL.Core.Test).

        [Fact]
        public void Resolve_PrefersDiRegisteredFactory_FallsBackToSharedDefault()
        {
            // Arrange: a host that registered its own factory.
            var custom = new BifrostAuthContextFactory();
            var withOverride = new DefaultHttpContext
            {
                RequestServices = new ServiceCollection()
                    .AddSingleton<IBifrostAuthContextFactory>(custom)
                    .BuildServiceProvider(),
            };
            var withoutServices = new DefaultHttpContext();

            // Act + Assert
            BifrostAuthContextFactory.Resolve(withOverride).Should().BeSameAs(custom);
            BifrostAuthContextFactory.Resolve(withoutServices)
                .Should().BeSameAs(BifrostAuthContextFactory.Instance);
        }

        // ---- IGrantResolver hook (S2) ----

        [Fact]
        public void CreateUserContext_GrantResolverRegistered_UnionsGrantsIntoPermissions()
        {
            // Arrange: a local-auth principal plus a resolver granting "x".
            var context = new DefaultHttpContext
            {
                User = AuthenticatedLocalPrincipal(),
                RequestServices = new ServiceCollection()
                    .AddBifrostGrantResolver((identity, sp, ct) =>
                        new ValueTask<IReadOnlyCollection<string>>(new[] { "x" }))
                    .BuildServiceProvider(),
            };

            // Act
            var userContext = Factory.CreateUserContext(context);

            // Assert: the resolver's grant is unioned into the owned permissions key.
            BifrostQL.Core.Auth.PolicyIdentity.ExtractPermissions(userContext)
                .Should().Contain("x");
        }

        [Fact]
        public void CreateUserContext_ResolverThrows_YieldsEmptyPermissions_AndLogsWarning()
        {
            // Arrange: an OIDC identity whose mapper grants permission "x" — the
            // pre-resolver set is NON-empty, so the fail-closed wipe is observable.
            var loggerFactory = new ListLoggerFactory();
            var context = new DefaultHttpContext
            {
                User = OidcPrincipalWithPermissions(),
                RequestServices = new ServiceCollection()
                    .AddSingleton(new OidcClaimMapperRegistry(new[]
                    {
                        new KeyValuePair<string, IOidcClaimMapper>("https://idp.test", new PermissionMapper()),
                    }))
                    .AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(loggerFactory)
                    .AddBifrostGrantResolver((identity, sp, ct) =>
                        throw new InvalidOperationException("grant store down"))
                    .BuildServiceProvider(),
            };

            // Act
            var userContext = Factory.CreateUserContext(context);

            // Assert: the pre-resolver permission "x" is gone — a throwing resolver
            // never yields the pre-resolver set — and a Warning names the identity id.
            BifrostQL.Core.Auth.PolicyIdentity.ExtractPermissions(userContext).Should().BeEmpty();
            loggerFactory.Entries.Should().Contain(e =>
                e.Level == Microsoft.Extensions.Logging.LogLevel.Warning &&
                e.Message.Contains("user-1"));
        }

        [Fact]
        public void CreateUserContext_ResolverReturnsNull_TreatedAsEmpty_NoWarning()
        {
            // Arrange
            var loggerFactory = new ListLoggerFactory();
            var context = new DefaultHttpContext
            {
                User = AuthenticatedLocalPrincipal(),
                RequestServices = new ServiceCollection()
                    .AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(loggerFactory)
                    .AddBifrostGrantResolver((identity, sp, ct) =>
                        new ValueTask<IReadOnlyCollection<string>>((IReadOnlyCollection<string>)null!))
                    .BuildServiceProvider(),
            };

            // Act
            var userContext = Factory.CreateUserContext(context);

            // Assert: null is an empty grant set — the identity's own (empty) permissions
            // remain and nothing is logged.
            BifrostQL.Core.Auth.PolicyIdentity.ExtractPermissions(userContext).Should().BeEmpty();
            loggerFactory.Entries.Should().BeEmpty();
        }

        [Fact]
        public void CreateUserContext_NoResolver_PermissionsComeFromIdentityOnly()
        {
            // Arrange: no resolver registered — behaviour unchanged.
            var context = new DefaultHttpContext { User = AuthenticatedLocalPrincipal() };

            // Act
            var userContext = Factory.CreateUserContext(context);

            // Assert
            BifrostQL.Core.Auth.PolicyIdentity.ExtractPermissions(userContext).Should().BeEmpty();
        }

        private static ClaimsPrincipal OidcPrincipalWithPermissions() => new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "user-1"),
            new Claim("iss", "https://idp.test"),
        }, authenticationType: "oidc"));

        private sealed class PermissionMapper : IOidcClaimMapper
        {
            public string Provider => "oidc:test";
            public BifrostQL.Core.Auth.AppIdentity Map(ClaimsPrincipal principal) =>
                new("user-1", Provider, permissions: new[] { "x" });
        }

        private sealed class ListLoggerFactory : Microsoft.Extensions.Logging.ILoggerFactory
        {
            public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = new();
            public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) { }
            public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new ListLogger(Entries);
            public void Dispose() { }

            private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
            {
                private readonly List<(Microsoft.Extensions.Logging.LogLevel, string)> _entries;
                public ListLogger(List<(Microsoft.Extensions.Logging.LogLevel, string)> entries) => _entries = entries;
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
                public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
                public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
                    TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                {
                    lock (_entries) _entries.Add((logLevel, formatter(state, exception)));
                }
            }
        }

        private static ClaimsPrincipal AuthenticatedLocalPrincipal() => new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "user-1"),
            new Claim(ClaimTypes.Email, "alice@club.test"),
            new Claim(ClaimTypes.Role, "admin"),
        }, authenticationType: "local"));
    }
}
