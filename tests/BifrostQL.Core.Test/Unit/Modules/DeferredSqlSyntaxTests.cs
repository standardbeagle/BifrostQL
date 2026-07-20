using BifrostQL.Core.QueryModel;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using BifrostQL.SqlServer;
using BifrostQL.Testing;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>Grammar harness for the conditional change-set state transitions used by deferred effects.</summary>
public sealed class DeferredSqlSyntaxTests
{
    public static IEnumerable<object[]> Dialects()
    {
        yield return [SqlServerDialect.Instance, SqlFlavor.SqlServer];
        yield return [PostgresDialect.Instance, SqlFlavor.Postgres];
        yield return [MySqlDialect.Instance, SqlFlavor.MySql];
        yield return [SqliteDialect.Instance, SqlFlavor.Sqlite];
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void ConditionalReleaseTransition_IsValidOnEveryShippedDialect(ISqlDialect dialect, SqlFlavor flavor)
    {
        var changes = dialect.TableReference("dbo", "change_sets");
        var state = dialect.EscapeIdentifier("state");
        var appliedAt = dialect.EscapeIdentifier("applied_at");
        var id = dialect.EscapeIdentifier("id");
        var sql = $"UPDATE {changes} SET {state}=@released, {appliedAt}=@now WHERE {id}=@id AND {state}=@held";

        SqlSyntax.AssertValid(sql, flavor, "deferred release must preserve its conditional held-state claim");
    }
}
