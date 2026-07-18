using Grpc.Core;

namespace BifrostQL.Server.Grpc
{
    /// <summary>
    /// A deliberately user-facing gRPC request fault owned by the adapter: a malformed request
    /// message, an unknown/unparseable key value, or an argument that cannot be decoded off the
    /// wire. Unlike a <c>BifrostExecutionError</c> (which may wrap raw driver/schema text and is
    /// therefore untrusted on the wire), this type's <see cref="System.Exception.Message"/> is
    /// curated for the caller and may be surfaced verbatim as a gRPC status message
    /// (protocol-adapter-security invariant 3).
    ///
    /// <para>It carries the gRPC <see cref="StatusCode"/> the single-funnel mapper
    /// (<see cref="GrpcStatusMapper"/>) surfaces, and is caught by that funnel on every RPC op
    /// class so a malformed HTTP/2 request can never escape unhandled to Kestrel
    /// (protocol-adapter-security invariant 1).</para>
    /// </summary>
    public sealed class GrpcRequestException : Exception
    {
        public StatusCode StatusCode { get; }

        public GrpcRequestException(StatusCode statusCode, string message) : base(message)
            => StatusCode = statusCode;

        /// <summary>An INVALID_ARGUMENT fault — a request the caller can correct.</summary>
        public static GrpcRequestException InvalidArgument(string message)
            => new(StatusCode.InvalidArgument, message);
    }
}
