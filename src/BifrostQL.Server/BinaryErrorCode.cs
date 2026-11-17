namespace BifrostQL.Server
{
    /// <summary>
    /// Machine-readable codes the binary transport puts in <see cref="BifrostMessage.ErrorCode"/>
    /// on its own Error frames. A client branches on these, never on the frame's text, which is
    /// for people and may change. The values are part of the wire contract: never rename one.
    /// GraphQL execution errors carry no frame code; they travel in <see cref="BifrostMessage.Errors"/>
    /// as on the HTTP endpoint.
    /// </summary>
    public static class BinaryErrorCode
    {
        /// <summary>
        /// The connection already runs <see cref="BifrostBinaryMiddleware.MaxConcurrentRequestsPerConnection"/>
        /// requests. The request never ran: resend it when one completes.
        /// </summary>
        public const string TooManyConcurrentRequests = "TOO_MANY_CONCURRENT_REQUESTS";

        /// <summary>
        /// The request_id names a request still in flight on this connection. The request never
        /// ran: resend it under a fresh id.
        /// </summary>
        public const string RequestIdInFlight = "REQUEST_ID_IN_FLIGHT";
    }
}
