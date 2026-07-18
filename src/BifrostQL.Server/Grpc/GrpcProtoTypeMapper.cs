using BifrostQL.Core.Utils;

namespace BifrostQL.Server.Grpc
{
    /// <summary>
    /// The proto3 wire type a database column maps to on the gRPC surface. All
    /// kinds except <see cref="Timestamp"/> are protobuf scalars; Timestamp is the
    /// well-known message <c>google.protobuf.Timestamp</c>.
    /// </summary>
    public enum GrpcScalarKind
    {
        Int32,
        Int64,
        Double,
        Float,
        Bool,
        String,
        Bytes,
        Timestamp,
    }

    /// <summary>
    /// Maps a column's effective SQL data type to its proto3 wire type, per the
    /// gRPC Schema Contract ADR. Two decisions this centralizes and must preserve:
    /// <list type="bullet">
    /// <item><b>decimal → string.</b> <c>decimal</c>/<c>numeric</c>/<c>money</c>
    /// carry canonical decimal text as proto <c>string</c>, never <c>double</c> —
    /// correcting the lossy float mapping of the seed generator (same "exact
    /// numerics as decimal strings" rule the client stack uses for Decimal keys).</item>
    /// <item><b>bigint stays int64.</b> Exact on the protobuf wire.</item>
    /// </list>
    /// Nullable presence is orthogonal to the type and handled by the contract
    /// builder (proto3 explicit presence), not here.
    /// </summary>
    public static class GrpcProtoTypeMapper
    {
        public static GrpcScalarKind Map(string effectiveDataType)
        {
            return StringNormalizer.NormalizeType(effectiveDataType) switch
            {
                "int" or "smallint" or "tinyint" => GrpcScalarKind.Int32,
                "bigint" => GrpcScalarKind.Int64,
                // ADR decimal strategy: exact decimal text, never a lossy double.
                "decimal" or "numeric" or "money" or "smallmoney" => GrpcScalarKind.String,
                "float" => GrpcScalarKind.Double,
                "real" => GrpcScalarKind.Float,
                "bit" => GrpcScalarKind.Bool,
                "datetime" or "datetime2" or "datetimeoffset" or "smalldatetime" or "date"
                    => GrpcScalarKind.Timestamp,
                // SQL Server rowversion/timestamp is an 8-byte binary token, not temporal.
                "binary" or "varbinary" or "image" or "rowversion" or "timestamp"
                    => GrpcScalarKind.Bytes,
                _ => GrpcScalarKind.String,
            };
        }

        /// <summary>
        /// The proto-source type token for a scalar kind (e.g. <c>int64</c>,
        /// <c>google.protobuf.Timestamp</c>). This is the value recorded in the
        /// field-number manifest so an incompatible type change on an existing
        /// number can be detected and fails generation.
        /// </summary>
        public static string ProtoToken(GrpcScalarKind kind) => kind switch
        {
            GrpcScalarKind.Int32 => "int32",
            GrpcScalarKind.Int64 => "int64",
            GrpcScalarKind.Double => "double",
            GrpcScalarKind.Float => "float",
            GrpcScalarKind.Bool => "bool",
            GrpcScalarKind.String => "string",
            GrpcScalarKind.Bytes => "bytes",
            GrpcScalarKind.Timestamp => "google.protobuf.Timestamp",
            _ => "string",
        };
    }
}
