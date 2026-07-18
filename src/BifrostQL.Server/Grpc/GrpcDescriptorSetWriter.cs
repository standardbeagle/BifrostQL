using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace BifrostQL.Server.Grpc
{
    /// <summary>
    /// Renders a <see cref="GrpcContract"/> to a serialized protobuf
    /// <see cref="FileDescriptorSet"/> — the portable, tooling-consumable descriptor a
    /// <c>.proto</c> compiles to. Built from the same contract as the <c>.proto</c> text so
    /// the two artifacts cannot drift. When any column maps to a Timestamp, the well-known
    /// <c>google/protobuf/timestamp.proto</c> descriptor is included so the set is
    /// self-contained.
    /// </summary>
    public static class GrpcDescriptorSetWriter
    {
        private const string FileName = "bifrostql.proto";
        private const string TimestampFile = "google/protobuf/timestamp.proto";
        private const string TimestampType = ".google.protobuf.Timestamp";

        public static byte[] Write(GrpcContract contract)
        {
            if (contract is null) throw new ArgumentNullException(nameof(contract));

            var file = new FileDescriptorProto
            {
                Name = FileName,
                Package = contract.Package,
                Syntax = "proto3",
            };

            if (contract.UsesTimestamp)
                file.Dependency.Add(TimestampFile);

            foreach (var message in contract.Messages)
                file.MessageType.Add(ToDescriptor(message, contract.Package));

            var service = new ServiceDescriptorProto { Name = contract.Service.Name };
            foreach (var method in contract.Service.Methods)
            {
                service.Method.Add(new MethodDescriptorProto
                {
                    Name = method.Name,
                    InputType = $".{contract.Package}.{method.InputType}",
                    OutputType = $".{contract.Package}.{method.OutputType}",
                    ServerStreaming = method.ServerStreaming,
                });
            }
            file.Service.Add(service);

            var set = new FileDescriptorSet();
            // Dependencies precede dependents so an in-order loader resolves references.
            if (contract.UsesTimestamp)
                set.File.Add(Timestamp.Descriptor.File.ToProto());
            set.File.Add(file);

            return set.ToByteArray();
        }

        private static DescriptorProto ToDescriptor(GrpcMessage message, string package)
        {
            var descriptor = new DescriptorProto { Name = message.Name };

            foreach (var field in message.Fields)
            {
                var proto = new FieldDescriptorProto
                {
                    Name = field.Name,
                    Number = field.Number,
                };

                if (field.MessageName is not null)
                {
                    proto.Type = FieldDescriptorProto.Types.Type.Message;
                    proto.TypeName = $".{package}.{field.MessageName}";
                }
                else if (field.Scalar == GrpcScalarKind.Timestamp)
                {
                    proto.Type = FieldDescriptorProto.Types.Type.Message;
                    proto.TypeName = TimestampType;
                }
                else
                {
                    proto.Type = ToProtoType(field.Scalar!.Value);
                }

                if (field.Repeated)
                {
                    proto.Label = FieldDescriptorProto.Types.Label.Repeated;
                }
                else
                {
                    proto.Label = FieldDescriptorProto.Types.Label.Optional;
                    if (field.Optional)
                    {
                        // proto3 explicit presence: a synthetic oneof "_<name>" the field lives in.
                        proto.Proto3Optional = true;
                        proto.OneofIndex = descriptor.OneofDecl.Count;
                        descriptor.OneofDecl.Add(new OneofDescriptorProto { Name = $"_{field.Name}" });
                    }
                }

                descriptor.Field.Add(proto);
            }

            return descriptor;
        }

        private static FieldDescriptorProto.Types.Type ToProtoType(GrpcScalarKind kind) => kind switch
        {
            GrpcScalarKind.Int32 => FieldDescriptorProto.Types.Type.Int32,
            GrpcScalarKind.Int64 => FieldDescriptorProto.Types.Type.Int64,
            GrpcScalarKind.Double => FieldDescriptorProto.Types.Type.Double,
            GrpcScalarKind.Float => FieldDescriptorProto.Types.Type.Float,
            GrpcScalarKind.Bool => FieldDescriptorProto.Types.Type.Bool,
            GrpcScalarKind.String => FieldDescriptorProto.Types.Type.String,
            GrpcScalarKind.Bytes => FieldDescriptorProto.Types.Type.Bytes,
            _ => FieldDescriptorProto.Types.Type.String,
        };
    }
}
