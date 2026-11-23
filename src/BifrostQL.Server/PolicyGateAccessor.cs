using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Schema;
using GraphQL;
using Microsoft.AspNetCore.Http;

namespace BifrostQL.Server
{
    /// <summary>
    /// Default <see cref="IPolicyGateAccessor"/>, registered scoped. The gate is built lazily on
    /// the first <see cref="GetAsync"/> and memoized for the request (invariant 13: per request,
    /// never per session). Nothing runs at construction, so a request whose endpoint never awaits
    /// the accessor (a health check, a static file) triggers no identity projection and no schema
    /// load. That matters because <see cref="PathCache{T}"/> drops a faulted load and retries it:
    /// a down database would otherwise make every request on every route retry the load.
    /// A faulted build is not memoized either, so a later call in the same request retries it.
    /// Concurrent callers share one build, published lock-free by compare-exchange. The build runs
    /// on the request's own <c>RequestAborted</c>; each caller's token abandons only that caller's
    /// wait, so one caller's cancellation never faults the build the others await.
    /// The caller is projected in the accessor's OWN DI scope, not the
    /// HttpContext's: a hook resolving the accessor inside a binary per-message scope or a hub
    /// invocation scope reaches a connection-lifetime HttpContext through
    /// <see cref="IHttpContextAccessor"/>, whose RequestServices hold a connection-lifetime grant
    /// resolver. The projection is not shared with the endpoint's own
    /// <c>GetBifrostUserContextAsync</c> call: an endpoint that awaits both runs the grant
    /// resolver twice.
    /// </summary>
    internal sealed class PolicyGateAccessor : IPolicyGateAccessor
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly PathCache<Inputs> _models;
        private readonly IServiceProvider _scope;
        private readonly bool _resolvedFromRoot;
        private Task<IPolicyGate>? _gate;

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
        {
            if (_resolvedFromRoot)
                throw new InvalidOperationException(
                    "IPolicyGateAccessor was resolved from the root service provider (a singleton captured it), " +
                    "so its gate would be shared by every caller. Resolve IPolicyGateAccessor from the request's " +
                    "scope (HttpContext.RequestServices or an injected scoped service) on each request.");
            return new(CurrentBuild().WaitAsync(cancellationToken));
        }

        /// <summary>
        /// The request's shared build, starting one when none exists or the last one faulted or was
        /// canceled. A candidate is created unstarted and published by compare-exchange; only the
        /// caller whose candidate won starts it.
        /// </summary>
        private Task<IPolicyGate> CurrentBuild()
        {
            while (true)
            {
                var current = Volatile.Read(ref _gate);
                if (current is { IsFaulted: false, IsCanceled: false })
                    return current;

                var http = _httpContextAccessor.HttpContext
                    ?? throw new InvalidOperationException("IPolicyGateAccessor requires an active HttpContext.");
                var start = new Task<Task<IPolicyGate>>(() => BuildAsync(http));
                var candidate = start.Unwrap();
                if (ReferenceEquals(Interlocked.CompareExchange(ref _gate, candidate, current), current))
                {
                    start.RunSynchronously();
                    return candidate;
                }
            }
        }

        private async Task<IPolicyGate> BuildAsync(HttpContext http)
        {
            // One identity decision per assembly (M13): the gate runs the caller through
            // BifrostIdentityGate like every HTTP mount does, never through a second
            // CreateUserContext call that would drift from it. A caller the gate cannot
            // project — anonymous, or a principal whose claims map to nothing — gets the
            // gate that refuses everything, so the endpoint answers ACCESS_DENIED the way
            // the GraphQL mount refuses that same caller. Identity comes first, so a refused
            // caller never triggers a model load.
            var (outcome, userContext) = await BifrostIdentityGate.ProjectAsync(http, _scope);
            if (outcome != BifrostIdentityOutcome.Projected)
                return PolicyGate.Refused;
            http.RequestAborted.ThrowIfCancellationRequested();
            var inputs = await ResolveModelInputsAsync(_models, http);
            var model = (inputs["model"] as IDbModel)
                ?? throw new InvalidOperationException("IPolicyGate model is unavailable.");
            return new PolicyGate(model, PolicyIdentity.FromUserContext(userContext));
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
