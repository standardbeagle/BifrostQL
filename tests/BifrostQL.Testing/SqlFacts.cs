using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit.Sdk;

namespace BifrostQL.Testing;

/// <summary>
/// Semantic facts extracted from generated T-SQL by walking the ScriptDom AST —
/// referenced columns/tables, presence of WHERE/ORDER BY, the OFFSET/FETCH values,
/// and parameter count. Lets tests assert <em>meaning</em> (the limit really is N,
/// only known columns are referenced, the filter column appears) rather than
/// substring-matching the SQL text. SQL Server only; the builder's other dialects
/// are syntax-checked via <see cref="SqlSyntax"/>.
/// </summary>
public sealed class SqlFacts
{
    public IReadOnlyCollection<string> Columns { get; }
    public IReadOnlyCollection<string> Tables { get; }
    public bool HasWhere { get; }
    public bool HasOrderBy { get; }
    public int? Fetch { get; }
    public int? Offset { get; }
    public int ParameterCount { get; }

    private SqlFacts(Visitor v)
    {
        Columns = v.Columns;
        Tables = v.Tables;
        HasWhere = v.HasWhere;
        HasOrderBy = v.HasOrderBy;
        Fetch = v.Fetch;
        Offset = v.Offset;
        ParameterCount = v.Params;
    }

    /// <summary>Synthetic identifiers the builder introduces (join keys, window
    /// columns, paging aliases) that have no model column behind them.</summary>
    public static readonly IReadOnlySet<string> SyntheticColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "JoinId", "src_id", "srcId", "joinId", "RowNumber", "Total", "total",
    };

    public static SqlFacts Parse(string sql)
    {
        using var reader = new StringReader(sql);
        var fragment = new TSql160Parser(initialQuotedIdentifiers: true).Parse(reader, out var errors);
        if (errors.Count > 0)
            throw new XunitException(
                $"SQL did not parse: {string.Join("; ", errors.Select(e => e.Message))}\nSQL: {sql}");
        var visitor = new Visitor();
        fragment.Accept(visitor);
        return new SqlFacts(visitor);
    }

    private sealed class Visitor : TSqlFragmentVisitor
    {
        public readonly HashSet<string> Columns = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Tables = new(StringComparer.OrdinalIgnoreCase);
        public bool HasWhere;
        public bool HasOrderBy;
        public int? Fetch;
        public int? Offset;
        public int Params;

        public override void Visit(ColumnReferenceExpression node)
        {
            var id = node.MultiPartIdentifier?.Identifiers?.LastOrDefault();
            if (id is not null && id.Value != "*")
                Columns.Add(id.Value);
        }

        public override void Visit(NamedTableReference node)
        {
            var id = node.SchemaObject?.BaseIdentifier;
            if (id is not null)
                Tables.Add(id.Value);
        }

        public override void Visit(WhereClause node) => HasWhere = true;
        public override void Visit(OrderByClause node) => HasOrderBy = true;
        public override void Visit(VariableReference node) => Params++;

        public override void Visit(OffsetClause node)
        {
            if (node.OffsetExpression is IntegerLiteral o && int.TryParse(o.Value, out var ov))
                Offset = ov;
            if (node.FetchExpression is IntegerLiteral f && int.TryParse(f.Value, out var fv))
                Fetch = fv;
        }
    }
}
