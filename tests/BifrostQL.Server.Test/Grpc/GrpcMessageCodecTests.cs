using BifrostQL.Core.Model;
using BifrostQL.Server.Grpc;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test.Grpc
{
    /// <summary>
    /// The contract-driven wire codec (no compiled stubs) round-trips a row correctly: server-side
    /// <c>GrpcMessageCodec.EncodeRow</c> → client-side decode. Covers the type strategy (decimal as
    /// string, bit as bool, datetime as Timestamp) and proto3 presence (a NULL nullable column is
    /// simply absent, so a masked/denied column never surfaces on the wire).
    /// </summary>
    public class GrpcMessageCodecTests
    {
        private static GrpcMessage RowMessage()
        {
            var model = Model(Table("Widgets",
                Col("id", "int", pk: true),
                Col("name", "varchar"),
                Col("total", "decimal"),
                Col("active", "bit"),
                Col("created", "datetime"),
                Col("nickname", "varchar", nullable: true)));
            var artifacts = GrpcSchemaGenerator.Generate(model, GrpcFieldNumberManifest.Empty(), Admin());
            return artifacts.Contract.Messages.Single(m => m.Name == "WidgetsRow");
        }

        [Fact]
        public void Encodes_and_decodes_scalars_with_the_type_strategy()
        {
            var rowMessage = RowMessage();
            var created = new DateTime(2026, 7, 18, 3, 4, 5, DateTimeKind.Utc);
            var row = new Dictionary<string, object?>
            {
                ["id"] = 7,
                ["name"] = "hi",
                ["total"] = 12.5m,
                ["active"] = true,
                ["created"] = created,
                ["nickname"] = null, // NULL nullable column → absent on the wire
            };

            var decoded = GrpcWireTestClient.DecodeRow(rowMessage, GrpcMessageCodec.EncodeRow(rowMessage, row));

            Convert.ToInt32(decoded["id"]).Should().Be(7);
            decoded["name"].Should().Be("hi");
            decoded["total"].Should().Be("12.5");        // decimal carried as canonical string
            decoded["active"].Should().Be(true);         // bit → bool
            ((DateTime)decoded["created"]!).Should().Be(created); // datetime → Timestamp round-trip
            decoded.Should().NotContainKey("nickname");  // proto3 presence: NULL is absent
        }

        private static IDictionary<string, object?> Admin() =>
            new Dictionary<string, object?> { ["user_id"] = "u", ["roles"] = new[] { "admin" } };

        private static IDbModel Model(params IDbTable[] tables)
        {
            var model = Substitute.For<IDbModel>();
            model.Tables.Returns(tables);
            return model;
        }

        private static IDbTable Table(string name, params ColumnDto[] columns)
        {
            var table = Substitute.For<IDbTable>();
            table.GraphQlName.Returns(name);
            table.DbName.Returns(name);
            table.TableSchema.Returns("dbo");
            table.Columns.Returns(columns);
            table.GetMetadataValue(MetadataKeys.Policy.Actions).Returns((string?)null);
            table.GetMetadataValue(MetadataKeys.Policy.ReadDeny).Returns((string?)null);
            return table;
        }

        private static ColumnDto Col(string name, string dataType, bool pk = false, bool nullable = false) => new()
        {
            ColumnName = name,
            GraphQlName = name,
            DataType = dataType,
            IsPrimaryKey = pk,
            IsNullable = nullable,
        };
    }
}
