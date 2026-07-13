using System.Security.Cryptography.X509Certificates;

namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// Configuration for the PostgreSQL wire-protocol front door (slice 1: startup,
    /// TLS, authentication). The query surface it fronts is selected by
    /// <see cref="Endpoint"/> (a registered BifrostQL endpoint path); the query loop
    /// itself arrives in a later slice.
    /// </summary>
    /// <summary>The authentication challenge the front door issues after startup.</summary>
    public enum PgAuthMethod
    {
        /// <summary>
        /// SCRAM-SHA-256 (RFC 7677): the secret never crosses the wire. The secure
        /// default; use it whenever the credential store holds a shared secret.
        /// </summary>
        ScramSha256,

        /// <summary>
        /// AuthenticationCleartextPassword: the secret crosses the (TLS-wrapped) wire.
        /// For credential sources that cannot participate in SCRAM — e.g. an OIDC
        /// client-secret exchanged server-side for a token.
        /// </summary>
        Cleartext,
    }

    public sealed class PgWireOptions
    {
        /// <summary>TCP port the front door listens on. Default 5432 (the PostgreSQL port).</summary>
        public int Port { get; set; } = 5432;

        /// <summary>Which authentication challenge to issue. SCRAM-SHA-256 by default.</summary>
        public PgAuthMethod AuthMethod { get; set; } = PgAuthMethod.ScramSha256;

        /// <summary>
        /// The server certificate presented when a client issues SSLRequest. Required:
        /// the front door refuses to start without it rather than silently answering 'N'
        /// (no-TLS) to every client — credentials must never cross the wire in the clear
        /// by misconfiguration. TLS negotiation itself remains client-initiated per the
        /// protocol (a client that never sends SSLRequest is answered on the raw socket).
        /// </summary>
        public X509Certificate2? ServerCertificate { get; set; }

        /// <summary>
        /// Registered BifrostQL endpoint path (e.g. <c>/graphql</c>) whose model, schema
        /// and connection authenticated sessions execute against. Null selects the single
        /// registered endpoint.
        /// </summary>
        public string? Endpoint { get; set; }
    }
}
