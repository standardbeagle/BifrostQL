using BifrostQL.Core.QueryModel;

namespace BifrostQL.Ngsql;

/// <summary>
/// PostgreSQL dialect implementation.
/// Uses double-quote identifiers ("name"), LIMIT/OFFSET pagination,
/// '||' for string concatenation, and lastval() for last inserted identity.
/// </summary>
public sealed class PostgresDialect : StandardConcatDialectBase
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly PostgresDialect Instance = new();

    public PostgresDialect() : base('"', "lastval()", " RETURNING id AS ID")
    {
    }
}
