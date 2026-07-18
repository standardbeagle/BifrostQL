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

        /// <summary>
        /// Maps <paramref name="ex"/> to the gRPC status to fault the call with. A caller
        /// cancellation/deadline is distinguished from an internal failure; everything that is not
        /// a curated adapter fault is sanitized to INTERNAL with detail logged.
        /// </summary>
        public static Status Map(Exception ex, ServerCallContext context, ILogger logger)
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
                    // Surface as PERMISSION_DENIED but with a GENERIC message — the exception text names
                    // the table/tenant key and must not reach the wire (invariant 3).
                    return new Status(StatusCode.PermissionDenied, "The request was denied by policy.");

                default:
                    // BifrostExecutionError and everything else: treat as untrusted on the wire.
                    // Log the full detail server-side; return a generic sanitized status.
                    logger.LogError(ex, "Unhandled error in gRPC {Method}.", context.Method);
                    return new Status(StatusCode.Internal, GenericMessage);
            }
        }

        /// <summary>
        /// Runs <paramref name="handler"/> and, on any exception, throws the single-funnel-mapped
        /// <see cref="RpcException"/>. Every op-class handler wraps its body in this so no op class
        /// can diverge from the shared mapping or leak an unhandled exception to Kestrel.
        /// </summary>
        public static async Task<T> GuardAsync<T>(
            ServerCallContext context, ILogger logger, Func<Task<T>> handler)
        {
            try
            {
                return await handler();
            }
            catch (Exception ex)
            {
                throw new RpcException(Map(ex, context, logger));
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
                throw new RpcException(Map(ex, context, logger));
            }
        }
    }
}
