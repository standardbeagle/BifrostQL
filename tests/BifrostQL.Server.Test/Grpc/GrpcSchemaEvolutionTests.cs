using BifrostQL.Core.Model;
using BifrostQL.Server.Grpc;
using FluentAssertions;
using Google.Protobuf.Reflection;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test.Grpc
{
    /// <summary>
    /// Slice-7 criterion 4 at the ASSEMBLED-DOOR level: the field-number stability the manifest unit
    /// tests prove (<see cref="GrpcFieldNumberManifestTests"/>) must survive all the way through to the
    /// serialized <see cref="FileDescriptorSet"/> — the portable artifact a cross-language client
    /// actually compiles against. A number that is stable in the manifest but drifts in the emitted
    /// descriptor would silently break every already-generated client. So these tests carry a manifest
    /// across a schema evolution and assert the PROPERTY on the descriptor set itself: existing field
    /// numbers are STABLE, and a removed column's number is RESERVED — never re-used by a later column.
    /// This is the descriptor-level complement of the manifest-level tests, not a duplicate: it proves
    /// the wire artifact honors the manifest, not just that the manifest tracks numbers.
    /// </summary>
    public class GrpcSchemaEvolutionTests
    {
        [Fact]
        public void Descriptor_set_keeps_existing_field_numbers_stable_when_a_column_is_added()
        {
            var v1 = new[] { Visible("Users", Col("id", "int", pk: true), Col("name", "varchar")) };
            var m1 = GrpcFieldNumberManifest.Empty().Reconcile(v1);
            var before = RowFields(DescriptorFor(v1, m1), "UsersRow");

            // Additive evolution: a new column, same persisted manifest carried forward.
            var v2 = new[] { Visible("Users", Col("id", "int", pk: true), Col("name", "varchar"), Col("email", "varchar")) };
            var m2 = m1.Reconcile(v2);
            var after = RowFields(DescriptorFor(v2, m2), "UsersRow");

            // Every pre-existing field keeps its exact number in the emitted descriptor…
            after["id"].Should().Be(before["id"]);
            after["name"].Should().Be(before["name"]);
            // …and the new column takes the next free number, distinct from every existing one.
            after.Should().ContainKey("email");
            after["email"].Should().NotBe(before["id"]).And.NotBe(before["name"]);
        }

        [Fact]
        public void Descriptor_set_reserves_a_removed_columns_number_and_never_reuses_it()
        {
            var v1 = new[] { Visible("Users", Col("id", "int", pk: true), Col("name", "varchar"), Col("phone", "varchar")) };
            var m1 = GrpcFieldNumberManifest.Empty().Reconcile(v1);
            var phoneNumber = RowFields(DescriptorFor(v1, m1), "UsersRow")["phone"];

            // Remove phone.
            var v2 = new[] { Visible("Users", Col("id", "int", pk: true), Col("name", "varchar")) };
            var m2 = m1.Reconcile(v2);
            var afterRemoval = RowFields(DescriptorFor(v2, m2), "UsersRow");

            // The removed column vanishes from the descriptor's row message…
            afterRemoval.Should().NotContainKey("phone");
            // …and its number is not silently handed to a surviving field.
            afterRemoval.Values.Should().NotContain(phoneNumber);

            // Add a fresh column: the descriptor must give it a NEW number, never the reserved one.
            var v3 = new[] { Visible("Users", Col("id", "int", pk: true), Col("name", "varchar"), Col("fax", "varchar")) };
            var m3 = m2.Reconcile(v3);
            var afterReAdd = RowFields(DescriptorFor(v3, m3), "UsersRow");

            afterReAdd.Should().ContainKey("fax");
            afterReAdd["fax"].Should().NotBe(phoneNumber, "a removed number is reserved and never reused");
        }

        // ---- helpers ----

        /// <summary>Builds the serialized descriptor set for a model version + carried-forward manifest.</summary>
        private static FileDescriptorSet DescriptorFor(IReadOnlyList<GrpcVisibleTable> visible, GrpcFieldNumberManifest manifest)
        {
            var contract = GrpcSchemaGenerator.BuildContract(visible, manifest);
            return FileDescriptorSet.Parser.ParseFrom(GrpcDescriptorSetWriter.Write(contract));
        }

        /// <summary>The field name -> field number map of a message inside the emitted descriptor set.</summary>
        private static IReadOnlyDictionary<string, int> RowFields(FileDescriptorSet set, string messageName)
        {
            var file = set.File.Single(f => f.Name == "bifrostql.proto");
            var message = file.MessageType.Single(m => m.Name == messageName);
            return message.Field.ToDictionary(f => f.Name, f => f.Number);
        }

        private static GrpcVisibleTable Visible(string tableName, params ColumnDto[] columns)
        {
            var table = Substitute.For<IDbTable>();
            table.GraphQlName.Returns(tableName);
            table.DbName.Returns(tableName);
            table.TableSchema.Returns("dbo");
            table.Columns.Returns(columns);
            return new GrpcVisibleTable(table, columns);
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
