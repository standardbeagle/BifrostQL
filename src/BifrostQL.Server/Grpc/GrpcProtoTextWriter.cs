using System.Text;

namespace BifrostQL.Server.Grpc
{
    /// <summary>
    /// Renders a <see cref="GrpcContract"/> to compilable proto3 source text. This is one of
    /// two renderers over the same contract (the other emits the binary descriptor set), so
    /// the <c>.proto</c> and the descriptor set describe byte-identical shapes.
    /// </summary>
    public static class GrpcProtoTextWriter
    {
        public static string Write(GrpcContract contract)
        {
            if (contract is null) throw new ArgumentNullException(nameof(contract));

            var sb = new StringBuilder();
            sb.AppendLine("syntax = \"proto3\";");
            sb.AppendLine();
            sb.AppendLine($"package {contract.Package};");
            if (contract.UsesTimestamp)
            {
                sb.AppendLine();
                sb.AppendLine("import \"google/protobuf/timestamp.proto\";");
            }
            sb.AppendLine();

            foreach (var message in contract.Messages)
            {
                if (message.Fields.Count == 0)
                {
                    sb.AppendLine($"message {message.Name} {{}}");
                }
                else
                {
                    sb.AppendLine($"message {message.Name} {{");
                    foreach (var field in message.Fields)
                    {
                        var label = field.Repeated ? "repeated " : field.Optional ? "optional " : "";
                        sb.AppendLine($"  {label}{TypeToken(field)} {field.Name} = {field.Number};");
                    }
                    sb.AppendLine("}");
                }
                sb.AppendLine();
            }

            sb.AppendLine($"service {contract.Service.Name} {{");
            foreach (var method in contract.Service.Methods)
            {
                var output = method.ServerStreaming ? $"stream {method.OutputType}" : method.OutputType;
                sb.AppendLine($"  rpc {method.Name} ({method.InputType}) returns ({output});");
            }
            sb.AppendLine("}");

            return sb.ToString();
        }

        private static string TypeToken(GrpcField field) =>
            field.MessageName ?? GrpcProtoTypeMapper.ProtoToken(field.Scalar!.Value);
    }
}
