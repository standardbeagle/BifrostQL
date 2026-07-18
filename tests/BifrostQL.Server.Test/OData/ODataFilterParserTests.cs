using BifrostQL.Server.OData;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.OData
{
    /// <summary>
    /// The bounded OData <c>$filter</c> parser in isolation: operator precedence and parentheses,
    /// OData <c>''</c> string escaping (an injection payload is carried as a literal VALUE, never as
    /// syntax), null semantics, rejection of every unsupported operator/function, and the resource
    /// bounds (nesting depth, token count, <c>in</c>-list size) that keep an adversarial filter a
    /// deterministic 400 instead of unbounded recursion/allocation.
    /// </summary>
    public sealed class ODataFilterParserTests
    {
        // ---- precedence + parentheses -------------------------------------------------------

        [Fact]
        public void And_binds_tighter_than_or()
        {
            // a eq 1 or b eq 2 and c eq 3  ==  (a eq 1) or ((b eq 2) and (c eq 3))
            var node = ODataFilterParser.Parse("a eq 1 or b eq 2 and c eq 3");

            var or = node.Should().BeOfType<BinaryNode>().Which;
            or.BoolOp.Should().Be(BinaryNode.Or);
            or.Left.Should().BeOfType<ComparisonNode>().Which.Property.Should().Be("a");
            or.Right.Should().BeOfType<BinaryNode>().Which.BoolOp.Should().Be(BinaryNode.And);
        }

        [Fact]
        public void Parentheses_override_precedence()
        {
            // (a eq 1 or b eq 2) and c eq 3  ==  root is AND, left is the parenthesized OR
            var node = ODataFilterParser.Parse("(a eq 1 or b eq 2) and c eq 3");

            var and = node.Should().BeOfType<BinaryNode>().Which;
            and.BoolOp.Should().Be(BinaryNode.And);
            and.Left.Should().BeOfType<BinaryNode>().Which.BoolOp.Should().Be(BinaryNode.Or);
            and.Right.Should().BeOfType<ComparisonNode>().Which.Property.Should().Be("c");
        }

        [Fact]
        public void Not_wraps_its_operand()
        {
            var node = ODataFilterParser.Parse("not (a eq 1)");
            node.Should().BeOfType<NotNode>()
                .Which.Operand.Should().BeOfType<ComparisonNode>();
        }

        [Theory]
        [InlineData("eq", "Eq")]
        [InlineData("ne", "Ne")]
        [InlineData("lt", "Lt")]
        [InlineData("le", "Le")]
        [InlineData("gt", "Gt")]
        [InlineData("ge", "Ge")]
        public void Parses_each_supported_comparison_operator(string op, string expected)
        {
            var node = ODataFilterParser.Parse($"total {op} 5");
            node.Should().BeOfType<ComparisonNode>().Which.Op.ToString().Should().Be(expected);
        }

        // ---- literals: escaping, injection, null, numbers, booleans -------------------------

        [Fact]
        public void String_literal_unescapes_doubled_quotes()
        {
            var node = ODataFilterParser.Parse("name eq 'O''Brien'");
            var literal = node.Should().BeOfType<ComparisonNode>().Which.Value;
            literal.Kind.Should().Be(ODataLiteralKind.String);
            literal.Text.Should().Be("O'Brien");
        }

        [Fact]
        public void Injection_payload_is_carried_as_a_literal_value_not_as_syntax()
        {
            // The classic ' OR '1'='1 payload, OData-escaped. The parser must treat the entire
            // unescaped run as ONE string value — never as operators/identifiers — so downstream it
            // can only ever be a bound parameter.
            var node = ODataFilterParser.Parse("name eq 'x'' OR ''1''=''1'");

            var literal = node.Should().BeOfType<ComparisonNode>().Which.Value;
            literal.Kind.Should().Be(ODataLiteralKind.String);
            literal.Text.Should().Be("x' OR '1'='1");
        }

        [Theory]
        [InlineData("true", "true")]
        [InlineData("false", "false")]
        public void Parses_boolean_literals(string text, string expected)
        {
            var node = ODataFilterParser.Parse($"flag eq {text}");
            var literal = node.Should().BeOfType<ComparisonNode>().Which.Value;
            literal.Kind.Should().Be(ODataLiteralKind.Boolean);
            literal.Text.Should().Be(expected);
        }

        [Fact]
        public void Eq_null_parses_as_a_null_test()
        {
            ODataFilterParser.Parse("note eq null").Should().BeOfType<NullCompareNode>()
                .Which.IsEqual.Should().BeTrue();
        }

        [Fact]
        public void Ne_null_parses_as_a_not_null_test()
        {
            ODataFilterParser.Parse("note ne null").Should().BeOfType<NullCompareNode>()
                .Which.IsEqual.Should().BeFalse();
        }

        [Fact]
        public void Null_with_an_ordering_operator_is_rejected()
        {
            var act = () => ODataFilterParser.Parse("note gt null");
            act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(400);
        }

        // ---- contains / in ------------------------------------------------------------------

        [Fact]
        public void Parses_contains_function()
        {
            var node = ODataFilterParser.Parse("contains(name, 'ab')");
            var contains = node.Should().BeOfType<ContainsNode>().Which;
            contains.Property.Should().Be("name");
            contains.Value.Text.Should().Be("ab");
        }

        [Fact]
        public void Parses_in_list()
        {
            var node = ODataFilterParser.Parse("id in (1, 2, 3)");
            var inList = node.Should().BeOfType<InNode>().Which;
            inList.Property.Should().Be("id");
            inList.Values.Select(v => v.Text).Should().Equal("1", "2", "3");
        }

        // ---- unsupported constructs → deterministic 400 -------------------------------------

        [Theory]
        [InlineData("startswith(name, 'a')")]   // unsupported function
        [InlineData("endswith(name, 'a')")]     // unsupported function
        [InlineData("length(name) gt 3")]       // unsupported function
        [InlineData("price add 1 gt 2")]        // arithmetic
        [InlineData("orders/any(o: o/id eq 1)")]// lambda
        [InlineData("id has 1")]                // unsupported operator word
        [InlineData("geo.distance(p, q) lt 1")] // geo
        public void Rejects_unsupported_functions_and_operators(string filter)
        {
            var act = () => ODataFilterParser.Parse(filter);
            act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(400);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("(")]
        [InlineData("a eq")]
        [InlineData("a eq 1 or")]
        [InlineData("name eq 'unterminated")]
        [InlineData("a eq 1 b eq 2")]           // trailing tokens, no combinator
        public void Rejects_malformed_expressions(string filter)
        {
            var act = () => ODataFilterParser.Parse(filter);
            act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(400);
        }

        // ---- resource bounds (invariant 6) --------------------------------------------------

        [Fact]
        public void Rejects_a_filter_nested_beyond_the_depth_cap_without_stack_overflow()
        {
            // Deeply nested parentheses: must be a deterministic 400 thrown BEFORE recursion blows
            // the stack — never an uncatchable StackOverflowException.
            var deep = new string('(', ODataFilterParser.MaxDepth + 5)
                       + "a eq 1"
                       + new string(')', ODataFilterParser.MaxDepth + 5);

            var act = () => ODataFilterParser.Parse(deep);
            act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(400);
        }

        [Fact]
        public void Rejects_deeply_nested_not_without_stack_overflow()
        {
            var deep = string.Concat(Enumerable.Repeat("not ", ODataFilterParser.MaxDepth + 5)) + "(a eq 1)";
            var act = () => ODataFilterParser.Parse(deep);
            act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(400);
        }

        [Fact]
        public void Rejects_a_filter_beyond_the_token_cap()
        {
            // A huge flat "a eq 1 and a eq 1 and …" chain: bounded by the token cap, not the stack.
            var clause = string.Join(" and ", Enumerable.Repeat("a eq 1", ODataFilterParser.MaxTokens));
            var act = () => ODataFilterParser.Parse(clause);
            act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(400);
        }

        [Fact]
        public void Rejects_an_in_list_beyond_the_element_cap()
        {
            var huge = "id in (" + string.Join(",", Enumerable.Range(0, ODataFilterParser.MaxInListSize + 1)) + ")";
            var act = () => ODataFilterParser.Parse(huge);
            act.Should().Throw<ODataProtocolException>().Which.HttpStatus.Should().Be(400);
        }
    }
}
