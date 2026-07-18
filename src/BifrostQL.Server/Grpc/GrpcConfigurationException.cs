namespace BifrostQL.Server.Grpc
{
    /// <summary>
    /// Thrown at startup when the gRPC front door is misconfigured — an out-of-range port, a
    /// TLS requirement with no readable certificate, or a non-positive stream/page bound. It is a
    /// startup-time diagnostic (not a wire fault): thrown from <see cref="GrpcWireAdapter.StartAsync"/>
    /// so a misconfigured adapter aborts host startup rather than coming up half-configured
    /// (fail-fast). It never reaches an adversary-controlled wire path, so it deliberately derives
    /// from <see cref="Exception"/> rather than a protocol-exception base.
    /// </summary>
    public sealed class GrpcConfigurationException : Exception
    {
        public GrpcConfigurationException(string message) : base(message) { }
    }
}
