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

    // `RETURNING id AS ID` assumes every table's identity column is literally
    // called `id`, which is wrong for the common `<table>_id` Postgres
    // convention. Drop the appended RETURNING clause and let the resolver
    // fall back to `SELECT lastval() ID`, which returns the sequence value
    // produced by the most recent INSERT in this session.
    public PostgresDialect() : base('"', "lastval()", null)
    {
    }
}
