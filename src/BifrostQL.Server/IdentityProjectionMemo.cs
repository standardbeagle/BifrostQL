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
    /// scope; a memo on the HttpContext served a revoked grant until reconnect.
    ///
    /// <para>Concurrent callers in one scope share one projection task, published lock-free by
    /// compare-exchange (the <see cref="PolicyGateAccessor"/> pattern), so the grant resolver runs
    /// once. Only a <see cref="BifrostIdentityOutcome.Projected"/> result is reused: a refusal, or a
    /// task that faulted, is replaced by the next caller. A slot is reused only for the principal
    /// and factory instances it was built from. Every caller gets its own copy of the context.</para>
    /// </summary>
    internal sealed class IdentityProjectionMemo
    {
        private Slot? _slot;

        private sealed record Slot(
            ClaimsPrincipal User, IBifrostAuthContextFactory Factory, Task<BifrostIdentityProjection> Projection)
        {
            /// <summary>A refusal or a fault is never memoized; an in-flight projection is shared.</summary>
            public bool IsReusable => !Projection.IsCompleted
                || (Projection.IsCompletedSuccessfully && Projection.Result.Outcome == BifrostIdentityOutcome.Projected);
        }

        internal async ValueTask<BifrostIdentityProjection> ProjectAsync(
            HttpContext context, IServiceProvider scope, IBifrostAuthContextFactory factory)
        {
            var projection = await CurrentSlot(context, scope, factory).Projection;
            return projection.Outcome == BifrostIdentityOutcome.Projected
                ? projection with { UserContext = UserContexts.Copy(projection.UserContext) }
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
            while (true)
            {
                var current = Volatile.Read(ref _slot);
                if (current is not null && current.IsReusable
                    && ReferenceEquals(current.User, user) && ReferenceEquals(current.Factory, factory))
                    return current;

                var start = new Task<Task<BifrostIdentityProjection>>(
                    () => BifrostIdentityGate.ProjectFreshAsync(context, scope).AsTask());
                var candidate = new Slot(user, factory, start.Unwrap());
                if (ReferenceEquals(Interlocked.CompareExchange(ref _slot, candidate, current), current))
                {
                    start.RunSynchronously();
                    return candidate;
                }
            }
        }
    }
}
