using BifrostQL.Core.Model;
using BifrostQL.Server.Grpc;
using FluentAssertions;
using Google.Protobuf.Reflection;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test.Grpc
{
    /// <summary>
    /// Criteria 1 &amp; 4: each visible table emits Get/List/Stream RPC descriptors (Get keyed
    /// by ALL PK columns; a table with no PK fails with a precise diagnostic), nullable
    /// presence and the decimal strategy are honored, artifact export emits compilable
    /// <c>.proto</c> plus a descriptor set, and denied tables/columns never appear — filtered
    /// by the SAME read policy the query path enforces (invariant 4).
    /// </summary>
    public class GrpcSchemaGeneratorTests
    {
        private static IDictionary<string, object?> Admin() =>
            new Dictionary<string, object?> { ["user_id"] = "u-admin", ["roles"] = new[] { "admin" } };

        private static IDictionary<string, object?> Member() =>
            new Dictionary<string, object?> { ["user_id"] = "u-member", ["roles"] = new[] { "member" } };

        [Fact]
        public void Emits_get_list_stream_rpc_per_visible_table()
        {
            var model = Model(Table("Users", Col("id", "int", pk: true), Col("name", "varchar")));

            var artifacts = GrpcSchemaGenerator.Generate(model, GrpcFieldNumberManifest.Empty(), Admin());

            artifacts.Contract.Service.Name.Should().Be("BifrostQuery");
            artifacts.Contract.Service.Methods.Select(m => m.Name)
                .Should().Contain(new[] { "GetUsers", "ListUsers", "StreamUsers" });
            artifacts.Contract.Service.Methods.Single(m => m.Name == "StreamUsers")
                .ServerStreaming.Should().BeTrue();
        }

        [Fact]
        public void Get_request_carries_one_field_per_composite_key_column()
        {
            // Composite PK: the Get request must AND all key columns, never index-zero to the first.
            var model = Model(Table("OrderLines",
                Col("orderId", "int", pk: true),
                Col("lineNo", "int", pk: true),
                Col("qty", "int")));

            var artifacts = GrpcSchemaGenerator.Generate(model, GrpcFieldNumberManifest.Empty(), Admin());

            var request = artifacts.Contract.Messages.Single(m => m.Name == "GetOrderLinesRequest");
            request.Fields.Select(f => f.Name).Should().BeEquivalentTo(new[] { "orderId", "lineNo" });
        }

        [Fact]
        public void Table_without_primary_key_fails_with_precise_diagnostic()
        {
            var model = Model(Table("Logs", Col("message", "varchar")));

            var act = () => GrpcSchemaGenerator.Generate(model, GrpcFieldNumberManifest.Empty(), Admin());

            act.Should().Throw<GrpcSchemaException>()
                .WithMessage("*Logs*")
                .WithMessage("*primary key*");
        }

        [Fact]
        public void Nullable_column_uses_explicit_presence_in_proto()
        {
            var model = Model(Table("Users", Col("id", "int", pk: true), Col("nickname", "varchar", nullable: true)));

            var artifacts = GrpcSchemaGenerator.Generate(model, GrpcFieldNumberManifest.Empty(), Admin());

            artifacts.ProtoText.Should().MatchRegex(@"optional\s+string\s+nickname");
            // The non-nullable key column keeps implicit presence.
            artifacts.ProtoText.Should().NotMatchRegex(@"optional\s+int32\s+id");
        }

        [Fact]
        public void Decimal_column_renders_as_string_in_proto()
        {
            var model = Model(Table("Invoices", Col("id", "int", pk: true), Col("total", "decimal")));

            var artifacts = GrpcSchemaGenerator.Generate(model, GrpcFieldNumberManifest.Empty(), Admin());

            artifacts.ProtoText.Should().MatchRegex(@"string\s+total");
            artifacts.ProtoText.Should().NotContain("double total");
        }

        [Fact]
        public void Timestamp_column_pulls_in_the_well_known_type_import_and_dependency()
        {
            var model = Model(Table("Events", Col("id", "int", pk: true), Col("occurredAt", "datetime")));

            var artifacts = GrpcSchemaGenerator.Generate(model, GrpcFieldNumberManifest.Empty(), Admin());

            artifacts.ProtoText.Should().Contain("import \"google/protobuf/timestamp.proto\";");
            artifacts.ProtoText.Should().MatchRegex(@"google\.protobuf\.Timestamp\s+occurredAt");

            var set = FileDescriptorSet.Parser.ParseFrom(artifacts.DescriptorSet);
            set.File.Select(f => f.Name).Should().Contain("google/protobuf/timestamp.proto");
        }

        [Fact]
        public void Descriptor_set_round_trips_with_service_and_row_message()
        {
            var model = Model(Table("Users", Col("id", "int", pk: true), Col("name", "varchar")));

            var artifacts = GrpcSchemaGenerator.Generate(model, GrpcFieldNumberManifest.Empty(), Admin());

            var set = FileDescriptorSet.Parser.ParseFrom(artifacts.DescriptorSet);
            var file = set.File.Single(f => f.Name == "bifrostql.proto");
            file.Syntax.Should().Be("proto3");
            file.MessageType.Select(m => m.Name).Should().Contain("UsersRow");
            file.Service.Single().Name.Should().Be("BifrostQuery");
            file.Service.Single().Method.Select(m => m.Name)
                .Should().Contain(new[] { "GetUsers", "ListUsers", "StreamUsers" });
        }

        [Fact]
        public void Read_denied_table_is_absent_from_artifacts()
        {
            // policy-actions without "read" denies non-admin reads; the table must vanish entirely.
            var secret = Table("Secrets", policyActions: "create", columns: new[] { Col("id", "int", pk: true) });
            var open = Table("Users", Col("id", "int", pk: true), Col("name", "varchar"));
            var model = Model(secret, open);

            var artifacts = GrpcSchemaGenerator.Generate(model, GrpcFieldNumberManifest.Empty(), Member());

            artifacts.ProtoText.Should().NotContain("Secrets");
            artifacts.ProtoText.Should().Contain("UsersRow");
        }

        [Fact]
        public void Read_denied_column_is_absent_from_the_row_message()
        {
            var users = Table("Users",
                policyActions: "read",
                readDeny: "ssn",
                columns: new[] { Col("id", "int", pk: true), Col("name", "varchar"), Col("ssn", "varchar") });
            var model = Model(users);

            var artifacts = GrpcSchemaGenerator.Generate(model, GrpcFieldNumberManifest.Empty(), Member());

            var row = artifacts.Contract.Messages.Single(m => m.Name == "UsersRow");
            row.Fields.Select(f => f.Name).Should().Contain("name").And.NotContain("ssn");
        }

        [Fact]
        public void Output_is_invariant_to_model_and_column_ordering()
        {
            var a = Model(
                Table("Users", Col("id", "int", pk: true), Col("name", "varchar"), Col("email", "varchar")),
                Table("Orders", Col("id", "int", pk: true), Col("total", "decimal")));
            var b = Model(
                Table("Orders", Col("total", "decimal"), Col("id", "int", pk: true)),
                Table("Users", Col("email", "varchar"), Col("name", "varchar"), Col("id", "int", pk: true)));

            var ra = GrpcSchemaGenerator.Generate(a, GrpcFieldNumberManifest.Empty(), Admin());
            var rb = GrpcSchemaGenerator.Generate(b, GrpcFieldNumberManifest.Empty(), Admin());

            rb.ProtoText.Should().Be(ra.ProtoText);
            rb.DescriptorSet.Should().Equal(ra.DescriptorSet);
        }

        // ---- fixtures (mirror the pgwire catalog-visibility test helpers) ----

        private static IDbModel Model(params IDbTable[] tables)
        {
            var model = Substitute.For<IDbModel>();
            model.Tables.Returns(tables);
            return model;
        }

        private static IDbTable Table(string name, params ColumnDto[] columns) =>
            Table(name, null, null, columns);

        private static IDbTable Table(
            string name,
            string? policyActions = null,
            string? readDeny = null,
            ColumnDto[]? columns = null)
        {
            var table = Substitute.For<IDbTable>();
            table.GraphQlName.Returns(name);
            table.DbName.Returns(name);
            table.TableSchema.Returns("dbo");
            table.Columns.Returns(columns ?? Array.Empty<ColumnDto>());
            table.GetMetadataValue(MetadataKeys.Policy.Actions).Returns(policyActions);
            table.GetMetadataValue(MetadataKeys.Policy.ReadDeny).Returns(readDeny);
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
