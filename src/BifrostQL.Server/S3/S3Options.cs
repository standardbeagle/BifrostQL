namespace BifrostQL.Server.S3
{
    /// <summary>
    /// Configuration for the opt-in S3-compatible HTTP endpoint. Disabled by default; a host
    /// enables it explicitly via <see cref="BifrostSetupOptions.AddS3Endpoint"/> or
    /// <see cref="BifrostMultiDbOptions.AddS3Endpoint"/>, mirroring the opt-in posture of the
    /// other protocol adapters (pgwire, RESP).
    /// </summary>
    public sealed class S3Options
    {
        /// <summary>Whether the S3 endpoint is enabled. Default: false (opt-in).</summary>
        public bool Enabled { get; set; }

        /// <summary>Path prefix the endpoint listens under. Default: "/s3".</summary>
        public string PathPrefix { get; set; } = "/s3";

        /// <summary>
        /// The AWS region this deployment's SigV4 credential scope must match
        /// (e.g. "us-east-1"). Requests scoped to any other region are rejected.
        /// </summary>
        public string Region { get; set; } = "us-east-1";

        /// <summary>
        /// Maximum allowed clock skew between a signed request's timestamp and server time,
        /// for both header (X-Amz-Date) and presigned (X-Amz-Date) authentication. AWS's own
        /// default window.
        /// </summary>
        public TimeSpan MaxClockSkew { get; set; } = TimeSpan.FromMinutes(15);

        /// <summary>
        /// Maximum expiry a presigned URL may declare via X-Amz-Expires. AWS's own cap is 7
        /// days; requests declaring a longer expiry are rejected before the signature is even
        /// checked.
        /// </summary>
        public TimeSpan MaxPresignedExpiry { get; set; } = TimeSpan.FromDays(7);

        /// <summary>Maximum request body size accepted, enforced from Content-Length before any buffering.</summary>
        public long MaxBodyBytes { get; set; } = 64 * 1024 * 1024;

        /// <summary>Maximum total size of request headers, enforced before the body is read.</summary>
        public int MaxHeaderBytes { get; set; } = 16 * 1024;

        /// <summary>Maximum length of the raw request URL (path + query string).</summary>
        public int MaxUrlLength { get; set; } = 8 * 1024;

        /// <summary>
        /// The registered GraphQL endpoint whose cached model/connection the list
        /// operations read against. Null selects the single registered endpoint.
        /// </summary>
        public string? Endpoint { get; set; }

        /// <summary>Default number of keys returned per ListObjectsV2 page when the request omits max-keys. S3's own default.</summary>
        public int DefaultMaxKeys { get; set; } = 1000;

        /// <summary>Hard cap on the per-page key count, applied to any client-requested max-keys. S3's own cap.</summary>
        public int MaxKeysLimit { get; set; } = 1000;

        /// <summary>
        /// Maximum number of candidate rows a single ListObjectsV2 scan may materialize.
        /// A bucket with more addressable objects than this fails fast rather than
        /// returning a silently truncated (and therefore lying) listing.
        /// </summary>
        public int MaxListMaterialize { get; set; } = 10_000;

        /// <summary>
        /// Optional HMAC secret binding and integrity-protecting continuation tokens.
        /// When set, tokens survive restarts and resolve across a scaled fleet; when
        /// unset, a per-instance random key is generated (tokens then live only for the
        /// process's lifetime — see <see cref="S3Listing"/>). Must not be a value a
        /// client could learn: it is the only thing preventing a forged resume position.
        /// </summary>
        public string? ContinuationTokenSecret { get; set; }
    }
}
