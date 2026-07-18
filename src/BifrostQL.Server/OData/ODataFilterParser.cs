using System.Text;

namespace BifrostQL.Server.OData
{
    /// <summary>
    /// A deliberately BOUNDED recursive-descent parser for the OData <c>$filter</c> subset this
    /// adapter supports: the comparison operators (<c>eq ne lt le gt ge</c>), boolean logic
    /// (<c>and or not</c>) with correct precedence and parentheses, <c>contains()</c>, <c>in</c>,
    /// and <c>eq</c>/<c>ne null</c>. Every other OData construct — arithmetic, <c>any</c>/<c>all</c>
    /// lambdas, geo functions, any unlisted function — has no grammar rule and is rejected as a
    /// deterministic 400, never partially interpreted.
    ///
    /// <para><b>Resource bounds (.claude/rules/protocol-adapter-security.md invariant 6).</b> The
    /// nesting depth is checked BEFORE each descent into a parenthesized group or a <c>not</c>, so a
    /// pathologically nested filter throws this adapter's own <see cref="ODataProtocolException"/>
    /// (already in the middleware's caught base) instead of recursing into an uncatchable
    /// <see cref="StackOverflowException"/> that would take the whole host process down. The token
    /// count is capped during tokenization (bounding a huge flat <c>a and a and …</c>), and an
    /// <c>in</c> list is grown incrementally with its own element cap — never pre-allocated from an
    /// attacker-controlled count.</para>
    ///
    /// <para>Numeric/date/guid literal <em>values</em> are not parsed here; the parser only records
    /// a literal's raw text and syntactic kind, so decoding to a CLR value (and its parse-exception
    /// family, invariant 5) happens once against the known target column type in
    /// <see cref="ODataFilterTranslator"/>.</para>
    /// </summary>
    internal static class ODataFilterParser
    {
        /// <summary>Maximum nesting depth of parentheses / <c>not</c> (RESP slice-1 used the same 32).</summary>
        public const int MaxDepth = 32;

        /// <summary>Maximum number of tokens in a single <c>$filter</c> expression.</summary>
        public const int MaxTokens = 500;

        /// <summary>Maximum number of elements in a single <c>in (…)</c> list.</summary>
        public const int MaxInListSize = 200;

        public static ODataFilterNode Parse(string filter)
        {
            if (filter is null) throw new ArgumentNullException(nameof(filter));

            var tokens = Tokenize(filter);
            if (tokens.Count == 0)
                throw ODataProtocolException.BadRequest("$filter is empty.");

            var parser = new Cursor(tokens);
            var node = parser.ParseOr(depth: 0);
            if (!parser.AtEnd)
                throw ODataProtocolException.BadRequest(
                    $"$filter has an unexpected trailing token '{parser.Current.Text}'.");
            return node;
        }

        // ---- tokenizer ----------------------------------------------------------------------

        private enum TokKind { Word, String, Number, LParen, RParen, Comma }

        private readonly record struct Token(TokKind Kind, string Text);

        /// <summary>
        /// Splits the raw <c>$filter</c> text into tokens, enforcing the token-count cap as it goes
        /// so an oversized expression is a clean 400 before any parsing/allocation. String literals
        /// honor OData's <c>''</c> escaping and must be terminated; an unterminated quote is a 400.
        /// </summary>
        private static List<Token> Tokenize(string text)
        {
            var tokens = new List<Token>();
            var i = 0;
            var n = text.Length;

            void Add(Token t)
            {
                if (tokens.Count >= MaxTokens)
                    throw ODataProtocolException.BadRequest(
                        $"$filter is too complex (more than {MaxTokens} tokens).");
                tokens.Add(t);
            }

            while (i < n)
            {
                var c = text[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }

                switch (c)
                {
                    case '(': Add(new Token(TokKind.LParen, "(")); i++; continue;
                    case ')': Add(new Token(TokKind.RParen, ")")); i++; continue;
                    case ',': Add(new Token(TokKind.Comma, ",")); i++; continue;
                    case '\'':
                        {
                            var (value, next) = ReadString(text, i);
                            Add(new Token(TokKind.String, value));
                            i = next;
                            continue;
                        }
                }

                if (c == '-' || char.IsDigit(c))
                {
                    var (value, next) = ReadNumber(text, i);
                    Add(new Token(TokKind.Number, value));
                    i = next;
                    continue;
                }

                if (IsWordStart(c))
                {
                    var (value, next) = ReadWord(text, i);
                    Add(new Token(TokKind.Word, value));
                    i = next;
                    continue;
                }

                throw ODataProtocolException.BadRequest($"$filter has an unexpected character '{c}'.");
            }

            return tokens;
        }

