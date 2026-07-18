namespace BifrostQL.Server.Grpc
{
    /// <summary>
    /// Thrown when a gRPC descriptor cannot be generated from the cached
    /// <c>DbModel</c> — a startup-time diagnostic, not a wire-protocol fault.
    /// gRPC slice 2 builds descriptors only (no connection handler, no Kestrel
    /// wiring), so this deliberately derives from <see cref="Exception"/> rather
    /// than a protocol-adapter base: there is no catch clause on an adversary
    /// path for it to escape (protocol-adapter-security invariant 1 governs the
    /// wire exception a later hosting slice adds). It exists to <b>fail fast at
    /// startup with a precise, actionable message</b> — never to silently emit a
    /// broken or renumbered contract.
    /// </summary>
    public sealed class GrpcSchemaException : Exception
    {
        public GrpcSchemaException(string message) : base(message) { }
    }
}
