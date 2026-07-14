using System.Globalization;
using System.Text;

namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// A validated, model-agnostic parse of the SAFE SQL subset the pgwire read
    /// path accepts: a single-table <c>SELECT</c> with an optional single
    /// schema-relationship <c>JOIN</c>, <c>WHERE</c>, <c>ORDER BY</c>,
    /// <c>LIMIT</c>, and <c>OFFSET</c>. It is an allowlist grammar: the parser
    /// recognizes ONLY these shapes and raises <see cref="PgQueryTranslationException"/>
    /// for everything else (functions, subqueries, GROUP BY/HAVING/UNION/CTEs,
    /// writes, a second statement after <c>;</c>). The parser never rebuilds or
    /// forwards the caller's SQL text — it emits this typed AST, which the
    /// translator maps onto a programmatic <c>GqlObjectQuery</c> with every literal
    /// bound as a parameter.
    /// </summary>
    internal sealed class PgSelectStatement
    {
        public bool IsSelectStar { get; init; }
        public IReadOnlyList<PgColumnRef> Columns { get; init; } = Array.Empty<PgColumnRef>();
        public required PgTableRef From { get; init; }
        public PgJoinClause? Join { get; init; }
        public PgBoolExpr? Where { get; init; }
        public IReadOnlyList<PgOrderTerm> OrderBy { get; init; } = Array.Empty<PgOrderTerm>();
        public int? Limit { get; init; }
        public int? Offset { get; init; }
    }

    /// <summary>A <c>schema.table</c> (or bare <c>table</c>) reference with an optional alias.</summary>
    internal sealed record PgTableRef(string? Schema, string Name, string? Alias);

    /// <summary>A <c>[qualifier.]column</c> reference. Qualifier is a table name or alias.</summary>
    internal sealed record PgColumnRef(string? Qualifier, string Name);

    /// <summary>A single INNER JOIN with an equality ON clause (<c>a.x = b.y</c>).</summary>
    internal sealed record PgJoinClause(PgTableRef Table, PgColumnRef Left, PgColumnRef Right);

    internal sealed record PgOrderTerm(PgColumnRef Column, bool Descending);

    /// <summary>Boolean WHERE tree: an AND/OR combination or a leaf predicate.</summary>
    internal abstract class PgBoolExpr { }

    internal sealed class PgBoolCombine : PgBoolExpr
    {
        public required bool IsAnd { get; init; }
        public required IReadOnlyList<PgBoolExpr> Terms { get; init; }
    }

    internal enum PgCompareOp { Eq, Neq, Lt, Lte, Gt, Gte, Like, In, Between, IsNull, IsNotNull }

    internal sealed class PgPredicate : PgBoolExpr
    {
        public required PgColumnRef Column { get; init; }
        public required PgCompareOp Op { get; init; }
        /// <summary>Single literal for comparison/LIKE; unused for IN/BETWEEN/IS NULL.</summary>
        public object? Value { get; init; }
        /// <summary>Value list for IN; the [low, high] pair for BETWEEN.</summary>
        public IReadOnlyList<object?>? Values { get; init; }
    }

    /// <summary>
    /// Recursive-descent parser + tokenizer for the pgwire SQL subset. Hand-rolled
    /// (not ScriptDom): ScriptDom is a T-SQL parser and a test-only dependency; a
    /// focused allowlist parser keeps the shipped Server package dependency-free and
    /// is safer for a deliberately narrow SAFE subset — it recognizes only the
    /// grammar below and rejects everything else, rather than parsing a large surface
    /// then trying to reject the dangerous parts.
    /// </summary>
    internal static class PgSqlSubsetParser
    {
        public static PgSelectStatement Parse(string sql)
        {
            var tokens = Tokenize(sql ?? "");
            var parser = new Cursor(tokens);
            var stmt = parser.ParseSelect();
            parser.ExpectEnd();
            return stmt;
        }

        // ---- tokenizer -------------------------------------------------------

        private enum TokKind { Word, Number, String, Symbol, End }

        private readonly record struct Tok(TokKind Kind, string Text, object? Literal);

        private static List<Tok> Tokenize(string sql)
        {
            var tokens = new List<Tok>();
            var i = 0;
            var n = sql.Length;
            while (i < n)
            {
                var c = sql[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }

                // Reject SQL comments outright — they are a classic smuggling vector
                // and have no place in the subset.
                if ((c == '-' && i + 1 < n && sql[i + 1] == '-') ||
                    (c == '/' && i + 1 < n && sql[i + 1] == '*'))
                    throw Reject("SQL comments are not supported.");

                if (c == '\'')
                {
                    tokens.Add(ReadString(sql, ref i));
                    continue;
                }
                if (c == '"')
                {
                    tokens.Add(ReadQuotedIdent(sql, ref i));
                    continue;
                }
                if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(sql[i + 1])))
                {
                    tokens.Add(ReadNumber(sql, ref i));
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    var start = i;
                    while (i < n && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) i++;
                    tokens.Add(new Tok(TokKind.Word, sql[start..i], null));
                    continue;
                }
                // Multi-char comparison symbols first.
                if (c == '<' && i + 1 < n && (sql[i + 1] == '=' || sql[i + 1] == '>')) { tokens.Add(new Tok(TokKind.Symbol, sql.Substring(i, 2), null)); i += 2; continue; }
                if (c == '>' && i + 1 < n && sql[i + 1] == '=') { tokens.Add(new Tok(TokKind.Symbol, ">=", null)); i += 2; continue; }
                if (c == '!' && i + 1 < n && sql[i + 1] == '=') { tokens.Add(new Tok(TokKind.Symbol, "!=", null)); i += 2; continue; }
                if ("=<>().,;*".IndexOf(c) >= 0) { tokens.Add(new Tok(TokKind.Symbol, c.ToString(), null)); i++; continue; }

                throw Reject($"unexpected character '{c}'.");
            }
            tokens.Add(new Tok(TokKind.End, "", null));
            return tokens;
        }

        private static Tok ReadString(string sql, ref int i)
        {
            var sb = new StringBuilder();
            i++; // opening quote
            var n = sql.Length;
            while (i < n)
            {
                var c = sql[i];
                if (c == '\'')
                {
                    if (i + 1 < n && sql[i + 1] == '\'') { sb.Append('\''); i += 2; continue; } // escaped ''
                    i++; // closing quote
                    return new Tok(TokKind.String, sb.ToString(), sb.ToString());
                }
                sb.Append(c);
                i++;
            }
            throw Reject("unterminated string literal.");
        }

        private static Tok ReadQuotedIdent(string sql, ref int i)
        {
            var sb = new StringBuilder();
            i++; // opening quote
            var n = sql.Length;
            while (i < n)
            {
                var c = sql[i];
                if (c == '"')
                {
                    if (i + 1 < n && sql[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                    i++;
                    return new Tok(TokKind.Word, sb.ToString(), "quoted");
                }
                sb.Append(c);
                i++;
            }
            throw Reject("unterminated quoted identifier.");
        }

        private static Tok ReadNumber(string sql, ref int i)
        {
            var start = i;
            var n = sql.Length;
            var seenDot = false;
            while (i < n && (char.IsDigit(sql[i]) || (sql[i] == '.' && !seenDot)))
            {
                if (sql[i] == '.') seenDot = true;
                i++;
            }
            var text = sql[start..i];
            object literal = seenDot
                ? decimal.Parse(text, CultureInfo.InvariantCulture)
                : long.Parse(text, CultureInfo.InvariantCulture);
            return new Tok(TokKind.Number, text, literal);
        }

        // ---- parser ----------------------------------------------------------

        private sealed class Cursor
        {
            private readonly List<Tok> _t;
            private int _p;

            public Cursor(List<Tok> tokens) { _t = tokens; }

            private Tok Peek => _t[_p];
            private Tok Next() => _t[_p++];
            private bool IsEnd => Peek.Kind == TokKind.End;

            private bool IsWord(string kw) =>
                Peek.Kind == TokKind.Word && Peek.Literal is not "quoted"
                && string.Equals(Peek.Text, kw, StringComparison.OrdinalIgnoreCase);

            private bool TryWord(string kw) { if (IsWord(kw)) { _p++; return true; } return false; }

            private void ExpectWord(string kw)
            {
                if (!TryWord(kw)) throw Reject($"expected '{kw}'.");
            }

            private bool IsSymbol(string s) => Peek.Kind == TokKind.Symbol && Peek.Text == s;
            private bool TrySymbol(string s) { if (IsSymbol(s)) { _p++; return true; } return false; }
            private void ExpectSymbol(string s) { if (!TrySymbol(s)) throw Reject($"expected '{s}'."); }

            public void ExpectEnd()
            {
                if (TrySymbol(";"))
                {
                    // A single trailing ';' terminates the one statement; anything
                    // after it is a second statement — reject (no statement batching).
                    if (!IsEnd) throw Reject("only a single statement is allowed.");
                    return;
                }
                if (!IsEnd) throw Reject("unexpected trailing tokens after the statement.");
            }

            public PgSelectStatement ParseSelect()
            {
                if (!TryWord("SELECT"))
                    throw Reject("only SELECT statements are supported.");

                // Guard the out-of-subset set-quantifiers up front for a clear message.
                if (IsWord("DISTINCT") || IsWord("ALL"))
                    throw RejectFeature("SELECT DISTINCT/ALL is not supported.");

                bool star;
                var columns = new List<PgColumnRef>();
                if (TrySymbol("*"))
                {
                    star = true;
                }
                else
                {
                    star = false;
                    columns.Add(ParseColumnRef());
                    while (TrySymbol(",")) columns.Add(ParseColumnRef());
                }

                ExpectWord("FROM");
                var from = ParseTableRef();

                PgJoinClause? join = null;
                if (IsWord("JOIN") || IsWord("INNER") || IsWord("LEFT") || IsWord("RIGHT") || IsWord("FULL") || IsWord("CROSS"))
                    join = ParseJoin();

                PgBoolExpr? where = null;
                if (TryWord("WHERE")) where = ParseOr();

                if (IsWord("GROUP")) throw RejectFeature("GROUP BY is not supported.");
                if (IsWord("HAVING")) throw RejectFeature("HAVING is not supported.");
                if (IsWord("UNION") || IsWord("INTERSECT") || IsWord("EXCEPT"))
                    throw RejectFeature("set operations (UNION/INTERSECT/EXCEPT) are not supported.");

                var orderBy = new List<PgOrderTerm>();
                if (TryWord("ORDER"))
                {
                    ExpectWord("BY");
                    orderBy.Add(ParseOrderTerm());
                    while (TrySymbol(",")) orderBy.Add(ParseOrderTerm());
                }

                int? limit = null, offset = null;
                // LIMIT and OFFSET may appear in either order.
                for (var guard = 0; guard < 2; guard++)
                {
                    if (TryWord("LIMIT")) { limit = ParseNonNegInt("LIMIT"); continue; }
                    if (TryWord("OFFSET")) { offset = ParseNonNegInt("OFFSET"); continue; }
                    break;
                }

                return new PgSelectStatement
                {
                    IsSelectStar = star,
                    Columns = columns,
                    From = from,
                    Join = join,
                    Where = where,
                    OrderBy = orderBy,
                    Limit = limit,
                    Offset = offset,
                };
            }

            private PgJoinClause ParseJoin()
            {
                // Only a plain INNER JOIN maps to a schema relationship in this MVP.
                if (TryWord("INNER")) { /* optional keyword */ }
                else if (IsWord("LEFT") || IsWord("RIGHT") || IsWord("FULL") || IsWord("CROSS"))
                    throw RejectFeature("only INNER JOIN is supported.");
                ExpectWord("JOIN");

                var table = ParseTableRef();
                ExpectWord("ON");
                var left = ParseColumnRef();
                if (!TrySymbol("="))
                    throw RejectFeature("JOIN ON supports a single equality condition only.");
                var right = ParseColumnRef();
                // A compound ON (AND/OR) is out of the single-FK MVP subset.
                if (IsWord("AND") || IsWord("OR"))
                    throw RejectFeature("JOIN ON supports a single equality condition only.");
                return new PgJoinClause(table, left, right);
            }

            private int ParseNonNegInt(string clause)
            {
                if (Peek.Kind != TokKind.Number || Peek.Literal is not long v)
                    throw Reject($"{clause} requires an integer.");
                _p++;
                if (v < 0 || v > int.MaxValue) throw Reject($"{clause} value out of range.");
                return (int)v;
            }

            private PgTableRef ParseTableRef()
            {
                var first = ExpectIdentifier("table name");
                string? schema = null;
                var name = first;
                if (TrySymbol("."))
                {
                    schema = first;
                    name = ExpectIdentifier("table name");
                }
                // Optional alias: `t` or `AS t`.
                string? alias = null;
                if (TryWord("AS")) alias = ExpectIdentifier("alias");
                else if (IsAliasCandidate()) alias = ExpectIdentifier("alias");
                return new PgTableRef(schema, name, alias);
            }

            private PgOrderTerm ParseOrderTerm()
            {
                var col = ParseColumnRef();
                var desc = false;
                if (TryWord("ASC")) desc = false;
                else if (TryWord("DESC")) desc = true;
                return new PgOrderTerm(col, desc);
            }

            private PgColumnRef ParseColumnRef()
            {
                var first = ExpectIdentifier("column name");
                // A '(' after an identifier is a function call — out of subset.
                if (IsSymbol("("))
                    throw RejectFeature($"function calls are not supported ('{first}').");
                if (TrySymbol("."))
                {
                    if (TrySymbol("*"))
                        throw RejectFeature("qualified '*' is not supported.");
                    var col = ExpectIdentifier("column name");
                    if (IsSymbol("("))
                        throw RejectFeature("function calls are not supported.");
                    return new PgColumnRef(first, col);
                }
                return new PgColumnRef(null, first);
            }

            // WHERE grammar: OR of ANDs of (parenthesized-expr | predicate).
            private PgBoolExpr ParseOr()
            {
                var terms = new List<PgBoolExpr> { ParseAnd() };
                while (TryWord("OR")) terms.Add(ParseAnd());
                return terms.Count == 1 ? terms[0] : new PgBoolCombine { IsAnd = false, Terms = terms };
            }

            private PgBoolExpr ParseAnd()
            {
                var terms = new List<PgBoolExpr> { ParseFactor() };
                while (TryWord("AND")) terms.Add(ParseFactor());
                return terms.Count == 1 ? terms[0] : new PgBoolCombine { IsAnd = true, Terms = terms };
            }

            private PgBoolExpr ParseFactor()
            {
                if (TrySymbol("("))
                {
                    var inner = ParseOr();
                    ExpectSymbol(")");
                    return inner;
                }
                if (TryWord("NOT"))
                    throw RejectFeature("NOT is only supported as part of 'IS NOT NULL'.");
                return ParsePredicate();
            }

            private PgPredicate ParsePredicate()
            {
                var col = ParseColumnRef();

                if (TryWord("IS"))
                {
                    var not = TryWord("NOT");
                    ExpectWord("NULL");
                    return new PgPredicate { Column = col, Op = not ? PgCompareOp.IsNotNull : PgCompareOp.IsNull };
                }

                if (TryWord("IN"))
                {
                    ExpectSymbol("(");
                    if (IsWord("SELECT")) throw RejectFeature("subqueries are not supported.");
                    var values = new List<object?> { ParseLiteral() };
                    while (TrySymbol(",")) values.Add(ParseLiteral());
                    ExpectSymbol(")");
                    return new PgPredicate { Column = col, Op = PgCompareOp.In, Values = values };
                }

                if (TryWord("BETWEEN"))
                {
                    var lo = ParseLiteral();
                    ExpectWord("AND");
                    var hi = ParseLiteral();
                    return new PgPredicate { Column = col, Op = PgCompareOp.Between, Values = new[] { lo, hi } };
                }

                if (TryWord("LIKE"))
                    return new PgPredicate { Column = col, Op = PgCompareOp.Like, Value = ParseLiteral() };

                // Comparison operators.
                var op = Peek.Kind == TokKind.Symbol
                    ? Peek.Text switch
                    {
                        "=" => PgCompareOp.Eq,
                        "<>" => PgCompareOp.Neq,
                        "!=" => PgCompareOp.Neq,
                        "<" => PgCompareOp.Lt,
                        "<=" => PgCompareOp.Lte,
                        ">" => PgCompareOp.Gt,
                        ">=" => PgCompareOp.Gte,
                        _ => throw Reject($"unexpected operator '{Peek.Text}'."),
                    }
                    : throw Reject("expected a comparison operator in WHERE.");
                _p++;
                var value = ParseLiteral();
                return new PgPredicate { Column = col, Op = op, Value = value };
            }

            private object? ParseLiteral()
            {
                var t = Peek;
                if (t.Kind == TokKind.Number) { _p++; return t.Literal; }
                if (t.Kind == TokKind.String) { _p++; return t.Literal; }
                if (t.Kind == TokKind.Word && t.Literal is not "quoted")
                {
                    if (string.Equals(t.Text, "TRUE", StringComparison.OrdinalIgnoreCase)) { _p++; return true; }
                    if (string.Equals(t.Text, "FALSE", StringComparison.OrdinalIgnoreCase)) { _p++; return false; }
                    if (string.Equals(t.Text, "NULL", StringComparison.OrdinalIgnoreCase)) { _p++; return null; }
                    if (string.Equals(t.Text, "SELECT", StringComparison.OrdinalIgnoreCase))
                        throw RejectFeature("subqueries are not supported.");
                }
                throw Reject("expected a literal value (number, quoted string, TRUE/FALSE/NULL).");
            }

            private string ExpectIdentifier(string what)
            {
                if (Peek.Kind != TokKind.Word)
                    throw Reject($"expected {what}.");
                // Bare (unquoted) reserved words cannot be identifiers.
                if (Peek.Literal is not "quoted" && Reserved.Contains(Peek.Text))
                    throw Reject($"expected {what} but found reserved word '{Peek.Text}'.");
                return Next().Text;
            }

            // Heuristic: a bare word that follows a table ref and is not a clause
            // keyword is a table alias.
            private bool IsAliasCandidate()
                => Peek.Kind == TokKind.Word && (Peek.Literal is "quoted" || !ClauseKeywords.Contains(Peek.Text));
        }

        private static readonly HashSet<string> ClauseKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "WHERE", "ORDER", "GROUP", "HAVING", "LIMIT", "OFFSET", "JOIN", "INNER",
            "LEFT", "RIGHT", "FULL", "CROSS", "ON", "UNION", "INTERSECT", "EXCEPT", "AS",
        };

        // Reserved words that may never be used as bare identifiers.
        private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "ORDER", "BY", "GROUP",
            "HAVING", "LIMIT", "OFFSET", "JOIN", "INNER", "LEFT", "RIGHT", "FULL",
            "CROSS", "ON", "IN", "IS", "NULL", "BETWEEN", "LIKE", "UNION", "INTERSECT",
            "EXCEPT", "AS", "DISTINCT", "ALL",
        };

        private static PgQueryTranslationException Reject(string detail) =>
            new($"pgwire: unsupported SQL — {detail}");

        private static PgQueryTranslationException RejectFeature(string detail) =>
            new($"pgwire: unsupported SQL — {detail}", PgWireProtocol.SqlStateFeatureNotSupported);
    }
}
