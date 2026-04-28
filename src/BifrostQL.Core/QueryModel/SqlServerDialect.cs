namespace BifrostQL.Core.QueryModel;

/// <summary>
/// SQL Server dialect implementation.
/// Uses bracket identifiers ([name]), OFFSET/FETCH NEXT pagination (requires ORDER BY),
/// '+' for string concatenation, and SCOPE_IDENTITY() for last inserted identity.
/// </summary>
public sealed class SqlServerDialect : SqlDialectBase
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly SqlServerDialect Instance = new();

    public SqlServerDialect() : base("[", "]", "+", "SCOPE_IDENTITY()", " OUTPUT INSERTED.id AS ID")
    {
    }

    /// <inheritdoc />
    /// <remarks>
    /// SQL Server requires ORDER BY for OFFSET/FETCH. When no sort columns are provided,
    /// ORDER BY (SELECT NULL) is used as a no-op ordering to satisfy the syntax requirement.
    /// </remarks>
    public override string Pagination(IEnumerable<string>? sortColumns, int? offset, int? limit)
    {
        var orderBy = sortColumns?.Any() == true
            ? " ORDER BY " + string.Join(", ", sortColumns)
            : " ORDER BY (SELECT NULL)";

        orderBy += $" OFFSET {offset ?? 0} ROWS";
        var actualLimit = limit switch { null => 100, -1 => null, _ => limit };
        if (actualLimit.HasValue)
            orderBy += $" FETCH NEXT {actualLimit} ROWS ONLY";
        return orderBy;
    }
}
