using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Server
{
    /// <summary>
    /// The ONE derivation of whether an HTTP-mounted front door (binary WebSocket,
    /// protocol frontend, GraphQL mount) requires an authenticated identity. H8 and H14
    /// each carried a private copy of this decision (one on the binary mount, one on the
    /// frontend mount); two copies of one security decision drift, one copy cannot
    /// (<see cref="MountAuthRequirementSourceScanTests"/> pins the single-derivation shape).
    ///
    /// <para>The requirement is taken from the GraphQL endpoint whose schema the mount
    /// serves — the mount carries that endpoint's surface, so it must carry its auth
    /// requirement. The GraphQL endpoints enforce theirs INSIDE their own <c>Map</c>
    /// branch, which is why a mount at its own path is not covered by it and has to
    /// resolve the requirement here.</para>
    ///
    /// <para>Fail closed: a deployment configured through neither options object — or a
    /// mount whose served endpoint cannot be identified, or is ambiguous — requires
    /// authentication. Serving anonymously is only ever an EXPLICIT choice
    /// (<c>DisableAuth</c> on the endpoint, or <c>requireAuthentication: false</c> at the
    /// mount).</para>
    /// </summary>
    internal static class MountAuthRequirement
    {
        /// <summary>
        /// Resolves the auth requirement for a mount serving the endpoint registered at
        /// <paramref name="endpointPath"/> (for the frontend/GraphQL mounts this IS the
        /// mount path; the binary mount threads its <c>graphqlPath</c>). The single-endpoint
        /// fallback mirrors <c>BifrostEngine.ExecuteAsync</c>'s own schema fallback: if the
        /// auth resolution and the schema resolution ever targeted different endpoints, the
        /// mount would authorize against one endpoint and serve another.
        /// </summary>
        internal static bool Resolve(IServiceProvider services, string endpointPath)
        {
            var multiDb = services.GetService<BifrostMultiDbOptions>();
            if (multiDb != null)
            {
                var served = multiDb.Endpoints.FirstOrDefault(
                    e => string.Equals(e.Path, endpointPath, StringComparison.OrdinalIgnoreCase));
                // A mount whose path names no registered endpoint resolves its schema by the
                // single-endpoint fallback, so follow the same rule here; with several
                // endpoints the target is ambiguous and the safe reading is "requires auth".
                served ??= multiDb.Endpoints.Count == 1 ? multiDb.Endpoints[0] : null;
                return ForEndpoint(served);
            }

            var singleDb = services.GetService<BifrostSetupOptions>();
            if (singleDb != null)
                return singleDb.IsUsingAuth;

            return true;
        }

        /// <summary>
        /// The requirement once the served endpoint is known — used directly by the
        /// GraphQL mounts in <c>UseBifrostEndpoints</c>, which already hold their
        /// endpoint. A null endpoint is the unidentified/ambiguous case: fail closed.
        /// </summary>
        internal static bool ForEndpoint(BifrostEndpointConfig? endpoint)
            => endpoint is null || !endpoint.DisableAuth;
    }
}
