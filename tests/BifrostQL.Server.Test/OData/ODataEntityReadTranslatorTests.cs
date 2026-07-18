using BifrostQL.Core.Model;
using BifrostQL.Server.OData;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test.OData
{
    /// <summary>
    /// Pure translation of an entity-set read plus its $select/$orderby/$top/$skip options into a
    /// programmatic <c>GqlObjectQuery</c>: property names are validated against the identity-filtered
    /// column set (unknown/ambiguous rejected, never interpolated), ordering is made a stable total
    /// order by appending the full primary key, $top is bounded and $skip non-negative, and the
    /// untrusted numeric options collapse malformed/overflow values to a clean 400 — never an
    /// unhandled parse fault. No Filter/WHERE is ever built here (the pipeline owns row scope).
    /// </summary>
    public sealed class ODataEntityReadTranslatorTests
    {
        private const int DefaultPageSize = 100;
        private const int MaxPageSize = 1000;

        // ---- $select / property resolution --------------------------------------------------

        [Fact]
        public void No_options_projects_all_columns_sorts_by_key_and_applies_the_default_page_size()
        {
            var read = Translate(Entity(), new ODataReadOptions(null, null, null, null));

            read.ProjectedColumns.Select(c => c.DbName).Should().Equal("id", "name", "total");
            read.Query.Sort.Should().Equal("id_asc"); // default order = primary key ascending
            read.Query.Limit.Should().Be(DefaultPageSize);
            read.Query.Offset.Should().BeNull();
            read.Query.Filter.Should().BeNull("the adapter builds no predicate; the pipeline scopes rows");
        }

        [Fact]
        public void Select_projects_only_the_named_columns_in_order_and_de_duplicates()
        {
            var read = Translate(Entity(), new ODataReadOptions("total, id, total", null, null, null));

            read.ProjectedColumns.Select(c => c.DbName).Should().Equal("total", "id");
            read.Query.ScalarColumns.Should().HaveCount(2);
        }

        [Fact]
        public void Select_rejects_an_unknown_property()
        {
            var act = () => Translate(Entity(), new ODataReadOptions("nope", null, null, null));
            act.Should().Throw<ODataProtocolException>()
                .Which.HttpStatus.Should().Be(400);
        }

        [Fact]
        public void Select_rejects_an_ambiguous_property()
        {
            // Two visible columns whose EDM names differ only by case are ambiguous under the
            // case-insensitive property match — reported, never silently resolved to one.
            var entity = EntityWith(
                Column("id", "id", isKey: true),
                Column("name_lower", "name"),
                Column("name_title", "Name"));

            var act = () => Translate(entity, new ODataReadOptions("name", null, null, null));
            act.Should().Throw<ODataProtocolException>()
                .Which.Message.Should().Contain("ambiguous");
        }

        [Fact]
        public void Select_rejects_an_empty_property_name()
        {
            var act = () => Translate(Entity(), new ODataReadOptions("id,,name", null, null, null));
            act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(400);
        }

        // ---- $orderby (stable total order) --------------------------------------------------

        [Fact]
        public void Orderby_maps_directions_and_appends_the_key_as_a_stable_tiebreak()
        {
            var read = Translate(Entity(), new ODataReadOptions(null, "name desc, total asc", null, null));

            // Requested keys first, then the primary key appended ascending for determinism.
            read.Query.Sort.Should().Equal("name_desc", "total_asc", "id_asc");
        }

        [Fact]
        public void Orderby_defaults_direction_to_ascending_and_does_not_duplicate_a_named_key()
        {
            var read = Translate(Entity(), new ODataReadOptions(null, "id desc", null, null));

            // The key was named explicitly (desc), so the tiebreak does not re-append it ascending.
            read.Query.Sort.Should().Equal("id_desc");
        }

        [Fact]
        public void Orderby_appends_the_full_composite_key_in_schema_order()
        {
            var entity = EntityWith(
                Column("order_id", "orderId", isKey: true),
                Column("line_no", "lineNo", isKey: true),
                Column("qty", "qty"));

            var read = Translate(entity, new ODataReadOptions(null, "qty asc", null, null));

            // Both key columns appended, in order — never a first-column guess.
            read.Query.Sort.Should().Equal("qty_asc", "orderId_asc", "lineNo_asc");
        }

        [Fact]
        public void Orderby_rejects_an_invalid_direction()
        {
            var act = () => Translate(Entity(), new ODataReadOptions(null, "name upward", null, null));
            act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(400);
        }

        [Fact]
        public void Orderby_rejects_an_unknown_property()
        {
            var act = () => Translate(Entity(), new ODataReadOptions(null, "missing asc", null, null));
            act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(400);
        }

        // ---- $top / $skip bounds ------------------------------------------------------------

        [Fact]
        public void Top_maps_to_limit_and_skip_to_offset()
        {
            var read = Translate(Entity(), new ODataReadOptions(null, null, "5", "10"));
            read.Query.Limit.Should().Be(5);
            read.Query.Offset.Should().Be(10);
        }

        [Fact]
        public void Top_is_clamped_to_the_maximum_page_size()
        {
            var read = Translate(Entity(), new ODataReadOptions(null, null, "100000", null));
            read.Query.Limit.Should().Be(MaxPageSize);
        }

        [Fact]
        public void Top_that_overflows_int_is_a_clean_bad_request()
        {
            // 29 nines: a well-formed but out-of-range value that int.Parse would overflow on.
            var act = () => Translate(Entity(), new ODataReadOptions(null, null, new string('9', 29), null));
            act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(400);
        }

        [Theory]
        [InlineData("abc")]
        [InlineData("-1")]
        [InlineData("1.5")]
        public void Top_that_is_malformed_or_negative_is_a_clean_bad_request(string top)
        {
            var act = () => Translate(Entity(), new ODataReadOptions(null, null, top, null));
            act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(400);
        }

        [Fact]
        public void Skip_negative_is_rejected()
        {
            var act = () => Translate(Entity(), new ODataReadOptions(null, null, null, "-3"));
            act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(400);
        }

        [Fact]
        public void Skip_zero_leaves_offset_unset()
        {
            var read = Translate(Entity(), new ODataReadOptions(null, null, null, "0"));
            read.Query.Offset.Should().BeNull();
        }

        // ---- ODataReadOptions.FromQuery (wire-level option rules) ---------------------------

        [Fact]
        public void FromQuery_parses_the_supported_options()
        {
            var options = ODataReadOptions.FromQuery(Query(
                ("$select", "id"), ("$orderby", "name desc"), ("$top", "5"), ("$skip", "2")));

            options.Should().Be(new ODataReadOptions("id", "name desc", "5", "2"));
        }

        [Fact]
        public void FromQuery_rejects_a_duplicated_system_option()
        {
            var query = new QueryCollection(new Dictionary<string, StringValues>
            {
                ["$top"] = new StringValues(new[] { "1", "2" }),
            });

            var act = () => ODataReadOptions.FromQuery(query);
            act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(400);
        }

        [Theory]
        [InlineData("$filter")]
        [InlineData("$expand")]
        public void FromQuery_reports_deferred_options_as_not_implemented(string option)
        {
            var act = () => ODataReadOptions.FromQuery(Query((option, "x")));
            act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(501);
        }

        [Fact]
        public void FromQuery_rejects_an_unknown_system_option()
        {
            var act = () => ODataReadOptions.FromQuery(Query(("$frobnicate", "x")));
            act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(400);
        }

        [Fact]
        public void FromQuery_ignores_custom_non_dollar_options()
        {
            var options = ODataReadOptions.FromQuery(Query(("api-version", "4.0"), ("$top", "3")));
            options.Should().Be(new ODataReadOptions(null, null, "3", null));
        }

        // ---- fixtures -----------------------------------------------------------------------

        private static ODataEntityRead Translate(ODataEntity entity, ODataReadOptions options)
            => ODataEntityReadTranslator.Translate(entity, options, DefaultPageSize, MaxPageSize);

        private static IQueryCollection Query(params (string Key, string Value)[] pairs)
            => new QueryCollection(pairs.ToDictionary(p => p.Key, p => new StringValues(p.Value)));

        private static ColumnDto Column(string dbName, string graphQlName, bool isKey = false, bool nullable = true)
            => new()
            {
                ColumnName = dbName,
                GraphQlName = graphQlName,
                DataType = "text",
                OrdinalPosition = 1,
                IsPrimaryKey = isKey,
                IsNullable = nullable,
            };

        private static ODataEntity Entity() => EntityWith(
            Column("id", "id", isKey: true, nullable: false),
            Column("name", "name"),
            Column("total", "total"));

        private static ODataEntity EntityWith(params ColumnDto[] columns)
        {
            var table = Substitute.For<IDbTable>();
            table.DbName.Returns("t");
            table.GraphQlName.Returns("t");
            table.TableSchema.Returns("main");
            var keys = columns.Where(c => c.IsPrimaryKey).ToList();
            return new ODataEntity(table, columns, keys, Array.Empty<ODataNavigation>());
        }
    }
}
