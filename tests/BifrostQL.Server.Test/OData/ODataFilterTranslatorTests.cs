using System.Globalization;
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

        /// <summary>
        /// Invariant 5: an integer literal parses with the Integer grammar and invariant glyphs.
        /// The lexer accepts any <c>char.IsDigit</c> digit and exponent characters, so the parse
        /// is the gate: an exponent or a non-ASCII digit is a 400, whatever the thread culture.
        /// </summary>
        [Theory]
        [InlineData("id eq 1e3")]            // exponent: Float grammar would accept it as 1000
        [InlineData("id eq 1E3")]
        [InlineData("id eq ٣")]         // ARABIC-INDIC DIGIT THREE passes char.IsDigit
        public void Integer_literals_outside_the_invariant_integer_grammar_are_rejected(string filter)
        {
            foreach (var culture in new[] { CultureInfo.InvariantCulture, NegCulture() })
            {
                using var _ = new CultureScope(culture);
                var act = () => ODataFilterTranslator.Translate(Entity(), filter, TypeMapper);
                act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(400);
            }
        }

        /// <summary>
        /// The provider picks the glyphs: a negative literal parses with the invariant '-' even
        /// when the thread culture spells the sign "NEG".
        /// </summary>
        [Fact]
        public void A_negative_integer_literal_parses_with_invariant_glyphs_under_a_foreign_culture()
        {
            using var _ = new CultureScope(NegCulture());
            ODataFilterTranslator.Translate(Entity(), "id eq -5", TypeMapper).Should().NotBeNull();
        }

        private static CultureInfo NegCulture()
        {
            var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            culture.NumberFormat.NegativeSign = "NEG";
            return culture;
        }

        private sealed class CultureScope : IDisposable
        {
            private readonly CultureInfo _previous = CultureInfo.CurrentCulture;
            public CultureScope(CultureInfo culture) => CultureInfo.CurrentCulture = culture;
            public void Dispose() => CultureInfo.CurrentCulture = _previous;
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
