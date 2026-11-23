using BifrostQL.Core.Schema;
using GraphQL;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Server
{
    /// <summary>
    /// The ONE derivation of whether an HTTP-mounted front door (binary WebSocket,
    /// protocol frontend, GraphQL mount, <c>/_app-metadata</c>, <c>/_blob</c>,
    /// <c>/_saved-objects</c>) requires an authenticated identity. H8 and H14
    /// each carried a private copy of this decision (one on the binary mount, one on the
    /// frontend mount); two copies of one security decision drift, one copy cannot
    /// (<c>MountAuthRequirementSourceScanTests</c> in BifrostQL.Server.Test pins the single-derivation shape).
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
        /// mount path; the binary mount threads its <c>graphqlPath</c>; the app-metadata,
        /// blob and saved-objects mounts thread their configured endpoint, null when none is
        /// named). The single-endpoint
        /// fallback mirrors <c>BifrostEngine.ExecuteAsync</c>'s own schema fallback: if the
        /// auth resolution and the schema resolution ever targeted different endpoints, the
        /// mount would authorize against one endpoint and serve another.
        /// </summary>
        internal static bool Resolve(IServiceProvider services, string? endpointPath)
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
        /// Refuses, at mount time, a mount whose host-configured endpoint cannot name one
        /// registered endpoint: null with several registered, or a path none is registered under
        /// (matched case-insensitively, as <c>IntentEndpointResolver</c> and <see cref="Resolve"/>
        /// match it). <see cref="Resolve"/>'s single-endpoint fallback otherwise resolves an auth
        /// requirement for a misspelled name, so the mount starts and every request then fails
        /// where the mount resolves its model or schema. A host with no Bifrost data path has
        /// nothing to name; naming one there is a misconfiguration too.
        /// <para>Every front door that takes a host-configured endpoint calls this once at startup:
        /// the HTTP mounts in their <c>UseBifrost*</c> method, the raw-wire adapters (pgwire, RESP,
        /// LDAP) and MCP over HTTP as the host resolves their hosted service, before any listener
        /// binds, and the stdio MCP adapter as the host constructs it. gRPC needs no call: <c>MapBifrostGrpcAsync</c> loads the endpoint's model.</para>
        /// </summary>
        /// <param name="services">The application services holding the registered endpoints.</param>
        /// <param name="endpointPath">The configured endpoint path; null selects the single endpoint.</param>
        /// <param name="mount">The mount method named in the error (<c>UseBifrostBlobs</c>).</param>
        /// <param name="option">The option that names the endpoint (<c>BifrostBlobOptions.Endpoint</c>).</param>
        internal static void RequireOneServedEndpoint(
            IServiceProvider services, string? endpointPath, string mount, string option)
        {
            var endpoints = services.GetService<PathCache<Inputs>>();
            if (endpointPath is null)
            {
                if (endpoints is { Count: > 1 })
                    throw new InvalidOperationException(
                        $"{mount}: {endpoints.Count} GraphQL endpoints are registered; set {option} " +
                        "to the one this mount serves.");
                return;
            }
            if (endpoints is null || !endpoints.HasPath(endpointPath.ToLowerInvariant()))
                throw new InvalidOperationException(
                    $"{mount}: {option} '{endpointPath}' names no registered GraphQL endpoint.");
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
