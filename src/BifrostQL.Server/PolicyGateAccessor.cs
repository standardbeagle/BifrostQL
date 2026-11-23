using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Schema;
using GraphQL;
using Microsoft.AspNetCore.Http;

namespace BifrostQL.Server
{
    /// <summary>
    /// Default <see cref="IPolicyGateAccessor"/>, registered scoped. The gate is built lazily on
    /// the first <c>GetAsync</c> and memoized for the accessor's scope, keyed on the identity it was
    /// built for (invariant 13): the self-projected build has one key, and a caller-supplied user
    /// context is keyed on the policy identity it holds at the call (<see cref="PolicyIdentity"/>,
    /// compared by value), so a context mutated between two calls is never served the gate of its
    /// earlier contents, and the build never reads the dictionary after the call. The self-projected
    /// identity is never memoized past the build. Nothing runs at construction, so a request whose endpoint never awaits
    /// the accessor (a health check, a static file) triggers no identity projection and no schema
    /// load. That matters because <see cref="PathCache{T}"/> drops a faulted load and retries it:
    /// a down database would otherwise make every request on every route retry the load.
    /// A faulted build is not memoized either, so a later call in the same request retries it.
    /// Concurrent callers share one build, published lock-free by compare-exchange. The build runs
    /// on the request's own <c>RequestAborted</c>; each caller's token abandons only that caller's
    /// wait, so one caller's cancellation never faults the build the others await.
    /// The caller is projected in the accessor's OWN DI scope, not the
    /// HttpContext's: a hook resolving the accessor inside a hub invocation scope reaches a
    /// connection-lifetime HttpContext through <see cref="IHttpContextAccessor"/>, whose
    /// RequestServices hold a connection-lifetime grant resolver. (A binary message's context is
    /// its own, with the message's scope as RequestServices.) An endpoint that already holds the caller's context (from
    /// <c>GetBifrostUserContextAsync</c>) passes it to <c>GetAsync(userContext)</c>, so the gate
    /// and the endpoint's write answer for one projection and the resolver runs once.
    /// The captive-accessor check rests on one assumption about the container: the singleton
    /// <see cref="RootScopeSentinel"/> receives the root provider, so the sentinel it holds is
    /// the one a root-resolved accessor is injected.
    /// </summary>
    internal sealed class PolicyGateAccessor : IPolicyGateAccessor
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly PathCache<Inputs> _models;
        private readonly IServiceProvider _scope;
        private readonly bool _resolvedFromRoot;
        private GateBuild? _build;

        public PolicyGateAccessor(
            IHttpContextAccessor httpContextAccessor, PathCache<Inputs> models, IServiceProvider scope,
            ScopeSentinel sentinel, RootScopeSentinel root)
        {
            _httpContextAccessor = httpContextAccessor;
            _models = models;
            _scope = scope;
            _resolvedFromRoot = root.IsRoot(sentinel);
        }

        /// <summary>
        /// Refuses (throws) when this accessor was resolved from the root provider, as one a
        /// singleton captured is: its memoized gate would be the first caller's gate, served to
        /// every later caller. Resolve the accessor per request instead.
        /// </summary>
        public ValueTask<IPolicyGate> GetAsync(CancellationToken cancellationToken = default)
            => GetAsync(SelfProjected, cancellationToken);

        /// <inheritdoc cref="GetAsync(CancellationToken)"/>
        public ValueTask<IPolicyGate> GetAsync(
            IDictionary<string, object?> userContext, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(userContext);
            if (_resolvedFromRoot)
                throw new InvalidOperationException(
                    "IPolicyGateAccessor was resolved from the root service provider (a singleton captured it), " +
                    "so its gate would be shared by every caller. Resolve IPolicyGateAccessor from the request's " +
                    "scope (HttpContext.RequestServices or an injected scoped service) on each request.");
            // Snapshot now: the gate answers for what the context holds at this call, whatever the
            // caller does to the dictionary afterwards.
            var key = ReferenceEquals(userContext, SelfProjected) ? SelfProjectedKey : GateKey.Of(userContext);
            return new(CurrentBuild(key).WaitAsync(cancellationToken));
        }

        /// <summary>The context the parameterless overload passes; never a caller's context.</summary>
        private static readonly IDictionary<string, object?> SelfProjected = new Dictionary<string, object?>();

        /// <summary>The key of the build that projects the caller itself.</summary>
        private static readonly GateKey SelfProjectedKey = new(null);

        /// <summary>
        /// What a build answers for: the self-projected caller (<see cref="Identity"/> null), or the
        /// policy identity a supplied context held at the call. A supplied context that was empty
        /// (the projection's refusal) keeps an identity of null too, but is told apart by
        /// <see cref="Refused"/>. Compared by value, so a context mutated between two calls gets a
        /// new build and an unchanged one, or an equal copy, shares the build.
        /// </summary>
        private sealed class GateKey
        {
            public GateKey(AppIdentity? identity, bool refused = false)
            {
                Identity = identity;
                Refused = refused;
            }

            public AppIdentity? Identity { get; }
            public bool Refused { get; }

            public static GateKey Of(IDictionary<string, object?> userContext)
                => userContext.Count == 0
                    ? new GateKey(null, refused: true)
                    : new GateKey(PolicyIdentity.FromUserContext(userContext));

            public bool SameAs(GateKey other)
            {
                if (Refused != other.Refused)
                    return false;
                if (Identity is null || other.Identity is null)
                    return Identity is null && other.Identity is null;
                return string.Equals(Identity.Id, other.Identity.Id, StringComparison.Ordinal)
                    && Identity.IsEngine == other.Identity.IsEngine
                    && Identity.Roles.SequenceEqual(other.Identity.Roles, StringComparer.Ordinal)
                    && Identity.Permissions.SequenceEqual(other.Identity.Permissions, StringComparer.Ordinal);
            }
        }

