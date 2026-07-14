using System.Text.RegularExpressions;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// A recognized simple query ready to run: the programmatic <see cref="QueryIntent"/>
    /// to execute through the read seam, and the ordered output columns to build the
    /// RowDescription and to project each result row into a DataRow.
    /// </summary>
    internal sealed class PgQueryPlan
    {
        public required QueryIntent Intent { get; init; }
        public required IReadOnlyList<PgResultColumn> Columns { get; init; }
    }

    /// <summary>
    /// A simple query string the translator could not turn into a runnable plan (not a
    /// recognized SELECT, an unknown table, an unknown column). It is a query-phase error,
    /// NOT a wire-protocol violation: the connection handler answers it with a non-fatal
    /// <c>ERROR</c> ErrorResponse and keeps the session alive, so it deliberately does NOT
    /// derive from <see cref="PgProtocolException"/> (that base tears the connection down).
    /// </summary>
    internal sealed class PgQueryTranslationException : Exception
    {
        public PgQueryTranslationException(string message) : base(message) { }
    }

    /// <summary>
    /// Turns a PostgreSQL simple query string into a runnable <see cref="PgQueryPlan"/>
    /// against a Bifrost endpoint. Kept behind an interface so the protocol/encoding code
    /// in <see cref="PgConnectionHandler"/> depends only on this seam.
    /// </summary>
    internal interface IPgQueryTranslator
    {
        Task<PgQueryPlan> TranslateAsync(
            IQueryIntentExecutor executor,
            string sql,
            IDictionary<string, object?> userContext,
            string? endpoint,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// SLICE 3 replaces this with the full SQL-subset parser (SELECT + WHERE / ORDER BY /
    /// LIMIT / OFFSET + joins → <see cref="GqlObjectQuery"/>). This slice-2 implementation
    /// is the THINNEST recognizer that proves the encoding + round-trip path end to end:
    /// <c>SELECT &lt;*|col,col&gt; FROM &lt;table&gt;</c> against a single table, no
    /// predicates. Anything richer (WHERE/ORDER/JOIN/LIMIT) fails as a syntax error — it is
    /// slice 3's job, not a silent partial execution. Swapping slice 3 in is a one-line DI
    /// change in <c>AddBifrostPgwire</c>; the protocol loop and result encoding do not move.
    /// </summary>
    internal sealed class PgSimpleQueryTranslator : IPgQueryTranslator
    {
        // SELECT <cols> FROM <table>, optional trailing ';'. The table token stops at the
        // first whitespace/';', so a trailing WHERE/ORDER/JOIN/LIMIT leaves unmatched tail
        // text and the whole match fails — exactly the slice-3 boundary we want to reject.
        private static readonly Regex SelectPattern = new(
            @"^\s*SELECT\s+(?<cols>.+?)\s+FROM\s+(?<table>[^\s;]+)\s*;?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        public async Task<PgQueryPlan> TranslateAsync(
            IQueryIntentExecutor executor,
            string sql,
            IDictionary<string, object?> userContext,
            string? endpoint,
            CancellationToken cancellationToken)
        {
            var match = SelectPattern.Match(sql ?? "");
            if (!match.Success)
                throw new PgQueryTranslationException(
                    "pgwire slice 2 supports only 'SELECT <columns> FROM <table>' (no WHERE/ORDER BY/JOIN/LIMIT yet).");

            var model = await executor.GetModelAsync(endpoint);
            var table = ResolveTable(model, Unquote(match.Groups["table"].Value));
            var columns = ResolveColumns(table, match.Groups["cols"].Value);

            var query = new GqlObjectQuery
            {
                DbTable = table,
                SchemaName = table.TableSchema,
                TableName = table.DbName,
                GraphQlName = table.GraphQlName,
                Path = table.GraphQlName,
            };
            foreach (var column in columns)
                query.ScalarColumns.Add(new GqlObjectColumn(column.DbName));

            return new PgQueryPlan
            {
                Intent = new QueryIntent
                {
                    Query = query,
                    UserContext = userContext,
                    Endpoint = endpoint,
                },
                // Row dictionaries are keyed by DB column name, so the RowDescription field
                // name and the DataRow projection both key off DbName.
                Columns = columns.Select(c => new PgResultColumn(c.DbName, c.DataType)).ToList(),
            };
        }

        private static IDbTable ResolveTable(IDbModel model, string name)
        {
            var table = model.Tables.FirstOrDefault(t =>
                string.Equals(t.DbName, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.GraphQlName, name, StringComparison.OrdinalIgnoreCase));
            if (table is null)
                throw new PgQueryTranslationException($"relation \"{name}\" does not exist.");
            return table;
        }

        private static IReadOnlyList<ColumnDto> ResolveColumns(IDbTable table, string columnList)
        {
            var trimmed = columnList.Trim();
            if (trimmed == "*")
                return table.Columns.OrderBy(c => c.OrdinalPosition).ToList();

            var result = new List<ColumnDto>();
            foreach (var raw in trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var name = Unquote(raw);
                var column = table.Columns.FirstOrDefault(c =>
                    string.Equals(c.DbName, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(c.GraphQlName, name, StringComparison.OrdinalIgnoreCase));
                if (column is null)
                    throw new PgQueryTranslationException(
                        $"column \"{name}\" does not exist on relation \"{table.DbName}\".");
                result.Add(column);
            }
            if (result.Count == 0)
                throw new PgQueryTranslationException("SELECT requires at least one column.");
            return result;
        }

        private static string Unquote(string token)
        {
            var t = token.Trim();
            return t.Length >= 2 && t[0] == '"' && t[^1] == '"' ? t[1..^1] : t;
        }
    }
}
