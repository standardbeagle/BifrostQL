using BifrostQL.Server.Grpc;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Grpc
{
    /// <summary>
    /// Criterion 2: the type mapping covers scalar numerics, bool, bytes, string,
    /// Timestamp, and the ADR decimal strategy (decimal -> string, never a lossy double).
    /// </summary>
    public class GrpcProtoTypeMapperTests
    {
        [Theory]
        [InlineData("int", GrpcScalarKind.Int32)]
        [InlineData("smallint", GrpcScalarKind.Int32)]
        [InlineData("tinyint", GrpcScalarKind.Int32)]
        [InlineData("bigint", GrpcScalarKind.Int64)]
        [InlineData("float", GrpcScalarKind.Double)]
        [InlineData("real", GrpcScalarKind.Float)]
        [InlineData("bit", GrpcScalarKind.Bool)]
        [InlineData("varchar", GrpcScalarKind.String)]
        [InlineData("nvarchar", GrpcScalarKind.String)]
        [InlineData("uniqueidentifier", GrpcScalarKind.String)]
        [InlineData("varbinary", GrpcScalarKind.Bytes)]
        [InlineData("binary", GrpcScalarKind.Bytes)]
        [InlineData("image", GrpcScalarKind.Bytes)]
        [InlineData("rowversion", GrpcScalarKind.Bytes)]
        [InlineData("datetime", GrpcScalarKind.Timestamp)]
        [InlineData("datetime2", GrpcScalarKind.Timestamp)]
        [InlineData("datetimeoffset", GrpcScalarKind.Timestamp)]
        [InlineData("date", GrpcScalarKind.Timestamp)]
        public void Map_covers_each_scalar_family(string dataType, GrpcScalarKind expected)
        {
            GrpcProtoTypeMapper.Map(dataType).Should().Be(expected);
        }

        [Theory]
        [InlineData("decimal")]
        [InlineData("numeric")]
        [InlineData("money")]
        [InlineData("smallmoney")]
        public void Map_carries_decimal_as_string_not_double(string dataType)
        {
            // ADR: exact decimal text on the wire, never a precision-losing double.
            GrpcProtoTypeMapper.Map(dataType).Should().Be(GrpcScalarKind.String);
            GrpcProtoTypeMapper.Map(dataType).Should().NotBe(GrpcScalarKind.Double);
        }

        [Fact]
        public void Map_is_case_and_whitespace_insensitive()
        {
            GrpcProtoTypeMapper.Map("  BigInt ").Should().Be(GrpcScalarKind.Int64);
        }

        [Fact]
        public void ProtoToken_renders_timestamp_as_well_known_type()
        {
            GrpcProtoTypeMapper.ProtoToken(GrpcScalarKind.Timestamp)
                .Should().Be("google.protobuf.Timestamp");
            GrpcProtoTypeMapper.ProtoToken(GrpcScalarKind.Int64).Should().Be("int64");
        }
    }
}
