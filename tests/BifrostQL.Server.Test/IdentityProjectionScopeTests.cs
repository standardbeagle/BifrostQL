using System.Net.WebSockets;
using System.Security.Claims;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Schema;
using FluentAssertions;
using GraphQL;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// Identity is projected afresh on every call, never per session (invariant 13). An HttpContext
/// can outlive a request: a WebSocket session, a SignalR connection, or the binary transport's
/// per-message scopes all run on ONE HttpContext. Each fact holds one long-lived HttpContext and
/// drives the real registrar's scoped services, so anything that kept a projection on the
/// HttpContext or its connection scope would serve a grant revoked between two messages.
/// </summary>
public sealed class IdentityProjectionScopeTests
{
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
            "a WebSocket's HttpContext spans the whole session, and each call projects afresh");
        host.ResolverCalls.Should().Be(2);
    }

    [Fact]
    public async Task UserContextAndGate_InOneRequestScope_EachProjectAfresh_AndEachCallerOwnsItsCopy()
    {
        // Nothing memoizes the projection, not even within one request: the endpoint's own call
        // and the policy gate each run the resolver, so a grant revoked between them is not
        // served to the gate, and a caller's writes into its copy never reach the next caller.
        var host = new Host();
        await using var request = host.Root.CreateAsyncScope();
        var http = host.RequestContext(request.ServiceProvider);

        var first = await http.GetBifrostUserContextAsync();
        first["scribble"] = "written by the first caller";
        host.Revoked = true;
        var second = await http.GetBifrostUserContextAsync();
        var gate = await request.ServiceProvider.GetRequiredService<IPolicyGateAccessor>().GetAsync();

        PolicyIdentity.ExtractPermissions(first).Should().Contain(Grants);
        PolicyIdentity.ExtractPermissions(second).Should().NotContain(Grants,
            "the second call in the same request projects afresh");
        second.Should().NotContainKey("scribble");
        gate.CanAct("main.payments", PolicyAction.Create).Allowed.Should().BeFalse(
            "the gate projects afresh too, after the revocation");
        host.ResolverCalls.Should().Be(3);
    }

    [Fact]
    public async Task AResolverThrowingANonCancellationException_UnderACancelledToken_IsRefused_NotDegraded()
    {
        // SqlClient raises SqlException ("Operation cancelled by user"), not an
        // OperationCanceledException, when the caller's token cancels a command. Keyed on the
        // exception type, the factory degraded that to empty permissions (E9) and served it.
        using var aborted = new CancellationTokenSource();
        var host = new Host(onResolve: (call, ct) =>
        {
            if (call != 1) return;
            aborted.Cancel();
            if (ct.IsCancellationRequested)
                throw new InvalidOperationException("Operation cancelled by user.");
        });
        await using var request = host.Root.CreateAsyncScope();
        var http = host.RequestContext(request.ServiceProvider);
        http.RequestAborted = aborted.Token;

        var (cancelled, _) = await BifrostIdentityGate.ProjectAsync(http);
        http.RequestAborted = CancellationToken.None;
        var (next, userContext) = await BifrostIdentityGate.ProjectAsync(http);

        cancelled.Should().Be(BifrostIdentityOutcome.Unprojectable,
            "a projection whose resolver failed under the caller's cancelled token is no identity");
        next.Should().Be(BifrostIdentityOutcome.Projected);
        PolicyIdentity.ExtractPermissions(userContext).Should().Contain(Grants, "the next call projects afresh");
        host.ResolverCalls.Should().Be(2);
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

    private sealed class Host
    {
        private readonly HttpContextHolder _accessor = new();
        private int _resolverCalls;

        public Host(Action<int, CancellationToken>? onResolve = null, bool validateScopes = true)
        {
            var services = new ServiceCollection();
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
                onResolve?.Invoke(call, ct);
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
