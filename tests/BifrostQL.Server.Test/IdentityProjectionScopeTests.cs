using System.Collections;
using System.Net.WebSockets;
using System.Security.Claims;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Schema;
using FluentAssertions;
using GraphQL;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// The HTTP identity projection is memoized per DI scope (invariant 13: per request, never per
/// session). An HttpContext can outlive a request: a WebSocket session, a SignalR connection, or
/// the binary transport's per-message scopes all run on ONE HttpContext. Each fact holds one
/// long-lived HttpContext and drives the real registrar's scoped services, so a memo kept on the
/// HttpContext (rather than in the scope) serves a grant revoked between two messages.
/// </summary>
public sealed class IdentityProjectionScopeTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private static readonly string[] Grants = { "payments.create" };

    [Fact]
    public async Task PolicyGate_TwoScopesOnOneLongLivedContext_EachRunTheResolver_AndARevokedGrantTakesEffect()
    {
        var host = new Host();
        var http = host.LongLivedContext(webSocket: false);

        await using (var message1 = host.Root.CreateAsyncScope())
        {
            var gate = await message1.ServiceProvider.GetRequiredService<IPolicyGateAccessor>().GetAsync();
            gate.CanAct("main.payments", PolicyAction.Create).Allowed.Should().BeTrue("the grant is live");
        }

        host.Revoked = true;

        await using (var message2 = host.Root.CreateAsyncScope())
        {
            var gate = await message2.ServiceProvider.GetRequiredService<IPolicyGateAccessor>().GetAsync();
            gate.CanAct("main.payments", PolicyAction.Create).Allowed.Should().BeFalse(
                "a grant revoked between two messages takes effect on the next one, never at reconnect");
        }

        host.ResolverCalls.Should().Be(2, "each scope projects the caller once");
        http.User.Should().NotBeNull();
    }

    [Fact]
    public async Task UserContext_OnAWebSocketContext_ProjectsOnEachCall_AndARevokedGrantTakesEffect()
    {
        var host = new Host();
        var http = host.LongLivedContext(webSocket: true);

        var first = await http.GetBifrostUserContextAsync();
        host.Revoked = true;
        var second = await http.GetBifrostUserContextAsync();

        PolicyIdentity.ExtractPermissions(first).Should().Contain(Grants);
        PolicyIdentity.ExtractPermissions(second).Should().NotContain(Grants,
            "a WebSocket's HttpContext spans the whole session, so its request scope is no per-request memo");
        host.ResolverCalls.Should().Be(2);
    }

    [Theory]
    [InlineData("/hub")]
    [InlineData("/connection")]
    public async Task UserContext_OnASignalRSseOrLongPollingContext_ProjectsOnEachCall_AndARevokedGrantTakesEffect(
        string transportPath)
    {
        // A hub (or a raw connection handler) on SSE or long polling: the connection's HttpContext
        // is no WebSocket request, and the long-polling clone carries the transport endpoint too.
        // The default overload memoized in its connection-lifetime RequestServices.
        var host = new Host();
        var http = host.LongLivedContext(webSocket: false);
        http.SetEndpoint(await SignalRTransportEndpointAsync(transportPath));

        var first = await http.GetBifrostUserContextAsync();
        host.Revoked = true;
        var second = await http.GetBifrostUserContextAsync();

        PolicyIdentity.ExtractPermissions(first).Should().Contain(Grants);
        PolicyIdentity.ExtractPermissions(second).Should().NotContain(Grants,
            "a SignalR transport's HttpContext spans the connection, so its request scope is no per-request memo");
        host.ResolverCalls.Should().Be(2);
    }

    [Fact]
    public async Task UserContext_InTwoHubInvocationScopes_OnOneLongLivedContext_AndARevokedGrantTakesEffect()
    {
        // A SignalR hub on a non-WebSocket transport (SSE, long polling): the connection's
        // HttpContext is no WebSocket request, so only the invocation's scope bounds the memo.
        var host = new Host();
        var http = host.LongLivedContext(webSocket: false);

        await using var invocation1 = host.Root.CreateAsyncScope();
        var first = await http.GetBifrostUserContextAsync(invocation1.ServiceProvider);
        var again = await http.GetBifrostUserContextAsync(invocation1.ServiceProvider);
        host.Revoked = true;
        await using var invocation2 = host.Root.CreateAsyncScope();
        var second = await http.GetBifrostUserContextAsync(invocation2.ServiceProvider);

        PolicyIdentity.ExtractPermissions(first).Should().Contain(Grants);
        PolicyIdentity.ExtractPermissions(again).Should().Contain(Grants);
        PolicyIdentity.ExtractPermissions(second).Should().NotContain(Grants,
            "the next invocation's scope projects afresh");
        host.ResolverCalls.Should().Be(2, "one projection per invocation scope");
    }

    [Fact]
    public async Task BinaryMessageProjection_AndAHookGateInTheSameScope_RunTheResolverOnce()
    {
        var host = new Host();
        var http = host.LongLivedContext(webSocket: true);

        await using var message = host.Root.CreateAsyncScope();
        var (outcome, _) = await BifrostIdentityGate.ProjectAsync(http, message.ServiceProvider);
        var gate = await message.ServiceProvider.GetRequiredService<IPolicyGateAccessor>().GetAsync();

        outcome.Should().Be(BifrostIdentityOutcome.Projected);
        gate.CanAct("main.payments", PolicyAction.Create).Allowed.Should().BeTrue();
        host.ResolverCalls.Should().Be(1, "the message's projection and a hook's gate share the message scope's memo");
    }

    [Fact]
    public async Task ConcurrentUserContextAndGateCallers_InOneRequest_RunTheResolverOnce()
    {
        // The first caller is held inside the resolver while the second asks for the gate. The
        // memo's projection must already be published by then, so the second joins it.
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var host = new Host(onResolve: call =>
        {
            if (call == 1)
            {
                entered.Set();
                release.Wait(Budget);
            }
        });
        await using var request = host.Root.CreateAsyncScope();
        var http = host.RequestContext(request.ServiceProvider);

        var first = Task.Run(() => http.GetBifrostUserContextAsync());
        entered.Wait(Budget).Should().BeTrue("the first caller reaches the resolver");
        var second = request.ServiceProvider.GetRequiredService<IPolicyGateAccessor>().GetAsync().AsTask();
        release.Set();
        await Task.WhenAll(first, second).WaitAsync(Budget);

        host.ResolverCalls.Should().Be(1, "concurrent callers in one request share one projection");
        PolicyIdentity.ExtractPermissions(await first).Should().Contain(Grants);
        (await second).CanAct("main.payments", PolicyAction.Create).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task ACallerMutatingItsPermissions_DoesNotChangeTheGateBuiltLater()
    {
        var host = new Host();
        await using var request = host.Root.CreateAsyncScope();
        var http = host.RequestContext(request.ServiceProvider);

        var first = await http.GetBifrostUserContextAsync();
        var permissions = (IList)first[MetadataKeys.Auth.DefaultPermissionsContextKey]!;
        permissions[permissions.IndexOf("payments.create")] = "scribbled-by-the-first-caller";
        var gate = await request.ServiceProvider.GetRequiredService<IPolicyGateAccessor>().GetAsync();

        host.ResolverCalls.Should().Be(1, "the gate reuses the memoized projection");
        gate.CanAct("main.payments", PolicyAction.Create).Allowed.Should().BeTrue(
            "one caller's writes into a collection value never reach another caller's context");
    }

    [Fact]
    public async Task AHostFactorysListValue_IsFrozen_SoACallerMutatingItDoesNotChangeTheGateBuiltLater()
    {
        var hostPermissions = new List<string> { "payments.create" };
        var host = new Host(factory: new ListValuedFactory(hostPermissions));
        await using var request = host.Root.CreateAsyncScope();
        var http = host.RequestContext(request.ServiceProvider);

        var first = await http.GetBifrostUserContextAsync();
        ((IList)first[MetadataKeys.Auth.DefaultPermissionsContextKey]!)[0] = "scribbled-by-the-first-caller";
        hostPermissions.Add("payments.refund");
        var gate = await request.ServiceProvider.GetRequiredService<IPolicyGateAccessor>().GetAsync();

        gate.CanAct("main.payments", PolicyAction.Create).Allowed.Should().BeTrue(
            "the gate's context is a copy of the frozen memo, not the first caller's list");
        PolicyIdentity.ExtractPermissions(await http.GetBifrostUserContextAsync()).Should().Equal(
            new[] { "payments.create" }, "the host's later write into its own list never reaches the memo");
        first[MetadataKeys.Auth.DefaultPermissionsContextKey].Should().BeOfType<string[]>(
            "a collection value is frozen into an array the caller owns");
    }

    [Fact]
    public async Task AStepUpIdentityAddedToTheSamePrincipal_IsProjectedAfresh()
    {
        var host = new Host();
        await using var request = host.Root.CreateAsyncScope();
        var http = host.RequestContext(request.ServiceProvider);

        var before = await http.GetBifrostUserContextAsync();
        http.User.AddIdentity(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "approver") }, "step-up"));
        var after = await http.GetBifrostUserContextAsync();

        PolicyIdentity.FromUserContext(before).Roles.Should().NotContain("approver");
        PolicyIdentity.FromUserContext(after).Roles.Should().Contain("approver",
            "an identity added to the same principal instance re-projects, never serves the old memo");
        host.ResolverCalls.Should().Be(2);
    }

    [Fact]
    public async Task AClaimRemovedFromTheSamePrincipal_IsProjectedAfresh()
    {
        var host = new Host();
        await using var request = host.Root.CreateAsyncScope();
        var http = host.RequestContext(request.ServiceProvider);

        var before = await http.GetBifrostUserContextAsync();
        var identity = (ClaimsIdentity)http.User.Identity!;
        identity.RemoveClaim(identity.FindFirst(ClaimTypes.Role));
        var after = await http.GetBifrostUserContextAsync();

        PolicyIdentity.FromUserContext(before).Roles.Should().Contain("clerk");
        PolicyIdentity.FromUserContext(after).Roles.Should().NotContain("clerk",
            "a claim removed from the same principal instance re-projects, never serves the old memo");
        host.ResolverCalls.Should().Be(2);
    }

    [Fact]
    public async Task UserContext_ProjectedInTheRootProvider_ProjectsOnEachCall_AndARevokedGrantTakesEffect()
    {
        // A host auth handler that caches one ClaimsPrincipal per API key hands the SAME principal
        // instance to every request. A memo resolved from the root provider lives for the process,
        // so it served the first request's grants to every later one.
        var host = new Host(validateScopes: false);
        var http = host.LongLivedContext(webSocket: false);

        var first = await http.GetBifrostUserContextAsync(host.Root);
        host.Revoked = true;
        var second = await http.GetBifrostUserContextAsync(host.Root);

        PolicyIdentity.ExtractPermissions(first).Should().Contain(Grants);
        PolicyIdentity.ExtractPermissions(second).Should().NotContain(Grants,
            "the root provider is no request scope, so nothing projected in it is memoized");
        host.ResolverCalls.Should().Be(2);
    }

    [Fact]
    public async Task UserContext_ProjectedInTheRootProvider_WithScopeValidation_IsRefused_NotAnUnhandledFault()
    {
        // With scope validation on, resolving the scoped memo from the root provider threw outside
        // the projection's try, and the host answered 500. The scoped grant resolver cannot be
        // resolved from the root either, so the caller is refused (403), never served.
        var host = new Host();
        var http = host.LongLivedContext(webSocket: false);

        var call = () => http.GetBifrostUserContextAsync(host.Root);

        await call.Should().ThrowAsync<BifrostIdentityRejectedException>();
        var (outcome, userContext) = await BifrostIdentityGate.ProjectAsync(http, host.Root);
        outcome.Should().Be(BifrostIdentityOutcome.Unprojectable);
        userContext.Should().BeEmpty();
        host.ResolverCalls.Should().Be(0);
    }

    [Fact]
    public async Task ACaptiveGateAccessor_RefusesLoudly_RatherThanServeOneCallersGateToAnother()
    {
        // A singleton that captures IPolicyGateAccessor holds one resolved from the root provider.
        // Its gate was memoized for the process: the second caller got the first caller's gate.
        var host = new Host(validateScopes: false);
        var accessor = host.Root.GetRequiredService<IPolicyGateAccessor>();

        await using var request1 = host.Root.CreateAsyncScope();
        host.RequestContext(request1.ServiceProvider, userId: "user-1");
        var first = async () => await accessor.GetAsync();
        (await first.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*resolved from the root service provider*");

        await using var request2 = host.Root.CreateAsyncScope();
        host.RequestContext(request2.ServiceProvider, userId: "user-2");
        host.Revoked = true;
        var second = async () => await accessor.GetAsync();
        await second.Should().ThrowAsync<InvalidOperationException>(
            "a captive accessor never serves one caller's gate to another");
        host.ResolverCalls.Should().Be(0, "a captive accessor projects no one");
    }

    // ---- fixtures ----

    /// <summary>The endpoint the production <c>MapHub</c> / <c>MapConnectionHandler</c> builds for
    /// the transport route (not the negotiate route), with its real metadata.</summary>
    private static async Task<Endpoint> SignalRTransportEndpointAsync(string path)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSignalR();
        await using var app = builder.Build();
        app.MapHub<ProbeHub>("/hub");
        app.MapConnectionHandler<ProbeConnectionHandler>("/connection");
        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == path);
    }

    private sealed class ProbeHub : Hub;

    private sealed class ProbeConnectionHandler : ConnectionHandler
    {
        public override Task OnConnectedAsync(ConnectionContext connection) => Task.CompletedTask;
    }

    private sealed class ListValuedFactory : IBifrostAuthContextFactory
    {
        private readonly List<string> _permissions;
        public ListValuedFactory(List<string> permissions) => _permissions = permissions;

        public IDictionary<string, object?> CreateUserContext(HttpContext context) => new Dictionary<string, object?>
        {
            [MetadataKeys.Auth.DefaultUserIdContextKey] = "user-1",
            [MetadataKeys.Auth.DefaultPermissionsContextKey] = _permissions,
        };
    }

    private sealed class Host
    {
        private readonly HttpContextHolder _accessor = new();
        private int _resolverCalls;

        public Host(Action<int>? onResolve = null, IBifrostAuthContextFactory? factory = null, bool validateScopes = true)
        {
            var services = new ServiceCollection();
            if (factory is not null)
                services.AddSingleton(factory);
            BifrostServiceRegistrar.RegisterGraphQlAndAuth(services, false, null, 10, null, null);
            var models = new PathCache<Inputs>();
            models.AddLoader("/graphql", () =>
                Task.FromResult(new Inputs(new Dictionary<string, object?> { ["model"] = PaymentsModel() })));
            services.AddSingleton(models);
            services.AddSingleton<IHttpContextAccessor>(_accessor);
            services.AddBifrostGrantResolver((identity, sp, ct) =>
            {
                // Counted unconditionally: `onResolve?.Invoke(Increment(...))` skips the increment
                // when no callback is set.
                var call = Interlocked.Increment(ref _resolverCalls);
                onResolve?.Invoke(call);
                return new ValueTask<IReadOnlyCollection<string>>(Revoked ? Array.Empty<string>() : Grants);
            });
            Root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = validateScopes });
        }

        public IServiceProvider Root { get; }
        public volatile bool Revoked;
        public int ResolverCalls => Volatile.Read(ref _resolverCalls);

        /// <summary>A connection-lifetime HttpContext whose RequestServices is the connection's scope.</summary>
        public HttpContext LongLivedContext(bool webSocket)
        {
            var http = RequestContext(Root.CreateScope().ServiceProvider);
            if (webSocket)
                http.Features.Set<IHttpWebSocketFeature>(new UpgradeRequest());
            return http;
        }

        public HttpContext RequestContext(IServiceProvider requestServices, string userId = "user-1")
        {
            var http = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Role, "clerk") }, "test")),
                RequestServices = requestServices,
            };
            http.Request.Path = "/payments";
            _accessor.HttpContext = http;
            return http;
        }
    }

    private sealed class UpgradeRequest : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => throw new NotSupportedException();
    }

    private sealed class HttpContextHolder : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private static IDbModel PaymentsModel()
    {
        var table = new DbTable
        {
            DbName = "payments",
            GraphQlName = "payments",
            NormalizedName = "payments",
            TableSchema = "main",
            ColumnLookup = new Dictionary<string, ColumnDto>(),
            GraphQlLookup = new Dictionary<string, ColumnDto>(),
            Metadata = new Dictionary<string, object?>
            {
                [MetadataKeys.Policy.Actions] = "read, create[payments.create]",
            },
        };
        return new DbModel
        {
            Tables = new IDbTable[] { table },
            Metadata = new Dictionary<string, object?>(),
        };
    }
}
