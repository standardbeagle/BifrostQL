using BifrostQL.Core.Model;
using BifrostQL.Server.OData;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test.OData
{
    /// <summary>
    /// The AST→<c>TableFilter</c> translation in isolation: property names are resolved against the
    /// visible column set (unknown → 400), and each literal is validated/coerced against its target
    /// column's EDM type so an incompatible literal (a string bound to an integer, a number bound to
    /// a string, a non-string in contains) is a clean 400 — never a value silently forced into a SQL
    /// parameter of the wrong type. A well-formed filter over compatible types produces a non-null
    /// filter.
    /// </summary>
    public sealed class ODataFilterTranslatorTests
    {
        private static readonly ITypeMapper TypeMapper = AnsiSqlTypeMapper.Instance;

        [Fact]
        public void Null_filter_text_yields_no_filter()
        {
            ODataFilterTranslator.Translate(Entity(), null, TypeMapper).Should().BeNull();
            ODataFilterTranslator.Translate(Entity(), "   ", TypeMapper).Should().BeNull();
        }

        [Theory]
        [InlineData("name eq 'alpha'")]      // string ← string
        [InlineData("id eq 1")]              // int ← number
        [InlineData("price gt 2.5")]         // double ← number
        [InlineData("active eq true")]       // boolean ← boolean
        [InlineData("note eq null")]         // null test
        [InlineData("id in (1, 2, 3)")]      // in-list, ints
        [InlineData("contains(name, 'a')")]  // contains on a string column
        [InlineData("not (id eq 1 or name eq 'x')")] // not + boolean logic
        public void Well_formed_compatible_filters_translate_to_a_filter(string filter)
        {
            ODataFilterTranslator.Translate(Entity(), filter, TypeMapper).Should().NotBeNull();
        }

        [Theory]
        [InlineData("id eq 'abc'")]             // string literal → integer column
        [InlineData("id eq 1.5")]               // non-integer number → integer column
        [InlineData("name eq 5")]               // number → string column
        [InlineData("active eq 3")]             // number → boolean column
        [InlineData("contains(id, 'x')")]       // contains on a non-string column
        [InlineData("contains(name, 5)")]       // non-string literal in contains
        [InlineData("id in (1, 'x')")]          // a mistyped element in an in-list
        [InlineData("id eq 99999999999999999999999999999")] // overflows Int64
        public void Incompatible_literals_are_rejected_as_bad_request(string filter)
        {
            var act = () => ODataFilterTranslator.Translate(Entity(), filter, TypeMapper);
            act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(400);
        }

        [Fact]
        public void Unknown_property_is_rejected()
        {
            var act = () => ODataFilterTranslator.Translate(Entity(), "nope eq 1", TypeMapper);
            act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(400);
        }

        // ---- fixtures -----------------------------------------------------------------------

        private static ColumnDto Column(string dbName, string graphQlName, string dataType, bool isKey = false)
            => new()
            {
                ColumnName = dbName,
                GraphQlName = graphQlName,
                DataType = dataType,
                OrdinalPosition = 1,
                IsPrimaryKey = isKey,
                IsNullable = true,
            };

        private static ODataEntity Entity()
        {
            var columns = new[]
            {
                Column("id", "id", "integer", isKey: true),
                Column("name", "name", "text"),
                Column("price", "price", "real"),
                Column("active", "active", "boolean"),
                Column("note", "note", "text"),
            };
            var table = Substitute.For<IDbTable>();
            table.DbName.Returns("Widgets");
            table.GraphQlName.Returns("Widgets");
            table.TableSchema.Returns("main");
            return new ODataEntity(table, columns, columns.Where(c => c.IsPrimaryKey).ToList(),
                Array.Empty<ODataNavigation>());
        }
    }
}
