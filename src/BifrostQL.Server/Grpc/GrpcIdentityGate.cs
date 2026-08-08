using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server.Grpc
{
    /// <summary>
    /// The ONE identity gate for the gRPC front door. Every op class on the port — the dynamic
    /// Get/List/Stream/mutation RPCs and server reflection alike — resolves its caller here, so
    /// there is no second projection that can drift into being weaker (protocol-adapter-security
    /// invariants 4, 9, 10).
    ///
    /// <para>It extracts the caller's bearer credential and projects it through the SHARED
    /// <see cref="IBifrostAuthContextFactory"/> — the same seam OData/MCP/S3 use. The adapter does
    /// NOT decide claims/identity mapping itself; it only (a) enforces a size cap on the raw
    /// <c>authorization</c> credential and (b) FAILS CLOSED before any intent or descriptor set is
    /// built when the projection is empty (missing/anonymous), throws (unmapped issuer,
    /// subject-less), or the credential is abusive. There is NO branch that proceeds with a
    /// permissive or anonymous identity. Every failure surfaces the SAME sanitized UNAUTHENTICATED
    /// — the real cause is logged server-side only (invariants 2, 3).</para>
    /// </summary>
    internal static class GrpcIdentityGate
    {
        /// <summary>
        /// The largest <c>authorization</c> credential the adapter will look at. A real bearer/JWT is
        /// well under this; the cap exists only to reject an abusive/oversized metadata value cleanly
        /// (a fixed-work fail-closed) before any projection, never an unbounded parse/alloc. It sits
        /// below Kestrel's own header-size limit so the adapter — not the host — returns the clean
        /// UNAUTHENTICATED.
        /// </summary>
        internal const int MaxAuthorizationChars = 8 * 1024;

        public static IDictionary<string, object?> ResolveIdentity(
            ServerCallContext context, IBifrostAuthContextFactory authFactory, ILogger logger)
        {
            var http = context.GetHttpContext();

            var authorizationLength = 0;
            foreach (var value in http.Request.Headers.Authorization)
                authorizationLength += value?.Length ?? 0;
            if (authorizationLength > MaxAuthorizationChars)
            {
                logger.LogWarning(
                    "gRPC authorization credential exceeded {Cap} chars ({Actual}); failing closed.",
                    MaxAuthorizationChars, authorizationLength);
                throw GrpcRequestException.Unauthenticated();
            }

            IDictionary<string, object?> projected;
            try
            {
                projected = authFactory.CreateUserContext(http);
            }
            catch (Exception ex)
            {
                // Unmapped OIDC issuer, subject-less principal, or any projection fault — fail closed.
                // The detail (issuer name, claim shape) is logged server-side only, never on the wire.
                logger.LogWarning(ex, "gRPC identity projection failed; failing closed.");
                throw GrpcRequestException.Unauthenticated();
            }

            // An empty context is the shared factory's fail-closed signal for a missing/anonymous
            // credential. Reject BEFORE building any intent — or any descriptor set — so a
            // credential-less call can never reach the executor, or reflection's schema inventory,
            // with a permissive identity. PolicyEvaluator ALLOWS a policy-less table for an empty
            // identity, so without this check reflection served the full table/column/PK/write
            // allow-list to any anonymous caller on a port whose data path was closed.
            if (projected.Count == 0)
                throw GrpcRequestException.Unauthenticated();

            return projected;
        }
    }
}
