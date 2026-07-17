namespace BifrostQL.Server.S3
{
    /// <summary>
    /// A deliberately user-facing S3 protocol violation: an auth failure, a malformed
    /// request, or a limit breach. <see cref="S3Middleware"/> catches exactly this type and
    /// maps it to the matching S3 XML error envelope (<see cref="Code"/>/<see cref="HttpStatus"/>);
    /// every other exception is treated as internal and mapped to a generic sanitized
    /// "InternalError" response instead of being forwarded verbatim (see
    /// .claude/rules/protocol-adapter-security.md invariants 1 and 3). <see cref="Message"/>
    /// on instances of this type is therefore curated to never carry credentials, canonical
    /// strings, request paths, or tenant data.
    /// </summary>
    public sealed class S3ProtocolException : Exception
    {
        public string Code { get; }
        public int HttpStatus { get; }

        public S3ProtocolException(string code, int httpStatus, string message) : base(message)
        {
            Code = code;
            HttpStatus = httpStatus;
        }

        public static S3ProtocolException AccessDenied(string message = "Access Denied.")
            => new("AccessDenied", 403, message);

        public static S3ProtocolException SignatureDoesNotMatch()
            => new("SignatureDoesNotMatch", 403,
                "The request signature we calculated does not match the signature you provided.");

        public static S3ProtocolException RequestTimeTooSkewed()
            => new("RequestTimeTooSkewed", 403, "The difference between the request time and the current time is too large.");

        public static S3ProtocolException AuthorizationHeaderMalformed(string message = "The authorization header is malformed.")
            => new("AuthorizationHeaderMalformed", 400, message);

        public static S3ProtocolException AuthorizationQueryParametersError(string message = "Error parsing the presigned query parameters.")
            => new("AuthorizationQueryParametersError", 400, message);

        public static S3ProtocolException ContentSha256Mismatch()
            => new("XAmzContentSHA256Mismatch", 400,
                "The provided 'x-amz-content-sha256' header does not match the computed payload hash.");

        public static S3ProtocolException InvalidArgument(string message)
            => new("InvalidArgument", 400, message);

        public static S3ProtocolException EntityTooLarge()
            => new("EntityTooLarge", 400, "Your proposed upload exceeds the maximum allowed size.");

        public static S3ProtocolException RequestExpired()
            => new("AccessDenied", 403, "Request has expired.");

        public static S3ProtocolException NotImplemented(string message = "A header or operation you provided is not implemented.")
            => new("NotImplemented", 501, message);

        public static S3ProtocolException InternalError()
            => new("InternalError", 500, "We encountered an internal error. Please try again.");
    }
}
