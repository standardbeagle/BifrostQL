namespace BifrostQL.Server.OData
{
    /// <summary>
    /// A deliberately user-facing OData protocol violation: an auth failure or an unimplemented
    /// operation. <see cref="ODataMiddleware"/> catches exactly this type and maps it to the
    /// matching OData JSON error envelope (<see cref="Code"/>/<see cref="HttpStatus"/>); every
    /// other exception is treated as internal and mapped to a generic sanitized "InternalError"
    /// response instead of being forwarded verbatim (see
    /// .claude/rules/protocol-adapter-security.md invariants 1 and 3). <see cref="Message"/> on
    /// instances of this type is therefore curated to never carry credentials, request paths, or
    /// tenant data. Because the middleware's catch clause filters on this exact type, any new
    /// protocol-violation exception must derive from it (invariant 1).
    /// </summary>
    public sealed class ODataProtocolException : Exception
    {
        public string Code { get; }
        public int HttpStatus { get; }

        public ODataProtocolException(string code, int httpStatus, string message) : base(message)
        {
            Code = code;
            HttpStatus = httpStatus;
        }

        /// <summary>No / absent / malformed credentials — the request is not authenticated.</summary>
        public static ODataProtocolException Unauthorized(string message = "Authentication is required.")
            => new("Unauthorized", 401, message);

        /// <summary>
        /// Authenticated, but the identity is not acceptable — a subject-less principal, an
        /// unmapped OIDC issuer, or a projection that yields no usable identity.
        /// </summary>
        public static ODataProtocolException Forbidden(string message = "Access is denied.")
            => new("Forbidden", 403, message);

        /// <summary>
        /// The request authenticated but its operation is not implemented in this slice
        /// (service document, metadata, and reads arrive in later slices).
        /// </summary>
        public static ODataProtocolException NotImplemented(string message = "The requested OData operation is not implemented.")
            => new("NotImplemented", 501, message);

        /// <summary>
        /// A malformed request the caller can correct — an unknown/ambiguous property name, a
        /// non-integer or out-of-range <c>$top</c>/<c>$skip</c>, a duplicated system query option,
        /// or an unsupported one. The message is curated to describe only the shape of the fault,
        /// never request or tenant data.
        /// </summary>
        public static ODataProtocolException BadRequest(string message)
            => new("BadRequest", 400, message);

        /// <summary>
        /// The addressed entity set does not exist — or is not visible to this identity, which is
        /// deliberately indistinguishable from "does not exist" so the endpoint never becomes an
        /// existence oracle for tables the caller may not read (fail-closed;
        /// .claude/rules/protocol-adapter-security.md invariant 4).
        /// </summary>
        public static ODataProtocolException NotFound(string message = "The requested entity set was not found.")
            => new("NotFound", 404, message);

        /// <summary>A sanitized envelope for an unexpected internal error; detail is logged server-side only.</summary>
        public static ODataProtocolException InternalError()
            => new("InternalError", 500, "An internal error occurred.");
    }
}