        /// <summary>A published build and the key it answers for.</summary>
        private sealed record GateBuild(GateKey Key, Task<IPolicyGate> Gate);

        /// <summary>
        /// The shared build for <paramref name="key"/>, starting one when none exists, the last one
        /// faulted or was canceled, or the last one was built for a different key. A candidate is
        /// created unstarted and published by compare-exchange; only the caller whose candidate won
        /// starts it.
        /// </summary>
        private Task<IPolicyGate> CurrentBuild(GateKey key)
        {
            while (true)
            {
                var current = Volatile.Read(ref _build);
                if (current is not null && current.Key.SameAs(key)
                    && current.Gate is { IsFaulted: false, IsCanceled: false })
                    return current.Gate;

                var http = _httpContextAccessor.HttpContext
                    ?? throw new InvalidOperationException("IPolicyGateAccessor requires an active HttpContext.");
                var start = new Task<Task<IPolicyGate>>(() => BuildAsync(http, key));
                var candidate = new GateBuild(key, start.Unwrap());
                if (ReferenceEquals(Interlocked.CompareExchange(ref _build, candidate, current), current))
                {
                    start.RunSynchronously();
                    return candidate.Gate;
                }
            }
        }

        private async Task<IPolicyGate> BuildAsync(HttpContext http, GateKey key)
        {
            // One identity decision per assembly (M13): the gate runs the caller through
            // BifrostIdentityGate like every HTTP mount does, never through a second
            // CreateUserContext call that would drift from it. A caller the gate cannot
            // project — anonymous, or a principal whose claims map to nothing — gets the
            // gate that refuses everything, so the endpoint answers ACCESS_DENIED the way
            // the GraphQL mount refuses that same caller. A supplied context came from that same
            // projection, and an empty one is the projection's refusal, so it is refused here too.
            // Identity comes first, so a refused caller never triggers a model load.
            if (key.Refused)
                return PolicyGate.Refused;
            var identity = key.Identity;
            if (identity is null)
            {
                var (outcome, projected) = await BifrostIdentityGate.ProjectAsync(http, _scope);
                if (outcome != BifrostIdentityOutcome.Projected)
                    return PolicyGate.Refused;
                identity = PolicyIdentity.FromUserContext(projected);
            }
            http.RequestAborted.ThrowIfCancellationRequested();
            var inputs = await ResolveModelInputsAsync(_models, http);
            var model = (inputs["model"] as IDbModel)
                ?? throw new InvalidOperationException("IPolicyGate model is unavailable.");
            return new PolicyGate(model, identity);
        }

        /// <summary>
        /// The mount's own schema-resolution rule (BifrostHttpMiddleware.ResolveExtensionsAsync),
        /// applied to the request an app endpoint is answering: a registered GraphQL path in
        /// PathBase or Path picks that database; otherwise the single registered endpoint is the
        /// answer, and with more than one the gate refuses rather than guess — the mount throws
        /// UnknownBifrostEndpointException there, so an app endpoint must not be answered from
        /// whichever database happened to register first.
        /// </summary>
        private static async Task<Inputs> ResolveModelInputsAsync(PathCache<Inputs> cache, HttpContext http)
        {
            var pathBase = http.Request.PathBase.Value?.ToLowerInvariant();
            if (!string.IsNullOrEmpty(pathBase) && cache.HasPath(pathBase))
                return await cache.GetValueAsync(pathBase);

            var path = http.Request.Path.Value?.ToLowerInvariant();
            if (!string.IsNullOrEmpty(path) && cache.HasPath(path))
                return await cache.GetValueAsync(path);

            if (cache.Count > 1)
                throw new InvalidOperationException(
                    "IPolicyGate could not resolve a database model: more than one GraphQL endpoint is " +
                    $"registered and the request path '{http.Request.PathBase}{http.Request.Path}' is not one " +
                    "of them. Mount the endpoint under a registered GraphQL path so the gate resolves that " +
                    "endpoint's model, as the GraphQL mount does.");

            return await cache.GetFirstValueAsync()
                ?? throw new InvalidOperationException("IPolicyGate model is unavailable.");
        }
    }

    /// <summary>
    /// A marker registered scoped: each DI scope holds its own instance, and the root provider,
    /// asked for it, holds one for the process. A <see cref="PolicyGateAccessor"/> resolved from
    /// the root (one a singleton captured) is injected the root's instance. Unlike comparing
    /// providers by reference, this holds in any container that caches a root-resolved scoped
    /// service in the root, including one (Autofac) that hands out a new
    /// <see cref="IServiceProvider"/> wrapper per resolve.
    /// </summary>
    internal sealed class ScopeSentinel;

    /// <summary>
    /// The root provider's <see cref="ScopeSentinel"/>, held by a singleton (a singleton's
    /// dependencies come from the root). Null when the root refuses a scoped service (scope
    /// validation) or none is registered: then no accessor can have been resolved from the root.
    /// </summary>
    internal sealed class RootScopeSentinel
    {
        public RootScopeSentinel(IServiceProvider root)
        {
            try
            {
                Sentinel = root.GetService(typeof(ScopeSentinel)) as ScopeSentinel;
            }
            catch (InvalidOperationException)
            {
                // Scope validation refuses scoped services from the root, the accessor included.
                Sentinel = null;
            }
        }

        public ScopeSentinel? Sentinel { get; }

        /// <summary>True when <paramref name="sentinel"/> is the root's own instance.</summary>
        public bool IsRoot(ScopeSentinel sentinel) => ReferenceEquals(sentinel, Sentinel);
    }
}
