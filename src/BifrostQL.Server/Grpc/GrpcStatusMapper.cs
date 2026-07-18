using BifrostQL.Core.Resolvers;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server.Grpc
{
    /// <summary>
    /// The SINGLE error-mapping funnel every gRPC op class routes through — Get, List, Stream, and
    /// reflection. Mapping a Bifrost/pipeline exception onto a gRPC <see cref="Status"/> in exactly
    /// one place is what prevents the differential-oracle bug class (the OData-epic single-funnel
    /// lesson / the S3 cross-cutting error-mapping lesson): the SAME underlying condition maps to
    /// the SAME gRPC status on every RPC, so a read-denied caller cannot tell one op class from
    /// another by its status.
    ///
    /// <para><b>Sanitization (invariant 3).</b> Only the adapter's own curated
    /// <see cref="GrpcRequestException"/> surfaces its message verbatim. A
    /// <see cref="BifrostExecutionError"/> or any other exception maps to a generic INTERNAL status
    /// with a fixed message; the real detail is logged server-side only, never forwarded onto the
    /// wire (it can wrap raw driver/schema text). Cancellation/deadline maps to CANCELLED /
    /// DEADLINE_EXCEEDED. Because this funnel catches the full exception family, a malformed request
    /// cannot escape unhandled to the host (invariant 1).</para>
    /// </summary>
    internal static class GrpcStatusMapper
    {
        private const string GenericMessage = "The gRPC request could not be completed.";

        /// <summary>Message used for BOTH a genuinely-missing row and an authorization-denied row on a
        /// Get, so the two are byte-identical on the wire (criterion 3 anti-oracle).</summary>
        internal const string RowNotFoundMessage = "No row matches the supplied key.";

        /// <summary>
        /// Maps <paramref name="ex"/> to the gRPC status to fault the call with. A caller
        /// cancellation/deadline is distinguished from an internal failure; everything that is not
        /// a curated adapter fault is sanitized to INTERNAL with detail logged.
        ///
        /// <para>When <paramref name="denialIsNotFound"/> is set (a row-addressed Get), an
        /// authorization denial is mapped to NOT_FOUND with the same message a missing row uses, so a
        /// caller cannot distinguish "row exists but is denied to me" from "row does not exist" — no
        /// NOT_FOUND-vs-PERMISSION_DENIED existence oracle (criterion 3).</para>
        /// </summary>
        public static Status Map(Exception ex, ServerCallContext context, ILogger logger, bool denialIsNotFound = false)
        {
            switch (ex)
            {
                case RpcException rpc:
                    // An op class already chose a precise, non-leaking status (e.g. NotFound for a
                    // missing/out-of-scope key). Preserve it — it went through this same contract.
                    return rpc.Status;

                case GrpcRequestException req:
                    // Adapter-owned, curated for the caller — safe to surface verbatim.
                    return new Status(req.StatusCode, req.Message);

                case OperationCanceledException:
                    // A client cancel or an elapsed deadline — never an internal fault to log noisily.
                    return context.CancellationToken.IsCancellationRequested
                        && context.Deadline <= DateTime.UtcNow
                            ? new Status(StatusCode.DeadlineExceeded, "The deadline was exceeded.")
                            : new Status(StatusCode.Cancelled, "The call was cancelled.");

                case BifrostExecutionError denied when denied.ErrorCode == BifrostExecutionError.AccessDeniedCode:
                    // A fail-closed authorization denial (missing tenant context, row/column policy).
                    // On a Get this must be INDISTINGUISHABLE from a missing row; elsewhere it surfaces
                    // as PERMISSION_DENIED. Either way the message is GENERIC — the exception text names
                    // the table/tenant key and must not reach the wire (invariant 3).
                    return denialIsNotFound
                        ? new Status(StatusCode.NotFound, RowNotFoundMessage)
                        : new Status(StatusCode.PermissionDenied, "The request was denied by policy.");

                default:
                    // BifrostExecutionError and everything else: treat as untrusted on the wire.
                    // Log the full detail server-side; return a generic sanitized status.
                    logger.LogError(ex, "Unhandled error in gRPC {Method}.", context.Method);
                    return new Status(StatusCode.Internal, GenericMessage);
            }
        }

        /// <summary>
        /// Maps <paramref name="ex"/> and wraps it as the <see cref="RpcException"/> to throw,
        /// attaching a google.rpc.BadRequest status-details trailer when (and only when) the fault is
        /// an adapter-owned validation exception carrying request-field violations.
        /// </summary>
        private static RpcException ToRpcException(
            Exception ex, ServerCallContext context, ILogger logger, bool denialIsNotFound)
        {
            var status = Map(ex, context, logger, denialIsNotFound);
            if (ex is GrpcRequestException req)
            {
                var trailers = GrpcRichError.TrailersFor(req, status.StatusCode, status.Detail);
                if (trailers is not null)
                    return new RpcException(status, trailers);
            }
            return new RpcException(status);
        }

        /// <summary>
        /// Runs <paramref name="handler"/> and, on any exception, throws the single-funnel-mapped
        /// <see cref="RpcException"/>. Every op-class handler wraps its body in this so no op class
        /// can diverge from the shared mapping or leak an unhandled exception to Kestrel. A
        /// row-addressed Get passes <paramref name="denialIsNotFound"/> so an authorization denial is
        /// hidden as NOT_FOUND (criterion 3).
        /// </summary>
        public static async Task<T> GuardAsync<T>(
            ServerCallContext context, ILogger logger, Func<Task<T>> handler, bool denialIsNotFound = false)
        {
            try
            {
                return await handler();
            }
            catch (Exception ex)
            {
                throw ToRpcException(ex, context, logger, denialIsNotFound);
            }
        }

        /// <summary>Void-returning counterpart for streaming handlers.</summary>
        public static async Task GuardAsync(
            ServerCallContext context, ILogger logger, Func<Task> handler)
        {
            try
            {
                await handler();
            }
            catch (Exception ex)
            {
                throw ToRpcException(ex, context, logger, denialIsNotFound: false);
            }
        }
    }
}
