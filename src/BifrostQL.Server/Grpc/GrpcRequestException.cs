using Grpc.Core;

namespace BifrostQL.Server.Grpc
{
    /// <summary>A single request-field validation fault, surfaced as a google.rpc.BadRequest
    /// FieldViolation. <see cref="Field"/> is ALWAYS a request/proto field name (e.g. a primary-key
    /// field the caller supplied) — never an internal column/table/SQL identifier (invariant 3).</summary>
    public readonly record struct GrpcFieldViolation(string Field, string Description);

    /// <summary>
    /// A deliberately user-facing gRPC request fault owned by the adapter: a malformed request
    /// message, an unknown/unparseable key value, an argument that cannot be decoded off the wire,
    /// or a fail-closed authentication rejection. Unlike a <c>BifrostExecutionError</c> (which may
    /// wrap raw driver/schema text and is therefore untrusted on the wire), this type's
    /// <see cref="System.Exception.Message"/> is curated for the caller and may be surfaced verbatim
    /// as a gRPC status message (protocol-adapter-security invariant 3).
    ///
    /// <para>It carries the gRPC <see cref="StatusCode"/> the single-funnel mapper
    /// (<see cref="GrpcStatusMapper"/>) surfaces, and is caught by that funnel on every RPC op
    /// class so a malformed HTTP/2 request can never escape unhandled to Kestrel
    /// (protocol-adapter-security invariant 1). When it carries <see cref="FieldViolations"/>, the
    /// funnel additionally emits a google.rpc.BadRequest in the status-details trailer — those
    /// violations reference ONLY request field names.</para>
    /// </summary>
    public sealed class GrpcRequestException : Exception
    {
        public StatusCode StatusCode { get; }

        /// <summary>Request-field violations to surface as google.rpc.BadRequest detail (may be empty).</summary>
        public IReadOnlyList<GrpcFieldViolation> FieldViolations { get; }

        public GrpcRequestException(
            StatusCode statusCode, string message, IReadOnlyList<GrpcFieldViolation>? fieldViolations = null)
            : base(message)
        {
            StatusCode = statusCode;
            FieldViolations = fieldViolations ?? Array.Empty<GrpcFieldViolation>();
        }

        /// <summary>An INVALID_ARGUMENT fault — a request the caller can correct.</summary>
        public static GrpcRequestException InvalidArgument(string message)
            => new(StatusCode.InvalidArgument, message);

        /// <summary>
        /// An INVALID_ARGUMENT fault carrying a google.rpc.BadRequest field violation. Both
        /// <paramref name="field"/> and the surfaced message reference only the request field the
        /// caller supplied — never an internal column/table/SQL detail (invariant 3).
        /// </summary>
        public static GrpcRequestException InvalidField(string field, string description)
            => new(
                StatusCode.InvalidArgument,
                $"Invalid request field '{field}': {description}",
                new[] { new GrpcFieldViolation(field, description) });

        /// <summary>
        /// A fail-closed authentication rejection (UNAUTHENTICATED). The message is a fixed,
        /// non-revealing constant: missing, malformed, unmapped-issuer, and subject-less credentials
        /// all surface the SAME message so the wire cannot be used to enumerate which issuers/users
        /// exist (invariants 2 and 3). The real cause is logged server-side only.
        /// </summary>
        public static GrpcRequestException Unauthenticated()
            => new(StatusCode.Unauthenticated, "The request could not be authenticated.");
    }
}
