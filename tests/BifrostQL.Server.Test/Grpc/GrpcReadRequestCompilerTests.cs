using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Server.Grpc;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test.Grpc
{
    /// <summary>
    /// Criteria 1 &amp; 2 at the compiler seam (no wire, no DB): the ONE List/Stream compiler resolves
    /// filter/sort/page onto the query model. Operators are the documented Bifrost set and become
    /// bound SQL parameters (never interpolated text); field/operator/sort names are validated
    /// against the identity-visible columns (unknown → INVALID_ARGUMENT); and every depth/count/size
    /// cap is enforced up front (invariant 6).
    /// </summary>
    public class GrpcReadRequestCompilerTests
    {
        private static readonly byte[] Secret = Encoding.UTF8.GetBytes("compiler-test-secret");
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

        [Fact]
        public void No_options_yields_no_filter_default_page_size_and_pk_ordering()
        {
            var compiled = Compile(Request());

            compiled.Query.Filter.Should().BeNull();
            compiled.Query.Limit.Should().Be(25); // clamp(defaultPageSize=25, maxPageSize=100)
            compiled.Query.Offset.Should().BeNull();
            // A total order even with no caller sort: the full primary key ascending.
            compiled.Query.Sort.Should().Equal("id_asc");
        }

        [Fact]
        public void Filter_becomes_a_bound_table_filter_value_never_interpolated_text()
        {
            var compiled = Compile(Request(filter: """{ "name": { "_eq": "alice" } }"""));

            // The literal is carried as a BOUND TableFilter value (emitted as a SQL parameter by the
            // shared machinery), keyed by the resolved column and operator — never spliced into text.
            var filter = compiled.Query.Filter;
            filter.Should().NotBeNull();
            filter!.ColumnName.Should().Be("name");
            filter.FilterType.Should().Be(FilterType.Join);
            filter.Next!.RelationName.Should().Be("_eq");
            filter.Next.Value.Should().Be("alice");
        }

        [Theory]
        [InlineData("_eq")]
        [InlineData("_neq")]
        [InlineData("_lt")]
        [InlineData("_lte")]
        [InlineData("_gt")]
        [InlineData("_gte")]
        public void Documented_comparison_operators_compile(string op)
        {
            var act = () => Compile(Request(filter: $$"""{ "age": { "{{op}}": 30 } }"""));
            act.Should().NotThrow();
        }

        [Fact]
        public void In_and_between_and_null_and_contains_compile()
        {
            Invoking(() => Compile(Request(filter: """{ "age": { "_in": [1,2,3] } }"""))).Should().NotThrow();
            Invoking(() => Compile(Request(filter: """{ "age": { "_between": [10,20] } }"""))).Should().NotThrow();
            Invoking(() => Compile(Request(filter: """{ "name": { "_null": true } }"""))).Should().NotThrow();
            Invoking(() => Compile(Request(filter: """{ "name": { "_contains": "li" } }"""))).Should().NotThrow();
        }

        [Fact]
        public void Composite_and_or_nesting_compiles()
        {
            var filter = """
            { "or": [
                { "name": { "_eq": "a" } },
                { "and": [ { "age": { "_gte": 18 } }, { "age": { "_lt": 65 } } ] }
            ] }
            """;
            Invoking(() => Compile(Request(filter: filter))).Should().NotThrow();
        }

        [Fact]
        public void Unknown_field_is_invalid_argument()
        {
            Invoking(() => Compile(Request(filter: """{ "nope": { "_eq": 1 } }""")))
                .Should().Throw<GrpcRequestException>()
                .Where(e => e.StatusCode == global::Grpc.Core.StatusCode.InvalidArgument);
        }

        [Fact]
        public void Unknown_operator_is_invalid_argument()
        {
            Invoking(() => Compile(Request(filter: """{ "age": { "_like": "x" } }""")))
                .Should().Throw<GrpcRequestException>()
                .Where(e => e.StatusCode == global::Grpc.Core.StatusCode.InvalidArgument);
        }

        [Fact]
        public void Unknown_sort_field_is_invalid_argument()
        {
            Invoking(() => Compile(Request(orderBy: "nope desc")))
                .Should().Throw<GrpcRequestException>()
                .Where(e => e.StatusCode == global::Grpc.Core.StatusCode.InvalidArgument);
        }

        [Fact]
        public void Sort_is_a_total_order_appending_the_full_composite_key()
        {
            var compiled = Compile(Request(orderBy: "qty desc"), CompositeTable());

            // caller key first, then the FULL primary key ascending as a deterministic tiebreak —
            // never an index-zero guess on the composite key.
            compiled.Query.Sort.Should().Equal("qty_desc", "order_id_asc", "line_no_asc");
        }

        [Fact]
        public void Out_of_range_number_for_an_integer_column_is_invalid_argument_not_overflow()
        {
            // 29 nines overflows Int64 — must be a clean INVALID_ARGUMENT (invariant 5), never a throw.
            Invoking(() => Compile(Request(filter: """{ "age": { "_eq": 99999999999999999999999999999 } }""")))
                .Should().Throw<GrpcRequestException>()
                .Where(e => e.StatusCode == global::Grpc.Core.StatusCode.InvalidArgument);
        }

        [Fact]
        public void Filter_nested_beyond_the_depth_cap_is_invalid_argument()
        {
            var sb = new StringBuilder();
            var open = 0;
            for (; open <= GrpcReadCaps.MaxFilterDepth + 2; open++)
                sb.Append("""{ "and": [ """);
            sb.Append("""{ "name": { "_eq": "x" } }""");
            for (var i = 0; i < open; i++)
                sb.Append(" ] }");

            Invoking(() => Compile(Request(filter: sb.ToString())))
                .Should().Throw<GrpcRequestException>()
                .Where(e => e.StatusCode == global::Grpc.Core.StatusCode.InvalidArgument);
        }

        [Fact]
        public void In_list_beyond_the_size_cap_is_invalid_argument()
        {
            var values = string.Join(",", Enumerable.Range(0, GrpcReadCaps.MaxInListSize + 5));
            Invoking(() => Compile(Request(filter: $$"""{ "age": { "_in": [{{values}}] } }""")))
                .Should().Throw<GrpcRequestException>()
                .Where(e => e.StatusCode == global::Grpc.Core.StatusCode.InvalidArgument);
        }

        [Fact]
        public void Oversized_page_token_is_invalid_argument_before_any_decode()
        {
            var token = new string('A', GrpcReadCaps.MaxCursorChars + 1);
            Invoking(() => Compile(Request(pageToken: token)))
                .Should().Throw<GrpcRequestException>()
                .Where(e => e.StatusCode == global::Grpc.Core.StatusCode.InvalidArgument);
        }

        [Fact]
        public void Malformed_filter_json_is_invalid_argument()
        {
            Invoking(() => Compile(Request(filter: "{ not json ")))
                .Should().Throw<GrpcRequestException>()
                .Where(e => e.StatusCode == global::Grpc.Core.StatusCode.InvalidArgument);
        }

        // ---- helpers ----

        private static Action Invoking(Action a) => a;

        private static GrpcCompiledRead Compile(
            IReadOnlyDictionary<string, object?> request, IDbTable? table = null)
        {
            table ??= SimpleTable();
            var visible = table.Columns.ToList();
            return GrpcReadRequestCompiler.Compile(
                table, visible, request,
                defaultPageSize: 25, maxPageSize: 100,
                identity: new Dictionary<string, object?> { ["user_id"] = "u1" },
                tokenSecret: Secret, now: DateTimeOffset.UtcNow, ttl: Ttl);
        }

        private static Dictionary<string, object?> Request(
            string? filter = null, string? orderBy = null, int? pageSize = null, string? pageToken = null)
        {
            var d = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (filter is not null) d["filter"] = filter;
            if (orderBy is not null) d["order_by"] = orderBy;
            if (pageSize is not null) d["page_size"] = pageSize;
            if (pageToken is not null) d["page_token"] = pageToken;
            return d;
        }

        private static IDbTable SimpleTable()
        {
            var id = new ColumnDto { ColumnName = "id", GraphQlName = "id", DataType = "int", IsPrimaryKey = true, OrdinalPosition = 1 };
            var name = new ColumnDto { ColumnName = "name", GraphQlName = "name", DataType = "varchar", OrdinalPosition = 2 };
            var age = new ColumnDto { ColumnName = "age", GraphQlName = "age", DataType = "int", OrdinalPosition = 3 };
            var table = Substitute.For<IDbTable>();
            table.GraphQlName.Returns("widgets");
            table.DbName.Returns("widgets");
            table.TableSchema.Returns("dbo");
            table.Columns.Returns(new[] { id, name, age });
            table.KeyColumns.Returns(new[] { id });
            return table;
        }

        private static IDbTable CompositeTable()
        {
            var orderId = new ColumnDto { ColumnName = "order_id", GraphQlName = "order_id", DataType = "int", IsPrimaryKey = true, OrdinalPosition = 1 };
            var lineNo = new ColumnDto { ColumnName = "line_no", GraphQlName = "line_no", DataType = "int", IsPrimaryKey = true, OrdinalPosition = 2 };
            var qty = new ColumnDto { ColumnName = "qty", GraphQlName = "qty", DataType = "int", OrdinalPosition = 3 };
            var table = Substitute.For<IDbTable>();
            table.GraphQlName.Returns("order_lines");
            table.DbName.Returns("order_lines");
            table.TableSchema.Returns("dbo");
            table.Columns.Returns(new[] { orderId, lineNo, qty });
            table.KeyColumns.Returns(new[] { orderId, lineNo });
            return table;
        }
    }
}
