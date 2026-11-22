using System.Security.Claims;
using BifrostQL.Core.Auth;
using Microsoft.AspNetCore.Http;

namespace BifrostQL.Server
{
    /// <summary>
    /// The identity projection memo for one DI scope, registered scoped: one per request, per
    /// binary WebSocket message, per hub invocation (invariant 13: per request, never per session).
    /// It lives in the scope, not on the <see cref="HttpContext"/>, because one HttpContext can
    /// span a whole WebSocket or SignalR session while every message or invocation gets its own
    /// scope; a memo on the HttpContext served a revoked grant until reconnect. Never used from the
    /// root provider (<see cref="RootServiceProvider.IsRoot"/>): resolved there, it would span
    /// every request.
    ///
    /// <para>Concurrent callers in one scope share one projection task, published lock-free by
    /// compare-exchange (the <see cref="PolicyGateAccessor"/> pattern), so the grant resolver runs
    /// once. Only a <see cref="BifrostIdentityOutcome.Projected"/> result is reused: a refusal, or a
    /// task that faulted, is replaced by the next caller. A slot is reused only for the principal
    /// and factory instances it was built from, and only while the principal's shape (identity
    /// count, total claim count) is unchanged, so a step-up identity or a removed claim on the same
    /// principal instance within the scope re-projects. A same-count swap (one claim removed and
    /// another added) is not detected; that window is one DI scope. Every caller gets its own copy
    /// of the context.</para>
    /// </summary>
    internal sealed class IdentityProjectionMemo
    {
        private Slot? _slot;

        private sealed record Slot(
            ClaimsPrincipal User, PrincipalShape Shape, IBifrostAuthContextFactory Factory,
            Task<BifrostIdentityProjection> Projection)
        {
            /// <summary>A refusal or a fault is never memoized; an in-flight projection is shared.</summary>
            public bool IsReusable => !Projection.IsCompleted
                || (Projection.IsCompletedSuccessfully && Projection.Result.Outcome == BifrostIdentityOutcome.Projected);
        }

        /// <summary>A cheap fingerprint of a principal's in-place mutations (<c>AddIdentity</c>,
        /// <c>AddClaim</c>, <c>RemoveClaim</c>).</summary>
        private readonly record struct PrincipalShape(int Identities, int Claims)
        {
            public static PrincipalShape Of(ClaimsPrincipal user)
            {
                var identities = 0;
                var claims = 0;
                foreach (var identity in user.Identities)
                {
                    identities++;
                    claims += identity.Claims.Count();
                }
                return new(identities, claims);
            }
        }

        internal async ValueTask<BifrostIdentityProjection> ProjectAsync(
            HttpContext context, IServiceProvider scope, IBifrostAuthContextFactory factory)
        {
            var projection = await CurrentSlot(context, scope, factory).Projection;
            return projection.Outcome == BifrostIdentityOutcome.Projected
                ? projection with { UserContext = UserContexts.Snapshot(projection.UserContext) }
                : projection;
        }

        /// <summary>
        /// The scope's shared projection, starting one when none matches this caller. A candidate is
        /// created unstarted and published by compare-exchange; only the caller whose candidate won
        /// starts it.
        /// </summary>
        private Slot CurrentSlot(HttpContext context, IServiceProvider scope, IBifrostAuthContextFactory factory)
        {
            var user = context.User;
            var shape = PrincipalShape.Of(user);
            while (true)
            {
                var current = Volatile.Read(ref _slot);
                if (current is not null && current.IsReusable
                    && ReferenceEquals(current.User, user) && current.Shape == shape
                    && ReferenceEquals(current.Factory, factory))
                    return current;

                var start = new Task<Task<BifrostIdentityProjection>>(
                    () => BifrostIdentityGate.ProjectFreshAsync(context, scope).AsTask());
                var candidate = new Slot(user, shape, factory, start.Unwrap());
                if (ReferenceEquals(Interlocked.CompareExchange(ref _slot, candidate, current), current))
                {
                    start.RunSynchronously();
                    return candidate;
                }
            }
        }
    }

    /// <summary>
    /// The root service provider, captured by a singleton (a singleton's injected
    /// <see cref="IServiceProvider"/> is the root). Scoped services resolved from the root live for
    /// the process, so a memo there spans every request, and a <see cref="PolicyGateAccessor"/>
    /// captured by a singleton served its first caller's gate to everyone.
    /// <see cref="IsRoot"/> lets the identity seams refuse to memoize in it.
    /// </summary>
    internal sealed class RootServiceProvider
    {
        private readonly IServiceProvider _root;

        public RootServiceProvider(IServiceProvider root) => _root = root;

        /// <summary>
        /// True when <paramref name="provider"/> is the root provider (the host's
        /// <c>app.Services</c>, or the provider injected into a root-resolved service). A scope
        /// resolves <see cref="IServiceProvider"/> to itself, the root to the root. False when
        /// <see cref="RootServiceProvider"/> is not registered, which is the case only where the
        /// memo and the accessor are not registered either.
        /// </summary>
        internal static bool IsRoot(IServiceProvider provider)
            => provider.GetService(typeof(RootServiceProvider)) is RootServiceProvider marker
               && ReferenceEquals(provider.GetService(typeof(IServiceProvider)), marker._root);
    }
}
