using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlParser.Dialects;

namespace BifrostQL.Testing;

/// <summary>Which engine's grammar to validate generated SQL against.</summary>
public enum SqlFlavor { SqlServer, Postgres, MySql, Sqlite }

/// <summary>
/// Parses generated SQL with a real parser and asserts it is syntactically valid.
/// SQL Server output is checked with Microsoft's ScriptDom (TSql160); Postgres,
/// MySQL, and SQLite output are checked with SqlParserCS using the matching
/// dialect. Routing the actual builder output through a grammar catches
/// structural defects — stray commas, empty projections, unbalanced parens —
/// that string/regex assertions silently miss. Parameter markers (@p0) parse fine.
/// </summary>
public static class SqlSyntax
{
    public static void AssertValid(string sql, string? because = null)
        => AssertValid(sql, SqlFlavor.SqlServer, because);

    public static void AssertValid(string sql, SqlFlavor flavor, string? because = null)
    {
        var ctx = because is null ? "" : $" ({because})";
        if (flavor == SqlFlavor.SqlServer)
        {
            using var reader = new StringReader(sql);
            new TSql160Parser(initialQuotedIdentifiers: true).Parse(reader, out var errors);
            errors.Should().BeEmpty(
                $"generated T-SQL must be valid{ctx}. " +
                $"Errors: {string.Join("; ", errors.Select(e => $"L{e.Line}:{e.Column} {e.Message}"))}\nSQL: {sql}");
            return;
        }

        SqlParser.Dialects.Dialect dialect = flavor switch
        {
            SqlFlavor.Postgres => new PostgreSqlDialect(),
            SqlFlavor.MySql => new MySqlDialect(),
            SqlFlavor.Sqlite => new SQLiteDialect(),
            _ => new GenericDialect(),
        };

        try
        {
            new SqlParser.Parser().ParseSql(sql, dialect);
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException(
                $"generated {flavor} SQL must be valid{ctx}. Parser error: {ex.Message}\nSQL: {sql}");
        }
    }
}