        // OData escapes a single quote inside a string literal by doubling it: 'O''Brien'.
        private static (string Value, int Next) ReadString(string text, int start)
        {
            var sb = new StringBuilder();
            var i = start + 1; // skip opening quote
            var n = text.Length;
            while (i < n)
            {
                var c = text[i];
                if (c == '\'')
                {
                    if (i + 1 < n && text[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                    return (sb.ToString(), i + 1); // closing quote
                }
                sb.Append(c);
                i++;
            }
            throw ODataProtocolException.BadRequest("$filter has an unterminated string literal.");
        }

        private static (string Value, int Next) ReadNumber(string text, int start)
        {
            var i = start;
            var n = text.Length;
            if (text[i] == '-') i++;
            var sawDigit = false;
            while (i < n && (char.IsDigit(text[i]) || text[i] is '.' or 'e' or 'E' or '+' or '-'))
            {
                if (char.IsDigit(text[i])) sawDigit = true;
                i++;
            }
            if (!sawDigit)
                throw ODataProtocolException.BadRequest("$filter has a malformed numeric literal.");
            return (text.Substring(start, i - start), i);
        }

        private static bool IsWordStart(char c) => char.IsLetter(c) || c == '_';
        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.';

        private static (string Value, int Next) ReadWord(string text, int start)
        {
            var i = start;
            while (i < text.Length && IsWordChar(text[i])) i++;
            return (text.Substring(start, i - start), i);
        }

        // ---- recursive-descent parser -------------------------------------------------------

        private sealed class Cursor
        {
            private readonly List<Token> _tokens;
            private int _pos;

            public Cursor(List<Token> tokens) => _tokens = tokens;

            public bool AtEnd => _pos >= _tokens.Count;
            public Token Current => _tokens[_pos];

            private bool PeekWord(string word)
                => !AtEnd && Current.Kind == TokKind.Word
                   && string.Equals(Current.Text, word, StringComparison.OrdinalIgnoreCase);

            private Token Next() => _tokens[_pos++];

            private void Expect(TokKind kind, string what)
            {
                if (AtEnd || Current.Kind != kind)
                    throw ODataProtocolException.BadRequest($"$filter expected {what}.");
                _pos++;
            }

            private static void CheckDepth(int depth)
            {
                if (depth > MaxDepth)
                    throw ODataProtocolException.BadRequest(
                        $"$filter is nested too deeply (more than {MaxDepth} levels).");
            }

            // orExpr := andExpr ('or' andExpr)*   — lowest precedence
            public ODataFilterNode ParseOr(int depth)
            {
                CheckDepth(depth);
                var left = ParseAnd(depth);
                while (PeekWord("or"))
                {
                    Next();
                    var right = ParseAnd(depth);
                    left = new BinaryNode(BinaryNode.Or, left, right);
                }
                return left;
            }

            // andExpr := unary ('and' unary)*   — binds tighter than 'or'
            private ODataFilterNode ParseAnd(int depth)
            {
                var left = ParseUnary(depth);
                while (PeekWord("and"))
                {
                    Next();
                    var right = ParseUnary(depth);
                    left = new BinaryNode(BinaryNode.And, left, right);
                }
                return left;
            }

            // unary := 'not' unary | primary
            private ODataFilterNode ParseUnary(int depth)
            {
                if (PeekWord("not"))
                {
                    Next();
                    // 'not' recurses; bound its depth so `not not not …` cannot overflow the stack.
                    CheckDepth(depth + 1);
                    return new NotNode(ParseUnary(depth + 1));
                }
                return ParsePrimary(depth);
            }

            // primary := '(' orExpr ')' | contains(...) | comparison | in
            private ODataFilterNode ParsePrimary(int depth)
            {
                if (AtEnd)
                    throw ODataProtocolException.BadRequest("$filter ended unexpectedly.");

                if (Current.Kind == TokKind.LParen)
                {
                    Next();
                    // Parenthesized group re-enters the top of the grammar; bound BEFORE descending.
                    var inner = ParseOr(depth + 1);
                    Expect(TokKind.RParen, "')'");
                    return inner;
                }

                if (Current.Kind != TokKind.Word)
                    throw ODataProtocolException.BadRequest(
                        $"$filter expected a property name or '(' but found '{Current.Text}'.");

                // A word directly followed by '(' is a function call. Only contains() is supported;
                // any other function (startswith, arithmetic-wrapping, any/all, geo, …) is rejected.
                if (_pos + 1 < _tokens.Count && _tokens[_pos + 1].Kind == TokKind.LParen)
                    return ParseFunction(depth);

                return ParseComparisonOrIn();
            }

            private ODataFilterNode ParseFunction(int depth)
            {
                var name = Next().Text;
                if (!string.Equals(name, "contains", StringComparison.OrdinalIgnoreCase))
                    throw ODataProtocolException.BadRequest(
                        $"$filter uses the unsupported function '{name}'.");

                Expect(TokKind.LParen, "'(' after contains");
                var property = ExpectProperty();
                Expect(TokKind.Comma, "',' in contains(property, value)");
                var literal = ExpectLiteral();
                Expect(TokKind.RParen, "')' to close contains");
                return new ContainsNode(property, literal);
            }

            private ODataFilterNode ParseComparisonOrIn()
            {
                var property = Next().Text; // Word, verified by caller

                if (PeekWord("in"))
                {
                    Next();
                    return ParseInList(property);
                }

                var op = ParseComparisonOp();

                // `<prop> eq|ne null` is a null test; a null with an ordering operator is invalid.
                if (PeekWord("null"))
                {
                    Next();
                    return op switch
                    {
                        ComparisonOp.Eq => new NullCompareNode(property, IsEqual: true),
                        ComparisonOp.Ne => new NullCompareNode(property, IsEqual: false),
                        _ => throw ODataProtocolException.BadRequest(
                            "$filter allows null only with 'eq' or 'ne'."),
                    };
                }

                return new ComparisonNode(property, op, ExpectLiteral());
            }

            private InNode ParseInList(string property)
            {
                Expect(TokKind.LParen, "'(' after 'in'");
                var values = new List<ODataLiteral>(); // grown incrementally, never pre-sized
                if (!AtEnd && Current.Kind == TokKind.RParen)
                {
                    Next();
                    return new InNode(property, values);
                }
                while (true)
                {
                    if (values.Count >= MaxInListSize)
                        throw ODataProtocolException.BadRequest(
                            $"$filter 'in' list is too large (more than {MaxInListSize} items).");
                    values.Add(ExpectLiteral());
                    if (AtEnd)
                        throw ODataProtocolException.BadRequest("$filter 'in' list is not closed.");
                    if (Current.Kind == TokKind.Comma) { Next(); continue; }
                    Expect(TokKind.RParen, "')' to close 'in' list");
                    break;
                }
                return new InNode(property, values);
            }

            private ComparisonOp ParseComparisonOp()
            {
                if (AtEnd || Current.Kind != TokKind.Word)
                    throw ODataProtocolException.BadRequest("$filter expected a comparison operator.");
                var op = Next().Text.ToLowerInvariant();
                return op switch
                {
                    "eq" => ComparisonOp.Eq,
                    "ne" => ComparisonOp.Ne,
                    "lt" => ComparisonOp.Lt,
                    "le" => ComparisonOp.Le,
                    "gt" => ComparisonOp.Gt,
                    "ge" => ComparisonOp.Ge,
                    _ => throw ODataProtocolException.BadRequest(
                        $"$filter uses the unsupported operator '{op}'."),
                };
            }

            private string ExpectProperty()
            {
                if (AtEnd || Current.Kind != TokKind.Word)
                    throw ODataProtocolException.BadRequest("$filter expected a property name.");
                return Next().Text;
            }

            private ODataLiteral ExpectLiteral()
            {
                if (AtEnd)
                    throw ODataProtocolException.BadRequest("$filter expected a literal value.");
                var t = Current;
                switch (t.Kind)
                {
                    case TokKind.String: Next(); return new ODataLiteral(ODataLiteralKind.String, t.Text);
                    case TokKind.Number: Next(); return new ODataLiteral(ODataLiteralKind.Number, t.Text);
                    case TokKind.Word when string.Equals(t.Text, "true", StringComparison.OrdinalIgnoreCase):
                        Next(); return new ODataLiteral(ODataLiteralKind.Boolean, "true");
                    case TokKind.Word when string.Equals(t.Text, "false", StringComparison.OrdinalIgnoreCase):
                        Next(); return new ODataLiteral(ODataLiteralKind.Boolean, "false");
                    case TokKind.Word when string.Equals(t.Text, "null", StringComparison.OrdinalIgnoreCase):
                        Next(); return new ODataLiteral(ODataLiteralKind.Null, "");
                    default:
                        throw ODataProtocolException.BadRequest(
                            $"$filter expected a literal value but found '{t.Text}'.");
                }
            }
        }
    }
}
