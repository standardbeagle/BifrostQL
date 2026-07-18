namespace BifrostQL.Server.Grpc
{
    /// <summary>
    /// A deterministic, renderer-agnostic description of the gRPC read surface for one
    /// profile projection. Built once from the visible <c>DbModel</c> + reconciled
    /// field-number manifest, then rendered to both a <c>.proto</c> and a
    /// <c>FileDescriptorSet</c> — so the two artifacts cannot drift from each other.
    /// Messages and fields are emitted in a stable order (message name, then field
    /// number), which is what makes the output invariant to database read order.
    /// </summary>
    public sealed record GrpcContract(
        string Package,
        IReadOnlyList<GrpcMessage> Messages,
        GrpcService Service,
        bool UsesTimestamp);

    public sealed record GrpcMessage(string Name, IReadOnlyList<GrpcField> Fields);

    /// <summary>
    /// One field. Exactly one of <see cref="Scalar"/> / <see cref="MessageName"/> is set:
    /// a scalar kind (Timestamp renders as the WKT message), or a reference to a locally
    /// declared row message. <see cref="Optional"/> is proto3 explicit presence (a NULL is
    /// wire-distinguishable from a typed default); <see cref="Repeated"/> is a list field.
    /// </summary>
    public sealed record GrpcField(
        string Name,
        int Number,
        GrpcScalarKind? Scalar,
        string? MessageName,
        bool Optional,
        bool Repeated);

    public sealed record GrpcService(string Name, IReadOnlyList<GrpcMethod> Methods);

    public sealed record GrpcMethod(
        string Name,
        string InputType,
        string OutputType,
        bool ServerStreaming);
}
