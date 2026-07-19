using BifrostQL.Core.QueryModel;
using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using BifrostQL.SqlServer;

namespace BifrostQL.Testing;

/// <summary>
/// The canonical set of shipped <see cref="ISqlDialect"/> implementations and the
/// xUnit <c>MemberData</c> projection over them, extracted so that every
/// cross-dialect test — in Core and in downstream module/dialect packages —
/// exercises the interface contract against the SAME four dialects from one place,
/// instead of each test class re-declaring the list.
/// </summary>
public static class DialectFixtures
{
    /// <summary>Every shipped dialect, one singleton instance each.</summary>
    public static readonly ISqlDialect[] AllDialects =
    {
        SqlServerDialect.Instance,
        PostgresDialect.Instance,
        MySqlDialect.Instance,
        SqliteDialect.Instance,
    };

    /// <summary>
    /// <see cref="AllDialects"/> projected as xUnit <c>[MemberData]</c> rows
    /// (one dialect per theory invocation).
    /// </summary>
    public static IEnumerable<object[]> AllDialectData =>
        AllDialects.Select(d => new object[] { d });
}
