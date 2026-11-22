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
    /// </summary>
    internal sealed class PolicyGateAccessor : IPolicyGateAccessor
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly PathCache<Inputs> _models;
        private Task<IPolicyGate>? _gate;

        public PolicyGateAccessor(IHttpContextAccessor httpContextAccessor, PathCache<Inputs> models)
        {
            _httpContextAccessor = httpContextAccessor;
            _models = models;
        }

        public ValueTask<IPolicyGate> GetAsync(CancellationToken cancellationToken = default)
        {
            var current = _gate;
            if (current is null || current.IsFaulted || current.IsCanceled)
                _gate = current = BuildAsync(cancellationToken);
            return new ValueTask<IPolicyGate>(current);
        }

        private async Task<IPolicyGate> BuildAsync(CancellationToken cancellationToken)
        {
            var http = _httpContextAccessor.HttpContext
                ?? throw new InvalidOperationException("IPolicyGateAccessor requires an active HttpContext.");
            // One identity decision per assembly (M13): the gate runs the caller through
            // BifrostIdentityGate like every HTTP mount does, never through a second
            // CreateUserContext call that would drift from it. A caller the gate cannot
            // project — anonymous, or a principal whose claims map to nothing — gets the
            // gate that refuses everything, so the endpoint answers ACCESS_DENIED the way
            // the GraphQL mount refuses that same caller. Identity comes first, so a refused
            // caller never triggers a model load.
            var (outcome, userContext) = await BifrostIdentityGate.ProjectAsync(http);
            if (outcome != BifrostIdentityOutcome.Projected)
                return PolicyGate.Refused;
            cancellationToken.ThrowIfCancellationRequested();
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
}
