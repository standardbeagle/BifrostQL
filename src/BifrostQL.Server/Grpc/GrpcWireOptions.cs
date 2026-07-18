namespace BifrostQL.Server.Grpc
{
    /// <summary>
    /// Configuration for the opt-in gRPC HTTP/2 front door (slice 3). The adapter is only
    /// registered when the host calls <c>AddBifrostGrpc</c>, so it is off by default; this type
    /// carries the runtime knobs and is validated fail-fast at startup by
    /// <see cref="GrpcWireAdapter"/>.
    ///
    /// <para><b>Fail-closed identity.</b> Bearer identity extraction is a later slice (4). Until
    /// then an unauthenticated call resolves to an empty user context through the shared
    /// <see cref="IBifrostAuthContextFactory"/>, which the transformer pipeline treats as
    /// fail-closed (tenant/policy scope narrows to nothing) — never full unfiltered data. There is
    /// no "allow anonymous" opt-in here by design.</para>
    /// </summary>
    public sealed class GrpcWireOptions
    {
        /// <summary>
        /// The TCP port the dedicated HTTP/2 listener binds. A value outside 1..65535 is a
        /// startup configuration error (fail-fast). A bind failure on this port aborts host
        /// startup — the adapter never comes up half-bound.
        /// </summary>
        public int Port { get; set; } = 5090;

        /// <summary>
        /// The registered GraphQL endpoint path whose cached DbModel/schema/connection the gRPC
        /// reads execute against. Null selects the single registered endpoint; with several
        /// registered it is required and an unknown path fails fast (no silent fallback).
        /// </summary>
        public string? Endpoint { get; set; }

        /// <summary>
        /// When true the listener must be configured for TLS: <see cref="TlsCertificatePath"/>
        /// must resolve to a readable certificate file, or startup aborts. gRPC over cleartext
        /// (h2c) is the default for local/in-proxy deployments; production should terminate TLS.
        /// </summary>
        public bool RequireTls { get; set; }

        /// <summary>Path to the PKCS#12/PEM certificate used when <see cref="RequireTls"/> is set.</summary>
        public string? TlsCertificatePath { get; set; }

        /// <summary>
        /// The hard upper bound on rows a server-streaming List may emit. A streaming read can
        /// never emit unbounded rows: the read intent's Limit is clamped to this value, so a
        /// hostile or accidental full-table stream is bounded by config
        /// (protocol-adapter-security invariant 6). Must be positive.
        /// </summary>
        public int MaxStreamRows { get; set; } = 10_000;

        /// <summary>
        /// The page size a unary List returns. Clamped to <see cref="MaxStreamRows"/>. Must be
        /// positive.
        /// </summary>
        public int ListPageSize { get; set; } = 1_000;

        /// <summary>
        /// The secret keying the HMAC on List page tokens. A configured secret keeps continuation
        /// tokens valid across restarts and across a horizontally-scaled fleet; absent one, a
        /// per-instance random key is generated (integrity-protected but not portable across
        /// restarts/instances) and the trade-off is logged, never silent. The token is position-only,
        /// so the MAC is a tamper/replay guard — the live read pipeline, not the token, is the
        /// authorization boundary (criterion 3).
        /// </summary>
        public string? PageTokenSecret { get; set; }

        /// <summary>How long a List page token stays valid before it fails closed exactly like a forged one.</summary>
        public TimeSpan PageTokenTtl { get; set; } = TimeSpan.FromMinutes(15);

        /// <summary>
        /// The GLOBAL opt-in for the gRPC write surface — Insert/Update/Delete RPCs. OFF by default
        /// (a read-only front door): the whole mutation surface is absent from dynamic dispatch and
        /// from reflection, so with writes disabled no mutation intent is ever built and the RPCs
        /// cannot even be probed for behavior (fail-closed by construction — protocol-adapter-security
        /// invariant 7). Turning it on exposes the mutation RPCs ONLY for tables that also carry the
        /// per-table <c>grpc-write: enabled</c> allow-list metadata, and logs a startup WARNING (a
        /// posture change worth surfacing). Enabling it never widens what a single call may write:
        /// every write still routes through the full <c>TableMutationPipeline</c> under the caller's
        /// identity, so tenant/policy scope is enforced structurally.
        /// </summary>
        public bool EnableWrites { get; set; }

        /// <summary>
        /// The field-number manifest pinning each column's wire number (gRPC Schema Contract ADR).
        /// Defaults to an empty manifest, which allocates deterministic numbers from the live
        /// schema. A checked-in manifest keeps numbers stable across schema drift. The full model
        /// is reconciled against this once at startup so dynamic dispatch and identity-filtered
        /// reflection agree on every field number.
        /// </summary>
        public GrpcFieldNumberManifest Manifest { get; set; } = GrpcFieldNumberManifest.Empty();
    }
}
