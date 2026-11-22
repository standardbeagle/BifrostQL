using System.Security.Claims;
using BifrostQL.Server.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Microsoft.Extensions.Logging.Testing;

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

        /// <summary>The async projection every transport gate awaits; it runs the grant resolver.</summary>
        private static ValueTask<IDictionary<string, object?>> ProjectAsync(HttpContext context)
            => Factory.CreateUserContextAsync(context, context.RequestServices, CancellationToken.None);

        [Fact]
        public void CreateUserContext_SyncContract_WithAGrantResolver_RefusesInsteadOfBlocking()
        {
            // The synchronous public contract cannot await the resolver. It must neither block on
            // it nor serve the caller without its grants: it throws, which every gate maps to an
            // unprojectable identity (fail closed). The resolver is never invoked.
            var invoked = false;
            var context = new DefaultHttpContext
            {
                User = AuthenticatedLocalPrincipal(),
                RequestServices = new ServiceCollection()
                    .AddBifrostGrantResolver((identity, sp, ct) =>
                    {
                        invoked = true;
                        return new ValueTask<IReadOnlyCollection<string>>(new[] { "x" });
                    })
                    .BuildServiceProvider(),
            };

            var act = () => Factory.CreateUserContext(context);

            act.Should().Throw<InvalidOperationException>().WithMessage("*IGrantResolver*");
            invoked.Should().BeFalse();
        }

        [Fact]
        public async Task HostDecoratorFactory_AwaitsTheDefaultProjection_GateServesTheCallerWithGrants()
        {
            // A host factory that decorates the default one (DI decoration of the registered
            // IBifrostAuthContextFactory) must be able to run the grant resolver. The gate awaits
            // the decorator's async projection, which awaits the inner default's: the caller is
            // served with its grants and the decorator's own key, never refused.
            var context = new DefaultHttpContext
            {
                User = AuthenticatedLocalPrincipal(),
                RequestServices = new ServiceCollection()
                    .AddSingleton<IBifrostAuthContextFactory>(new DecoratingAuthContextFactory(Factory))
                    .AddBifrostGrantResolver(async (identity, sp, ct) =>
                    {
                        await Task.Yield();
                        return (IReadOnlyCollection<string>)new[] { "decorated.grant" };
                    })
                    .BuildServiceProvider(),
            };

            var userContext = await context.GetBifrostUserContextAsync();

            userContext.Should().ContainKey(DecoratingAuthContextFactory.MarkerKey,
                "the gate projected through the host's decorator, not around it");
            BifrostQL.Core.Auth.PolicyIdentity.ExtractPermissions(userContext)
                .Should().Contain("decorated.grant");
        }

        [Fact]
        public async Task CustomAdapter_PublicAsyncProjection_WithAsyncResolver_YieldsTheGrants()
        {
            // A third-party IProtocolAdapter projects its carrier through the public contract
            // (docs/src/content/docs/guides/protocol-adapters.md). With a resolver registered the
            // async projection delivers the grants instead of refusing the login.
            var services = new ServiceCollection()
                .AddBifrostGrantResolver(async (identity, sp, ct) =>
                {
                    await Task.Yield();
                    return (IReadOnlyCollection<string>)new[] { "adapter.grant" };
                })
                .BuildServiceProvider();
            IBifrostAuthContextFactory authFactory = Factory;
            var identityCarrier = new DefaultHttpContext { RequestServices = services, User = AuthenticatedLocalPrincipal() };

            var userContext = await authFactory.CreateUserContextAsync(
                identityCarrier, identityCarrier.RequestServices, CancellationToken.None);

            BifrostQL.Core.Auth.PolicyIdentity.ExtractPermissions(userContext)
                .Should().Contain("adapter.grant");
        }

        /// <summary>A host factory decorating another: it adds one key to the inner projection.</summary>
        private sealed class DecoratingAuthContextFactory : IBifrostAuthContextFactory
        {
            public const string MarkerKey = "host_decorated";
            private readonly IBifrostAuthContextFactory _inner;

            public DecoratingAuthContextFactory(IBifrostAuthContextFactory inner) => _inner = inner;

            public IDictionary<string, object?> CreateUserContext(HttpContext context)
                => Decorate(_inner.CreateUserContext(context));

            public async ValueTask<IDictionary<string, object?>> CreateUserContextAsync(
                HttpContext context, IServiceProvider? services, CancellationToken cancellationToken)
                => Decorate(await _inner.CreateUserContextAsync(context, services, cancellationToken));

            private static IDictionary<string, object?> Decorate(IDictionary<string, object?> userContext)
            {
                userContext[MarkerKey] = true;
                return userContext;
            }
        }


        [Fact]
        public async Task CreateUserContextAsync_GrantResolverRegistered_UnionsGrantsIntoPermissions()
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
            var userContext = await ProjectAsync(context);

            // Assert: the resolver's grant is unioned into the owned permissions key.
            BifrostQL.Core.Auth.PolicyIdentity.ExtractPermissions(userContext)
                .Should().Contain("x");
        }

        [Fact]
        public async Task CreateUserContextAsync_CallerCancelled_RethrowsInsteadOfDegradingToEmptyPermissions()
        {
            // A projection that lost its grants to the caller's own cancellation is no identity:
            // degrading it to empty permissions made it a valid projection the request memo kept.
            var context = new DefaultHttpContext
            {
                User = AuthenticatedLocalPrincipal(),
                RequestServices = new ServiceCollection()
                    .AddBifrostGrantResolver((identity, sp, ct) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        return new ValueTask<IReadOnlyCollection<string>>(new[] { "x" });
                    })
                    .BuildServiceProvider(),
            };
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            await FluentActions.Awaiting(() => BifrostAuthContextFactory.Instance
                    .CreateUserContextAsync(context, context.RequestServices, cancelled.Token).AsTask())
                .Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public async Task CreateUserContextAsync_ResolverCancelsOnItsOwn_StillFailsClosedToEmptyPermissions()
        {
            // The strict half: a resolver's own timeout, with the caller's token live, is a
            // throwing resolver like any other (E9), not a caller cancellation.
            var context = new DefaultHttpContext
            {
                User = AuthenticatedLocalPrincipal(),
                RequestServices = new ServiceCollection()
                    .AddBifrostGrantResolver((identity, sp, ct) =>
                        throw new TaskCanceledException("resolver timed out"))
                    .BuildServiceProvider(),
            };

            var userContext = await BifrostAuthContextFactory.Instance
                .CreateUserContextAsync(context, context.RequestServices, CancellationToken.None);

            BifrostQL.Core.Auth.PolicyIdentity.ExtractPermissions(userContext).Should().BeEmpty();
        }

        [Fact]
        public async Task CreateUserContextAsync_GrantResolver_ReceivesTheIdentityTenant()
        {
            // A host's resolver typically loads grants per (user, tenant). The identity it is
            // handed carries the tenant the identity mapper wrote (tenant_id), not null.
            BifrostQL.Core.Auth.AppIdentity? seen = null;
            var context = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "user-1"),
                    new Claim(LocalAuthClaims.Tenant, "acme"),
                }, authenticationType: "local")),
                RequestServices = new ServiceCollection()
                    .AddBifrostGrantResolver((identity, sp, ct) =>
                    {
                        seen = identity;
                        return new ValueTask<IReadOnlyCollection<string>>(Array.Empty<string>());
                    })
                    .BuildServiceProvider(),
            };

            await ProjectAsync(context);

            seen.Should().NotBeNull();
            seen!.TenantId.Should().Be("acme");
        }

        [Fact]
        public async Task CreateUserContextAsync_GrantResolver_SeesTheRawClaimsOfThePrincipal()
        {
            // With tenant-context-key: org_id fed by a raw org_id claim, the resolver gets
            // TenantId = null. The documented answer is to read the claim; the resolver is handed
            // no principal (and on pgwire/RESP/LDAP no IHttpContextAccessor), so the identity
            // carries the principal's claims, one string[] per claim type.
            BifrostQL.Core.Auth.AppIdentity? seen = null;
            var context = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "user-1"),
                    new Claim("org_id", "acme"),
                }, authenticationType: "local")),
                RequestServices = new ServiceCollection()
                    .AddBifrostGrantResolver((identity, sp, ct) =>
                    {
                        seen = identity;
                        return new ValueTask<IReadOnlyCollection<string>>(Array.Empty<string>());
                    })
                    .BuildServiceProvider(),
            };

            await ProjectAsync(context);

            seen.Should().NotBeNull();
            seen!.TenantId.Should().BeNull("control: the raw claim is not the identity's own tenant");
            seen.Claims.TryGetValue("Org_Id", out var org).Should().BeTrue("claim types compare case-insensitively");
            org.Should().BeEquivalentTo(new[] { "acme" });
        }

        [Fact]
        public async Task CreateUserContextAsync_ResolverThrows_YieldsEmptyPermissions_AndLogsWarning()
        {
            // Arrange: an OIDC identity whose mapper grants permission "x" — the
            // pre-resolver set is NON-empty, so the fail-closed wipe is observable.
            var log = new FakeLogCollector();
            var context = new DefaultHttpContext
            {
                User = OidcPrincipalWithPermissions(),
                RequestServices = new ServiceCollection()
                    .AddSingleton(new OidcClaimMapperRegistry(new[]
                    {
                        new KeyValuePair<string, IOidcClaimMapper>("https://idp.test", new PermissionMapper()),
                    }))
                    .AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(new Microsoft.Extensions.Logging.LoggerFactory(new[] { new FakeLoggerProvider(log) }))
                    .AddBifrostGrantResolver((identity, sp, ct) =>
                        throw new InvalidOperationException("grant store down"))
                    .BuildServiceProvider(),
            };

            // Act
            var userContext = await ProjectAsync(context);

            // Assert: the pre-resolver permission "x" is gone — a throwing resolver
            // never yields the pre-resolver set — and a Warning names the identity id.
            BifrostQL.Core.Auth.PolicyIdentity.ExtractPermissions(userContext).Should().BeEmpty();
            log.GetSnapshot().Should().Contain(e =>
                e.Level == Microsoft.Extensions.Logging.LogLevel.Warning &&
                e.Message.Contains("user-1"));
        }

        [Fact]
        public async Task CreateUserContextAsync_ResolverReturnsNull_TreatedAsEmpty_NoWarning()
        {
            // Arrange
            var log = new FakeLogCollector();
            var context = new DefaultHttpContext
            {
                User = AuthenticatedLocalPrincipal(),
                RequestServices = new ServiceCollection()
                    .AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(new Microsoft.Extensions.Logging.LoggerFactory(new[] { new FakeLoggerProvider(log) }))
                    .AddBifrostGrantResolver((identity, sp, ct) =>
                        new ValueTask<IReadOnlyCollection<string>>((IReadOnlyCollection<string>)null!))
                    .BuildServiceProvider(),
            };

            // Act
            var userContext = await ProjectAsync(context);

            // Assert: null is an empty grant set — the identity's own (empty) permissions
            // remain and nothing is logged.
            BifrostQL.Core.Auth.PolicyIdentity.ExtractPermissions(userContext).Should().BeEmpty();
            log.GetSnapshot().Should().BeEmpty();
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

        private static ClaimsPrincipal AuthenticatedLocalPrincipal() => new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "user-1"),
            new Claim(ClaimTypes.Email, "alice@club.test"),
            new Claim(ClaimTypes.Role, "admin"),
        }, authenticationType: "local"));
    }
}
