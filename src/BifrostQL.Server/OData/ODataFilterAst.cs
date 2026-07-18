namespace BifrostQL.Server.OData
{
    /// <summary>
    /// The bounded, typed abstract syntax tree an OData <c>$filter</c> expression parses to before
    /// it is translated (schema-validated + coerced) into a parameterized
    /// <see cref="Core.QueryModel.TableFilter"/>. The tree is deliberately small: only the operators
    /// this slice supports have a node, so an unsupported OData construct (arithmetic, a lambda,
    /// an unlisted function) has no representation and is rejected at parse time rather than being
    /// partially interpreted (.claude/rules/protocol-adapter-security.md — deterministic 400, never
    /// a silent pass-through). No node carries SQL or GraphQL text; a literal is a raw, already
    /// unescaped value whose CLR type is only decided against the target column during translation,
    /// so a literal always becomes a bound parameter and never wire text.
    /// </summary>
    internal abstract record ODataFilterNode;

    /// <summary>A boolean combinator: <c>and</c> or <c>or</c> over two sub-expressions.</summary>
    internal sealed record BinaryNode(string BoolOp, ODataFilterNode Left, ODataFilterNode Right) : ODataFilterNode
    {
        public const string And = "and";
        public const string Or = "or";
    }

    /// <summary>Logical negation of a sub-expression; pushed into the leaves (De Morgan) at translation.</summary>
    internal sealed record NotNode(ODataFilterNode Operand) : ODataFilterNode;

    /// <summary>A binary comparison <c>&lt;property&gt; &lt;op&gt; &lt;literal&gt;</c>.</summary>
    internal sealed record ComparisonNode(string Property, ComparisonOp Op, ODataLiteral Value) : ODataFilterNode;

    /// <summary>A <c>&lt;property&gt; eq|ne null</c> test, kept distinct so it maps to IS [NOT] NULL.</summary>
    internal sealed record NullCompareNode(string Property, bool IsEqual) : ODataFilterNode;

    /// <summary>A <c>contains(&lt;property&gt;, &lt;literal&gt;)</c> substring test.</summary>
    internal sealed record ContainsNode(string Property, ODataLiteral Value) : ODataFilterNode;

    /// <summary>A <c>&lt;property&gt; in (&lt;literal&gt;, …)</c> membership test.</summary>
    internal sealed record InNode(string Property, IReadOnlyList<ODataLiteral> Values) : ODataFilterNode;

    /// <summary>The comparison operators this slice supports.</summary>
    internal enum ComparisonOp { Eq, Ne, Lt, Le, Gt, Ge }

    /// <summary>The syntactic kind of a parsed literal, before it is coerced to a column type.</summary>
    internal enum ODataLiteralKind { String, Number, Boolean, Null }

    /// <summary>
    /// A parsed literal value. For a <see cref="ODataLiteralKind.String"/> the <see cref="Text"/> is
    /// the fully unescaped content (OData doubles a single quote to escape it — <c>''</c> → <c>'</c>),
    /// so an injection payload inside a string literal is carried verbatim as data and never as
    /// syntax. For a number it is the raw digits (parsed to the CLR numeric only against the target
    /// column, so overflow collapses to a clean 400). A boolean is <c>"true"</c>/<c>"false"</c>.
    /// </summary>
    internal sealed record ODataLiteral(ODataLiteralKind Kind, string Text);
}
