using BifrostQL.Core.Model;
using BifrostQL.Server.Grpc;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test.Grpc
{
    /// <summary>
    /// Criterion 3: reordering the model does not change output, and additive/removal
    /// evolution yields stable/reserved field numbers driven by the manifest, not by the
    /// column read order. Also covers the ADR incompatible-type-change failure.
    /// </summary>
    public class GrpcFieldNumberManifestTests
    {
        [Fact]
        public void Reconcile_reorder_of_columns_does_not_change_numbers()
        {
            var order1 = new[] { Visible("Users", Col("id", "int", pk: true), Col("name", "varchar"), Col("email", "varchar")) };
            var order2 = new[] { Visible("Users", Col("email", "varchar"), Col("id", "int", pk: true), Col("name", "varchar")) };

            var m1 = GrpcFieldNumberManifest.Empty().Reconcile(order1);
            var m2 = GrpcFieldNumberManifest.Empty().Reconcile(order2);

            // Read order is irrelevant: identical numbering regardless of column order.
            m2.ToJson().Should().Be(m1.ToJson());
            m1.NumberOf("UsersRow", "email").Should().Be(m1.NumberOf("UsersRow", "email"));
        }

        [Fact]
        public void Reconcile_additive_column_preserves_all_existing_numbers()
        {
            var before = GrpcFieldNumberManifest.Empty().Reconcile(new[]
            {
                Visible("Users", Col("id", "int", pk: true), Col("name", "varchar")),
            });
            var idNum = before.NumberOf("UsersRow", "id");
            var nameNum = before.NumberOf("UsersRow", "name");

            var after = before.Reconcile(new[]
            {
                Visible("Users", Col("id", "int", pk: true), Col("name", "varchar"), Col("email", "varchar")),
            });

            // Every existing number is unchanged; the new column gets the next free number.
            after.NumberOf("UsersRow", "id").Should().Be(idNum);
            after.NumberOf("UsersRow", "name").Should().Be(nameNum);
            after.NumberOf("UsersRow", "email").Should().Be(3);
        }

        [Fact]
        public void Reconcile_removed_column_reserves_its_number_and_never_reuses_it()
        {
            var before = GrpcFieldNumberManifest.Empty().Reconcile(new[]
            {
                Visible("Users", Col("id", "int", pk: true), Col("name", "varchar"), Col("phone", "varchar")),
            });
            var phoneNum = before.NumberOf("UsersRow", "phone");
            phoneNum.Should().Be(3);

            var afterRemoval = before.Reconcile(new[]
            {
                Visible("Users", Col("id", "int", pk: true), Col("name", "varchar")),
            });

            afterRemoval.NumberOf("UsersRow", "phone").Should().BeNull();
            afterRemoval.Messages["UsersRow"].Reserved.Should().Contain(phoneNum!.Value);
            afterRemoval.Messages["UsersRow"].ReservedNames.Should().Contain("phone");

            // A newly added column must skip the reserved number, never reuse it.
            var afterReAdd = afterRemoval.Reconcile(new[]
            {
                Visible("Users", Col("id", "int", pk: true), Col("name", "varchar"), Col("fax", "varchar")),
            });
            afterReAdd.NumberOf("UsersRow", "fax").Should().Be(4);
            afterReAdd.NumberOf("UsersRow", "fax").Should().NotBe(phoneNum);
        }

        [Fact]
        public void Reconcile_incompatible_type_change_fails_generation()
        {
            var before = GrpcFieldNumberManifest.Empty().Reconcile(new[]
            {
                Visible("Users", Col("id", "int", pk: true), Col("amount", "int")),
            });

            // amount changes int -> decimal (int32 -> string): the wire type of a fixed
            // number cannot change, so generation must fail rather than renumber silently.
            var act = () => before.Reconcile(new[]
            {
                Visible("Users", Col("id", "int", pk: true), Col("amount", "decimal")),
            });

            act.Should().Throw<GrpcSchemaException>().WithMessage("*amount*");
        }

        [Fact]
        public void Json_round_trips_numbers_reserved_and_types()
        {
            var manifest = GrpcFieldNumberManifest.Empty().Reconcile(new[]
            {
                Visible("Users", Col("id", "bigint", pk: true), Col("name", "varchar")),
            });
            manifest = manifest.Reconcile(new[]
            {
                Visible("Users", Col("id", "bigint", pk: true)),
            });

            var restored = GrpcFieldNumberManifest.FromJson(manifest.ToJson());

            restored.NumberOf("UsersRow", "id").Should().Be(manifest.NumberOf("UsersRow", "id"));
            restored.Messages["UsersRow"].Fields["id"].Type.Should().Be("int64");
            restored.Messages["UsersRow"].ReservedNames.Should().Contain("name");
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
